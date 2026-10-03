using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace GameBackend;

internal static class Shared
{
    public static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static readonly Lazy<AmazonDynamoDBClient> Ddb = new(() => new AmazonDynamoDBClient());

    public static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;

    public static APIGatewayHttpApiV2ProxyResponse HttpJson<T>(T value, HttpStatusCode code = HttpStatusCode.OK)
        => new()
        {
            StatusCode = (int)code,
            Headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
            Body = JsonSerializer.Serialize(value, JsonOpts),
        };

    public static string Segment(string path, int index)
        => Uri.UnescapeDataString(path.Split('/', StringSplitOptions.RemoveEmptyEntries)[index]);
}

/// <summary>HTTP API handler: players, matchmaking queue, matches, leaderboard.</summary>
public sealed class MatchmakingHandler
{
    private readonly string _playersTable = Shared.Env("PLAYERS_TABLE", "players");
    private readonly string _queueTable   = Shared.Env("QUEUE_TABLE", "queue");
    private readonly string _matchesTable = Shared.Env("MATCHES_TABLE", "matches");
    private readonly string _leaderboard  = Shared.Env("LEADERBOARD_TABLE", "leaderboard");

    public async Task<APIGatewayHttpApiV2ProxyResponse> Handle(APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        try
        {
            var (method, path) = (request.RequestContext.Http.Method, request.RawPath);

            if ((method, path) == ("GET", "/leaderboard/top"))
                return await TopScores(request); // the only public route (see api.tf)

            // Everything else sits behind the Cognito JWT authorizer; the player is the
            // token's subject, never an id taken from the path or body.
            var caller = CallerId(request);
            if (caller is null)
                return Shared.HttpJson(new { error = "Unauthorized" }, HttpStatusCode.Unauthorized);

            return (method, path) switch
            {
                ("POST", "/players")                              => await RegisterPlayer(request, caller),
                ("GET", _) when path.StartsWith("/players/")       => await GetPlayer(path),
                ("POST", "/matches/queue")                         => await JoinQueue(request, caller),
                ("GET", _) when path.StartsWith("/matches/queue/") => await PollQueue(path, caller),
                ("POST", _) when path.StartsWith("/leaderboard/")  => await RecordScore(path, request, caller),
                _                                                  => Shared.HttpJson(new { error = "Not found" }, HttpStatusCode.NotFound),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return Shared.HttpJson(new { error = ex.Message }, HttpStatusCode.BadRequest);
        }
        catch (Exception ex)
        {
            context.Logger.LogError("Handler failure: {0}", ex);
            return Shared.HttpJson(new { error = "Internal error" }, HttpStatusCode.InternalServerError);
        }
    }

    private static string? CallerId(APIGatewayHttpApiV2ProxyRequest request)
        => request.RequestContext?.Authorizer?.Jwt?.Claims is { } claims
           && claims.TryGetValue("sub", out var sub) && !string.IsNullOrEmpty(sub)
            ? sub
            : null;

    private static APIGatewayHttpApiV2ProxyResponse Forbidden()
        => Shared.HttpJson(new { error = "You can only act as yourself." }, HttpStatusCode.Forbidden);

    private async Task<APIGatewayHttpApiV2ProxyResponse> RegisterPlayer(APIGatewayHttpApiV2ProxyRequest request, string playerId)
    {
        var body = JsonSerializer.Deserialize<RegisterRequest>(request.Body ?? "", Shared.JsonOpts)
            ?? throw new InvalidOperationException("Body must be { displayName }");
        if (string.IsNullOrWhiteSpace(body.DisplayName) || body.DisplayName.Length > 40)
            throw new InvalidOperationException("displayName must be 1-40 characters");

        var table = Table.LoadTable(Shared.Ddb.Value, _playersTable);
        await table.PutItemAsync(new Document
        {
            ["player_id"] = playerId,
            ["display_name"] = body.DisplayName,
            ["games_played"] = 0,
            ["wins"] = 0,
            ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
        });
        return Shared.HttpJson(new { player_id = playerId, display_name = body.DisplayName });
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> GetPlayer(string path)
    {
        var playerId = Shared.Segment(path, 1);
        var table = Table.LoadTable(Shared.Ddb.Value, _playersTable);
        var doc = await table.GetItemAsync(playerId);
        return doc is null
            ? Shared.HttpJson(new { error = "No player " + playerId }, HttpStatusCode.NotFound)
            : Shared.HttpJson(PlayerView.From(doc), HttpStatusCode.OK);
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> JoinQueue(APIGatewayHttpApiV2ProxyRequest request, string playerId)
    {
        var body = JsonSerializer.Deserialize<JoinQueueRequest>(request.Body ?? "", Shared.JsonOpts)
            ?? throw new InvalidOperationException("Body must be { gameId, teamSize }");
        if (string.IsNullOrWhiteSpace(body.GameId) || body.GameId.Length > 64)
            throw new InvalidOperationException("gameId must be 1-64 characters");
        if (body.TeamSize is < MinTeamSize or > MaxTeamSize)
            throw new InvalidOperationException($"teamSize must be {MinTeamSize}-{MaxTeamSize}");

        var table = Table.LoadTable(Shared.Ddb.Value, _queueTable);
        var now = DateTimeOffset.UtcNow;
        await table.PutItemAsync(new Document
        {
            ["game_id"]   = body.GameId,
            ["player_id"] = playerId,
            ["status"]    = "WAITING",
            ["team_size"] = body.TeamSize,
            ["created_at"] = now.ToUnixTimeSeconds().ToString(),
            ["ttl"]       = now.AddMinutes(10).ToUnixTimeSeconds(), // DynamoDB TTL only honours a Number attribute
        });
        return Shared.HttpJson(new { game_id = body.GameId, player_id = playerId, status = "WAITING" });
    }

    private const int MinTeamSize = 2;
    private const int MaxTeamSize = 10;

    private async Task<APIGatewayHttpApiV2ProxyResponse> PollQueue(string path, string playerId)
    {
        // /matches/queue/{gameId}[/{playerId}] - the optional player segment must be the caller.
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var gameId = Shared.Segment(path, 2);
        if (segments.Length > 3 && Shared.Segment(path, 3) != playerId)
            return Forbidden();

        var queue = Table.LoadTable(Shared.Ddb.Value, _queueTable);
        var slot = await queue.GetItemAsync(gameId, playerId);
        if (slot is null)
            return Shared.HttpJson(new { status = "NOT_QUEUED", game_id = gameId, player_id = playerId });

        // Already matched?
        if (slot["status"].AsString() == "READY" && slot.Contains("match_id"))
            return await MatchView(slot["match_id"].AsString());

        // Lobby full yet? Only unexpired WAITING slots for the same team size count;
        // READY slots from earlier matches stay in the partition until their TTL.
        var teamSize = slot["team_size"].AsInt();
        var now = DateTimeOffset.UtcNow;
        var waiting = await queue.Query(new QueryOperationConfig
        {
            KeyExpression = new Expression
            {
                ExpressionStatement = "game_id = :g",
                ExpressionAttributeValues = new Dictionary<string, DynamoDBEntry> { [":g"] = gameId },
            },
            FilterExpression = new Expression
            {
                ExpressionStatement = "#st = :w AND team_size = :n AND #t > :now",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#st"] = "status", ["#t"] = "ttl" },
                ExpressionAttributeValues = new Dictionary<string, DynamoDBEntry>
                {
                    [":w"] = "WAITING",
                    [":n"] = teamSize,
                    [":now"] = now.ToUnixTimeSeconds(),
                },
            },
            Select = SelectValues.AllAttributes,
        }).GetRemainingAsync();

        if (waiting.Count < teamSize)
            return Shared.HttpJson(new { status = "WAITING", game_id = gameId, player_id = playerId, queued = waiting.Count, needed = teamSize });

        // Full: take exactly teamSize players - the caller plus the longest-waiting others.
        var players = waiting
            .Select(d => (Id: d["player_id"].AsString(), CreatedAt: d.Contains("created_at") ? d["created_at"].AsString() : ""))
            .Where(p => p.Id != playerId)
            .OrderBy(p => p.CreatedAt, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal)
            .Take(teamSize - 1)
            .Select(p => p.Id)
            .Append(playerId)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        // Claim every slot and create the match in one transaction. Each slot update is
        // conditional on status = WAITING, so two concurrent polls cannot both claim a
        // player: the loser's transaction is cancelled and it simply reports its state.
        var matchId = Guid.NewGuid().ToString("N");
        var claim = new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = _matchesTable,
                        Item = new Dictionary<string, AttributeValue>
                        {
                            ["match_id"]   = new() { S = matchId },
                            ["game_id"]    = new() { S = gameId },
                            ["status"]     = new() { S = "ACTIVE" },
                            ["players"]    = new() { SS = players },
                            ["created_at"] = new() { S = now.ToUnixTimeSeconds().ToString() },
                        },
                        ConditionExpression = "attribute_not_exists(match_id)",
                    },
                },
                .. players.Select(p => new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = _queueTable,
                        Key = new Dictionary<string, AttributeValue>
                        {
                            ["game_id"]   = new() { S = gameId },
                            ["player_id"] = new() { S = p },
                        },
                        UpdateExpression = "SET #st = :r, match_id = :m, #t = :t",
                        ConditionExpression = "#st = :w",
                        ExpressionAttributeNames = new Dictionary<string, string> { ["#st"] = "status", ["#t"] = "ttl" },
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":r"] = new() { S = "READY" },
                            [":w"] = new() { S = "WAITING" },
                            [":m"] = new() { S = matchId },
                            [":t"] = new() { N = now.AddHours(1).ToUnixTimeSeconds().ToString() }, // TTL must be a Number
                        },
                    },
                }),
            ],
        };

        try
        {
            await Shared.Ddb.Value.TransactWriteItemsAsync(claim);
        }
        catch (TransactionCanceledException)
        {
            // Another poll claimed at least one of these players first.
            var fresh = await queue.GetItemAsync(gameId, playerId);
            if (fresh is not null && fresh["status"].AsString() == "READY" && fresh.Contains("match_id"))
                return await MatchView(fresh["match_id"].AsString());
            return Shared.HttpJson(new { status = "WAITING", game_id = gameId, player_id = playerId, needed = teamSize });
        }

        return await MatchView(matchId);
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> MatchView(string matchId)
    {
        var table = Table.LoadTable(Shared.Ddb.Value, _matchesTable);
        var doc = await table.GetItemAsync(matchId);
        if (doc is null) return Shared.HttpJson(new { error = "No match " + matchId }, HttpStatusCode.NotFound);
        var players = doc.Contains("players") && doc["players"] is PrimitiveList pl
            ? pl.Entries.Select(e => e.AsString()).ToList()
            : new List<string>();
        return Shared.HttpJson(new MatchView(matchId, doc["game_id"].AsString(), doc["status"].AsString(), players));
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> RecordScore(string path, APIGatewayHttpApiV2ProxyRequest request, string caller)
    {
        // /leaderboard/{playerId}/score - players can only post their own score. The score
        // itself is still client-reported; a real game would compute it server-side.
        var playerId = Shared.Segment(path, 1);
        if (playerId != caller)
            return Forbidden();
        var body = JsonSerializer.Deserialize<ScoreRequest>(request.Body ?? "", Shared.JsonOpts)
            ?? throw new InvalidOperationException("Body must be { score }");
        if (body.Score < 0)
            throw new InvalidOperationException("score must not be negative");

        try
        {
            await Shared.Ddb.Value.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _leaderboard,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["player_id"] = new() { S = playerId },
                },
                UpdateExpression = "SET #s = :s, #b = :b, #u = :u ADD games_played :one",
                ConditionExpression = "attribute_not_exists(#s) OR #s < :s",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#s"] = "score", ["#b"] = "board", ["#u"] = "updated_at",
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":s"] = new() { N = body.Score.ToString() },
                    [":b"] = new() { S = "global" },
                    [":u"] = new() { N = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() },
                    [":one"] = new() { N = "1" },
                },
            });
        }
        catch (ConditionalCheckFailedException)
        {
            // Not a new personal best; the stored score stays.
        }

        return Shared.HttpJson(new { player_id = playerId, score = body.Score, board = "global" });
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> TopScores(APIGatewayHttpApiV2ProxyRequest request)
    {
        var limit = request.QueryStringParameters?.TryGetValue("limit", out var l) == true && int.TryParse(l, out var n)
            ? Math.Clamp(n, 1, 100)
            : 30;

        var table = Table.LoadTable(Shared.Ddb.Value, _leaderboard);
        var top = await table.Query(new QueryOperationConfig
        {
            IndexName       = "board-score-index",
            Limit           = limit,
            BackwardSearch  = true,
            KeyExpression   = new Expression
            {
                ExpressionStatement = "board = :b",
                ExpressionAttributeValues = new Dictionary<string, DynamoDBEntry> { [":b"] = "global" },
            },
        }).GetRemainingAsync();

        return Shared.HttpJson(top.Select(d => new ScoreRow(d["player_id"].AsString(), d.Contains("score") ? d["score"].AsInt() : 0)).ToList());
    }
}

public sealed record PlayerView(string player_id, string? display_name, int games_played, int wins)
{
    public static PlayerView From(Amazon.DynamoDBv2.DocumentModel.Document d) => new(
        d["player_id"].AsString(),
        d.Contains("display_name") ? d["display_name"].AsString() : null,
        d.Contains("games_played") ? d["games_played"].AsInt() : 0,
        d.Contains("wins") ? d["wins"].AsInt() : 0);
}

public sealed record MatchView(string match_id, string game_id, string status, IReadOnlyList<string> players);
public sealed record ScoreRow(string player_id, int score);
public sealed record RegisterRequest(string DisplayName);
public sealed record JoinQueueRequest(string GameId, int TeamSize);
public sealed record ScoreRequest(int Score);