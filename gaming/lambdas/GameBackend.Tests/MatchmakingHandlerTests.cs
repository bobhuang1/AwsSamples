using System.Net;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using NSubstitute;
using Xunit;
using static GameBackend.Tests.TestSupport;

namespace GameBackend.Tests;

public class MatchmakingHandlerTests
{
    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly MatchmakingHandler _handler;

    public MatchmakingHandlerTests() => _handler = new MatchmakingHandler(_ddb, leaderboardShards: 4);

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    private void GivenMatch(string status, params string[] players)
        => _ddb.GetItemAsync(Arg.Is<GetItemRequest>(r => r.TableName == "matches"), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse
            {
                Item = new Dictionary<string, AttributeValue>
                {
                    ["match_id"] = S("m1"), ["game_id"] = S("duel"), ["status"] = S(status),
                    ["players"] = new AttributeValue { SS = players.ToList() },
                },
            });

    private const string TwoPlayerResult =
        """{"results":[{"playerId":"alice","kills":3,"deaths":1,"objectives":0,"won":true},{"playerId":"bob","kills":1,"deaths":3,"objectives":0,"won":false}]}""";

    // ---------------- match results ----------------

    [Fact]
    public async Task A_result_from_a_player_token_is_refused_and_writes_nothing()
    {
        var res = await _handler.Handle(Http("POST", "/matches/m1/result", TwoPlayerResult, sub: "alice"), Context());

        Assert.Equal(403, res.StatusCode);
        await _ddb.DidNotReceive().TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_player_cannot_post_a_score_any_more()
    {
        var res = await _handler.Handle(Http("POST", "/leaderboard/alice/score", """{"score":2147483647}""", sub: "alice"), Context());

        Assert.Equal(404, res.StatusCode);
        await _ddb.DidNotReceiveWithAnyArgs().UpdateItemAsync(default!, default);
        await _ddb.DidNotReceiveWithAnyArgs().TransactWriteItemsAsync(default!, default);
    }

    [Fact]
    public async Task The_server_computes_the_points_and_closes_the_match_in_one_transaction()
    {
        GivenMatch("ACTIVE", "alice", "bob");
        TransactWriteItemsRequest? sent = null;
        _ddb.TransactWriteItemsAsync(Arg.Do<TransactWriteItemsRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new TransactWriteItemsResponse());

        var res = await _handler.Handle(Http("POST", "/matches/m1/result", TwoPlayerResult, iam: true), Context());

        Assert.Equal(200, res.StatusCode);
        Assert.NotNull(sent);
        Assert.Equal(5, sent!.TransactItems.Count); // the match + two leaderboard rows + two player profiles

        var matchUpdate = sent.TransactItems[0].Update;
        Assert.Equal("matches", matchUpdate.TableName);
        Assert.Equal("#st = :active", matchUpdate.ConditionExpression); // only an ACTIVE match can be closed

        var alice = sent.TransactItems.Select(i => i.Update).Single(u => u.TableName == "leaderboard" && u.Key["player_id"].S == "alice");
        Assert.Equal((3 * 100 + 500 - 25).ToString(), alice.ExpressionAttributeValues[":pts"].N); // 775
        Assert.Equal(LeaderboardShards.KeyFor("alice", 4), alice.ExpressionAttributeValues[":b"].S);
        Assert.Contains("ADD #s :pts", alice.UpdateExpression);

        var bob = sent.TransactItems.Select(i => i.Update).Single(u => u.TableName == "leaderboard" && u.Key["player_id"].S == "bob");
        Assert.Equal("25", bob.ExpressionAttributeValues[":pts"].N); // 1 kill, 3 deaths: 100 - 75

        var scores = Json(res.Body).GetProperty("scores");
        Assert.Equal(2, scores.GetArrayLength());
    }

    [Fact]
    public async Task The_result_updates_games_played_and_wins_on_the_player_profiles()
    {
        GivenMatch("ACTIVE", "alice", "bob");
        TransactWriteItemsRequest? sent = null;
        _ddb.TransactWriteItemsAsync(Arg.Do<TransactWriteItemsRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new TransactWriteItemsResponse());

        await _handler.Handle(Http("POST", "/matches/m1/result", TwoPlayerResult, iam: true), Context());

        var profiles = sent!.TransactItems.Select(i => i.Update).Where(u => u.TableName == "players").ToDictionary(u => u.Key["player_id"].S);
        Assert.Equal(2, profiles.Count);
        Assert.Equal("ADD games_played :one, wins :w", profiles["alice"].UpdateExpression);
        Assert.Equal("1", profiles["alice"].ExpressionAttributeValues[":w"].N); // winner
        Assert.Equal("0", profiles["bob"].ExpressionAttributeValues[":w"].N);
        Assert.Equal("1", profiles["bob"].ExpressionAttributeValues[":one"].N);
    }

    [Fact]
    public async Task A_null_entry_in_results_is_a_400_not_a_500()
    {
        GivenMatch("ACTIVE", "alice", "bob");

        var res = await _handler.Handle(Http("POST", "/matches/m1/result", """{"results":[null]}""", iam: true), Context());

        Assert.Equal(400, res.StatusCode);
        await _ddb.DidNotReceiveWithAnyArgs().TransactWriteItemsAsync(default!, default);
    }

    [Fact]
    public async Task A_player_who_was_not_in_the_match_gets_no_score()
    {
        GivenMatch("ACTIVE", "alice", "bob");
        const string body = """{"results":[{"playerId":"mallory","kills":50,"deaths":0,"objectives":20,"won":true}]}""";

        var res = await _handler.Handle(Http("POST", "/matches/m1/result", body, iam: true), Context());

        Assert.Equal(400, res.StatusCode);
        await _ddb.DidNotReceiveWithAnyArgs().TransactWriteItemsAsync(default!, default);
    }

    [Fact]
    public async Task Out_of_bounds_stats_are_rejected()
    {
        GivenMatch("ACTIVE", "alice", "bob");
        const string body = """{"results":[{"playerId":"alice","kills":99999,"deaths":0,"objectives":0,"won":true}]}""";

        var res = await _handler.Handle(Http("POST", "/matches/m1/result", body, iam: true), Context());

        Assert.Equal(400, res.StatusCode);
        await _ddb.DidNotReceiveWithAnyArgs().TransactWriteItemsAsync(default!, default);
    }

    [Fact]
    public async Task A_match_that_is_already_scored_is_refused()
    {
        GivenMatch("COMPLETED", "alice", "bob");

        var res = await _handler.Handle(Http("POST", "/matches/m1/result", TwoPlayerResult, iam: true), Context());

        Assert.Equal(409, res.StatusCode);
        await _ddb.DidNotReceiveWithAnyArgs().TransactWriteItemsAsync(default!, default);
    }

    [Fact]
    public async Task Two_racing_reports_score_the_match_once()
    {
        GivenMatch("ACTIVE", "alice", "bob");
        _ddb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .Returns<TransactWriteItemsResponse>(_ => throw Cancelled("ConditionalCheckFailed", "None", "None"));

        var res = await _handler.Handle(Http("POST", "/matches/m1/result", TwoPlayerResult, iam: true), Context());

        Assert.Equal(409, res.StatusCode);
    }

    [Theory]
    [InlineData("None", "TransactionConflict", "None")]
    [InlineData("ThrottlingError", "None", "None")]
    [InlineData("None", "ConditionalCheckFailed", "None")] // a leaderboard row failing is not "already scored"
    public async Task A_transient_cancellation_is_a_503_the_game_server_can_retry(string first, string second, string third)
    {
        GivenMatch("ACTIVE", "alice", "bob");
        _ddb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .Returns<TransactWriteItemsResponse>(_ => throw Cancelled(first, second, third));

        var res = await _handler.Handle(Http("POST", "/matches/m1/result", TwoPlayerResult, iam: true), Context());

        Assert.Equal(503, res.StatusCode);
        Assert.True(res.Headers.ContainsKey("retry-after"));
    }

    private static TransactionCanceledException Cancelled(params string[] codes)
        => new("cancelled") { CancellationReasons = codes.Select(c => new CancellationReason { Code = c }).ToList() };

    [Fact]
    public async Task An_unknown_match_is_a_404()
    {
        _ddb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>()).Returns(new GetItemResponse());

        var res = await _handler.Handle(Http("POST", "/matches/nope/result", TwoPlayerResult, iam: true), Context());

        Assert.Equal(404, res.StatusCode);
    }

    // ---------------- leaderboard ----------------

    [Fact]
    public async Task Top_scores_read_every_shard_and_merge()
    {
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var board = call.Arg<QueryRequest>().ExpressionAttributeValues[":b"].S;
            var rows = board switch
            {
                "global#0" => new[] { ("p1", 900L), ("p2", 100L) },
                "global#1" => new[] { ("p3", 950L) },
                "global#2" => new[] { ("p4", 500L), ("p5", 400L) },
                _ => Array.Empty<(string, long)>(),
            };
            return new QueryResponse
            {
                Items = rows.Select(r => new Dictionary<string, AttributeValue> { ["player_id"] = S(r.Item1), ["score"] = N(r.Item2) }).ToList(),
            };
        });

        var res = await _handler.Handle(Http("GET", "/leaderboard/top", query: new() { ["limit"] = "3" }), Context());

        Assert.Equal(200, res.StatusCode);
        Assert.Equal("public, max-age=10", res.Headers["cache-control"]);
        var top = Json(res.Body).EnumerateArray().Select(e => e.GetProperty("player_id").GetString()).ToList();
        Assert.Equal(["p3", "p1", "p4"], top);
        await _ddb.Received(4).QueryAsync(Arg.Is<QueryRequest>(q => q.IndexName == "board-score-index" && q.Limit == 3 && !q.ScanIndexForward), Arg.Any<CancellationToken>());
    }

    // ---------------- matchmaking ----------------

    private static Dictionary<string, AttributeValue> Slot(string status, int teamSize, long joined, string? matchId = null)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["game_id"] = S("duel"), ["player_id"] = S("alice"), ["status"] = S(status),
            ["team_size"] = N(teamSize), ["lobby_ts"] = N(joined),
        };
        if (matchId is not null) item["match_id"] = S(matchId);
        return item;
    }

    private void GivenSlot(Dictionary<string, AttributeValue> slot)
        => _ddb.GetItemAsync(Arg.Is<GetItemRequest>(r => r.TableName == "queue"), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse { Item = slot });

    [Fact]
    public async Task Polling_reads_only_the_sparse_lobby_index_and_never_the_whole_partition()
    {
        GivenSlot(Slot("WAITING", 2, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        QueryRequest? query = null;
        _ddb.QueryAsync(Arg.Do<QueryRequest>(q => query = q), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [new() { ["player_id"] = S("alice") }] });

        var res = await _handler.Handle(Http("GET", "/matches/queue/duel", sub: "alice"), Context());

        Assert.Equal(200, res.StatusCode);
        Assert.Equal("lobby-index", query!.IndexName);
        Assert.Equal("queue", query.TableName);
        Assert.Equal(2, query.Limit); // team size, not the partition size
        Assert.Equal("duel#2", query.ExpressionAttributeValues[":l"].S);
        Assert.Equal("WAITING", Json(res.Body).GetProperty("status").GetString());
        await _ddb.DidNotReceiveWithAnyArgs().TransactWriteItemsAsync(default!, default);
    }

    [Fact]
    public async Task A_waiting_poll_tells_the_client_when_to_come_back()
    {
        GivenSlot(Slot("WAITING", 2, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 45));
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(new QueryResponse { Items = [] });

        var res = await _handler.Handle(Http("GET", "/matches/queue/duel", sub: "alice"), Context());

        Assert.Equal("6", res.Headers["retry-after"]); // 45 s waited: 2 + 45/10
        Assert.Equal(6000, Json(res.Body).GetProperty("next_poll_ms").GetInt32());
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(9, 2)]
    [InlineData(10, 3)]
    [InlineData(45, 6)]
    [InlineData(600, 10)] // capped
    public void Poll_interval_backs_off_with_the_time_waited(long waited, int expectedSeconds)
        => Assert.Equal(expectedSeconds, MatchmakingHandler.NextPollSeconds(waited));

    [Fact]
    public async Task A_full_lobby_is_claimed_in_one_transaction_that_removes_the_index_keys()
    {
        GivenSlot(Slot("WAITING", 2, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5));
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [new() { ["player_id"] = S("bob") }, new() { ["player_id"] = S("alice") }] });
        TransactWriteItemsRequest? sent = null;
        _ddb.TransactWriteItemsAsync(Arg.Do<TransactWriteItemsRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new TransactWriteItemsResponse());
        GivenMatch("ACTIVE", "alice", "bob");

        var res = await _handler.Handle(Http("GET", "/matches/queue/duel", sub: "alice"), Context());

        Assert.Equal(3, sent!.TransactItems.Count); // the match + two slots
        var slotUpdates = sent.TransactItems.Where(i => i.Update is not null).Select(i => i.Update).ToList();
        Assert.All(slotUpdates, u =>
        {
            Assert.Equal("#st = :w", u.ConditionExpression);
            Assert.Contains("REMOVE lobby, lobby_ts", u.UpdateExpression);
        });
        Assert.Equal("ACTIVE", Json(res.Body).GetProperty("status").GetString());
        // The new match is read back with a consistent read: an eventually consistent one can miss it.
        await _ddb.Received().GetItemAsync(Arg.Is<GetItemRequest>(r => r.TableName == "matches" && r.ConsistentRead == true), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_player_cannot_poll_for_somebody_else()
    {
        var res = await _handler.Handle(Http("GET", "/matches/queue/duel/bob", sub: "alice"), Context());

        Assert.Equal(403, res.StatusCode);
    }

    [Fact]
    public async Task Joining_writes_the_sparse_index_keys()
    {
        PutItemRequest? put = null;
        _ddb.PutItemAsync(Arg.Do<PutItemRequest>(r => put = r), Arg.Any<CancellationToken>()).Returns(new PutItemResponse());

        var res = await _handler.Handle(Http("POST", "/matches/queue", """{"gameId":"duel","teamSize":2}""", sub: "alice"), Context());

        Assert.Equal(200, res.StatusCode);
        Assert.Equal("duel#2", put!.Item["lobby"].S);
        Assert.True(put.Item.ContainsKey("lobby_ts"));
        Assert.Equal("2", res.Headers["retry-after"]);
    }

    [Fact]
    public async Task Bad_input_is_a_400_not_a_500()
    {
        var res = await _handler.Handle(Http("POST", "/matches/queue", "not json", sub: "alice"), Context());
        Assert.Equal((int)HttpStatusCode.BadRequest, res.StatusCode);

        var res2 = await _handler.Handle(Http("POST", "/matches/queue", """{"gameId":"duel","teamSize":99}""", sub: "alice"), Context());
        Assert.Equal(400, res2.StatusCode);
    }

    [Fact]
    public async Task Routes_other_than_the_leaderboard_and_results_need_a_token()
    {
        var res = await _handler.Handle(Http("POST", "/players", """{"displayName":"A"}"""), Context());

        Assert.Equal(401, res.StatusCode);
    }
}
