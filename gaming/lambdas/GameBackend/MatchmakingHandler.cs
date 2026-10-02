using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
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

            return (method, path) switch
            {
                ("POST", "/players")                              => await RegisterPlayer(request),
                ("GET", _) when path.StartsWith("/players/")       => await GetPlayer(path),
                ("POST", "/matches/queue")                         => await JoinQueue(request),
                ("GET", _) when path.StartsWith("/matches/queue/") => await PollQueue(path),
                ("POST", _) when path.StartsWith("/leaderboard/")  => await RecordScore(path, request),
                ("GET", "/leaderboard/top")                        => await TopScores(request),
                _                                                  => Shared.HttpJson(new { error = "Not found" }, HttpStatusCode.NotFound),
            };
        }
        catch (Exception ex)
        {
            context.Logger.LogError("Handler failure: {0}", ex);
            return Shared.HttpJson(new { error = ex.Message }, HttpStatusCode.InternalServerError);
        }
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> RegisterPlayer(APIGatewayHttpApiV2ProxyRequest request)
    {
        var body = JsonSerializer.Deserialize<RegisterRequest>(request.Body, Shared.JsonOpts)
            ?? throw new InvalidOperationException("Body must be { playerId, displayName }");

        var table = Table.LoadTable(Shared.Ddb.Value, _playersTable);
        await table.PutItemAsync(new Document
        {
            ["player_id"] = body.PlayerId,
            ["display_name"] = body.DisplayName,
            ["games_played"] = 0,
            ["wins"] = 0,
            ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
        });
        return Shared.HttpJson(new { player_id = body.PlayerId, display_name = body.DisplayName });
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

    private async Task<APIGatewayHttpApiV2ProxyResponse> JoinQueue(APIGatewayHttpApiV2ProxyRequest request)
    {
        var body = JsonSerializer.Deserialize<JoinQueueRequest>(request.Body, Shared.JsonOpts)
            ?? throw new InvalidOperationException("Body must be { playerId, gameId, teamSize }");

        var table = Table.LoadTable(Shared.Ddb.Value, _queueTable);
        var now = DateTimeOffset.UtcNow;
        await table.PutItemAsync(new Document
        {
            ["game_id"]   = body.GameId,
            ["player_id"] = body.PlayerId,
            ["status"]    = "WAITING",
            ["team_size"] = body.TeamSize,
            ["created_at"] = now.ToUnixTimeSeconds().ToString(),
            ["ttl"]       = now.AddMinutes(10).ToUnixTimeSeconds(), // DynamoDB TTL only honours a Number attribute
        });
        return Shared.HttpJson(new { game_id = body.GameId, player_id = body.PlayerId, status = "WAITING" });
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> PollQueue(string path)
    {
        // /matches/queue/{gameId}/{playerId}
        var gameId = Shared.Segment(path, 2);
        var playerId = Shared.Segment(path, 3);
        var queue = Table.LoadTable(Shared.Ddb.Value, _queueTable);

        var slot = await queue.GetItemAsync(gameId, playerId);
        if (slot is null)
            return Shared.HttpJson(new { status = "NOT_QUEUED", game_id = gameId, player_id = playerId });

        // Already matched?
        if (slot["status"].AsString() == "READY" && slot.Contains("match_id"))
            return await MatchView(slot["match_id"].AsString());

        // Lobby full yet? Query the whole game partition and count.
        var search = await queue.Query(new QueryOperationConfig
        {
            IndexName = null,
            KeyExpression = new Expression
            {
                ExpressionStatement = "game_id = :g",
                ExpressionAttributeValues = new Dictionary<string, DynamoDBEntry> { [":g"] = gameId },
            },
            Limit  = 100,
            Select = SelectValues.AllAttributes,
        }).GetRemainingAsync();

        if (search.Count < slot["team_size"].AsInt())
            return Shared.HttpJson(new { status = "WAITING", game_id = gameId, player_id = playerId, queued = search.Count, needed = slot["team_size"].AsInt() });

        // Full: create the match and mark every slot READY with the same match id.
        var matchId = Guid.NewGuid().ToString("N");
        var players = search.Select(d => d["player_id"].AsString()).OrderBy(p => p).ToList();
        var now = DateTimeOffset.UtcNow;

        var matches = Table.LoadTable(Shared.Ddb.Value, _matchesTable);
        await matches.PutItemAsync(new Document
        {
            ["match_id"]   = matchId,
            ["game_id"]    = gameId,
            ["status"]     = "ACTIVE",
            ["players"]    = players,
            ["created_at"] = now.ToUnixTimeSeconds().ToString(),
        });

        var batch = queue.CreateBatchWrite();
        foreach (var d in search)
            batch.AddDocumentToPut(new Document
            {
                ["game_id"]   = gameId,
                ["player_id"] = d["player_id"].AsString(),
                ["status"]    = "READY",
                ["match_id"]  = matchId,
                ["team_size"] = slot["team_size"].AsInt(),
                ["created_at"] = d["created_at"].AsString(),
                ["ttl"]       = now.AddHours(1).ToUnixTimeSeconds(), // DynamoDB TTL only honours a Number attribute
            });
        await batch.ExecuteAsync();

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

    private async Task<APIGatewayHttpApiV2ProxyResponse> RecordScore(string path, APIGatewayHttpApiV2ProxyRequest request)
    {
        // /leaderboard/{playerId}/score
        var playerId = Shared.Segment(path, 1);
        var body = JsonSerializer.Deserialize<ScoreRequest>(request.Body, Shared.JsonOpts)
            ?? throw new InvalidOperationException("Body must be { score }");

        await Shared.Ddb.Value.UpdateItemAsync(new Amazon.DynamoDBv2.Model.UpdateItemRequest
        {
            TableName = _leaderboard,
            Key = new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>
            {
                ["player_id"] = new() { S = playerId },
            },
            UpdateExpression = "SET #s = :s, #b = :b, #u = :u ADD games_played :one",
            ConditionExpression = "attribute_not_exists(#s) OR #s < :s",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#s"] = "score", ["#b"] = "board", ["#u"] = "updated_at",
            },
            ExpressionAttributeValues = new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>
            {
                [":s"] = new() { N = body.Score.ToString() },
                [":b"] = new() { S = "global" },
                [":u"] = new() { N = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() },
                [":one"] = new() { N = "1" },
            },
        });

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
public sealed record RegisterRequest(string PlayerId, string DisplayName);
public sealed record JoinQueueRequest(string PlayerId, string GameId, int TeamSize);
public sealed record ScoreRequest(int Score);