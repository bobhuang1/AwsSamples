using System.Net;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("GameBackend.Tests")]

namespace GameBackend;

internal static class Shared
{
    public static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static readonly Lazy<IAmazonDynamoDB> Ddb = new(() => new AmazonDynamoDBClient());

    public static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;

    public static APIGatewayHttpApiV2ProxyResponse HttpJson<T>(T value, HttpStatusCode code = HttpStatusCode.OK, int cacheSeconds = 0,
        IDictionary<string, string>? extraHeaders = null)
    {
        var headers = new Dictionary<string, string> { ["content-type"] = "application/json" };
        if (cacheSeconds > 0) headers["cache-control"] = $"public, max-age={cacheSeconds}";
        if (extraHeaders is not null)
            foreach (var (k, v) in extraHeaders) headers[k] = v;
        return new()
        {
            StatusCode = (int)code,
            Headers = headers,
            Body = JsonSerializer.Serialize(value, JsonOpts),
        };
    }

    /// <summary>
    /// The request path without the API stage. With a named stage (not $default) HTTP APIs
    /// put the stage in front of rawPath (/v1/players); routes are matched without it.
    /// </summary>
    public static string RoutePath(APIGatewayHttpApiV2ProxyRequest request)
    {
        var path = request.RawPath ?? "/";
        var stage = request.RequestContext?.Stage;
        if (!string.IsNullOrEmpty(stage) && stage != "$default")
        {
            var prefix = "/" + stage;
            if (path == prefix) return "/";
            if (path.StartsWith(prefix + "/", StringComparison.Ordinal)) return path[prefix.Length..];
        }
        return path;
    }

    public static string Segment(string path, int index)
        => Uri.UnescapeDataString(path.Split('/', StringSplitOptions.RemoveEmptyEntries)[index]);

    public static AttributeValue S(string value) => new() { S = value };
    public static AttributeValue N(long value) => new() { N = value.ToString() };
    public static Dictionary<string, AttributeValue> Key(string name, string value) => new() { [name] = S(value) };
    public static Dictionary<string, AttributeValue> Key(string n1, string v1, string n2, string v2) => new() { [n1] = S(v1), [n2] = S(v2) };

    public static string? Str(Dictionary<string, AttributeValue> item, string name)
        => item.TryGetValue(name, out var v) ? v.S : null;

    public static long Num(Dictionary<string, AttributeValue> item, string name, long fallback = 0)
        => item.TryGetValue(name, out var v) && long.TryParse(v.N, out var n) ? n : fallback;
}

/// <summary>A client error whose message is written by this code and safe to return.</summary>
internal sealed class BadRequestException(string message) : Exception(message);

/// <summary>HTTP API handler: players, matchmaking queue, matches, match results, leaderboard.</summary>
public sealed class MatchmakingHandler
{
    private readonly IAmazonDynamoDB _ddb;
    private readonly string _playersTable = Shared.Env("PLAYERS_TABLE", "players");
    private readonly string _queueTable   = Shared.Env("QUEUE_TABLE", "queue");
    private readonly string _matchesTable = Shared.Env("MATCHES_TABLE", "matches");
    private readonly string _leaderboard  = Shared.Env("LEADERBOARD_TABLE", "leaderboard");
    private readonly int _shards;

    public const string LobbyIndex = "lobby-index";
    public const string LeaderboardIndex = "board-score-index";

    /// <summary>How long a waiting slot lives (it also bounds which slots a poll looks at).</summary>
    private const int QueueLifetimeSeconds = 600;

    private const int MinTeamSize = 2;
    private const int MaxTeamSize = 10;

    // The Lambda runtime uses this constructor.
    public MatchmakingHandler() : this(Shared.Ddb.Value, LeaderboardShards.CountFromEnv()) { }

    public MatchmakingHandler(IAmazonDynamoDB ddb, int leaderboardShards)
    {
        _ddb = ddb;
        _shards = leaderboardShards;
    }

    public async Task<APIGatewayHttpApiV2ProxyResponse> Handle(APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        try
        {
            var (method, path) = (request.RequestContext.Http.Method, Shared.RoutePath(request));

            if ((method, path) == ("GET", "/leaderboard/top"))
                return await TopScores(request); // public (see api.tf)

            // Reported by the game server, which signs its request with IAM (see api.tf). Players
            // never reach this route: they have no say in a score.
            if (method == "POST" && IsResultPath(path))
                return await RecordMatchResult(request, path);

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
                _                                                  => Shared.HttpJson(new { error = "Not found" }, HttpStatusCode.NotFound),
            };
        }
        catch (BadRequestException ex)
        {
            return Shared.HttpJson(new { error = ex.Message }, HttpStatusCode.BadRequest);
        }
        catch (Exception ex) when (ex is JsonException or IndexOutOfRangeException)
        {
            // Fixed text: parser and indexer messages describe internals.
            return Shared.HttpJson(new { error = "Malformed request" }, HttpStatusCode.BadRequest);
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

    private static bool IsResultPath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 && parts[0] == "matches" && parts[2] == "result" && parts[1] != "queue";
    }

    private static APIGatewayHttpApiV2ProxyResponse Forbidden()
        => Shared.HttpJson(new { error = "You can only act as yourself." }, HttpStatusCode.Forbidden);

    // ---------------- players ----------------

    private async Task<APIGatewayHttpApiV2ProxyResponse> RegisterPlayer(APIGatewayHttpApiV2ProxyRequest request, string playerId)
    {
        var body = JsonSerializer.Deserialize<RegisterRequest>(request.Body ?? "", Shared.JsonOpts)
            ?? throw new BadRequestException("Body must be { displayName }");
        if (string.IsNullOrWhiteSpace(body.DisplayName) || body.DisplayName.Length > 40)
            throw new BadRequestException("displayName must be 1-40 characters");

        // Registering again only renames the player: the counters and created_at of an
        // existing profile are kept.
        await _ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _playersTable,
            Key = Shared.Key("player_id", playerId),
            UpdateExpression = "SET display_name = :n, created_at = if_not_exists(created_at, :c), games_played = if_not_exists(games_played, :z), wins = if_not_exists(wins, :z)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":n"] = Shared.S(body.DisplayName),
                [":c"] = Shared.S(DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
                [":z"] = Shared.N(0),
            },
        });
        return Shared.HttpJson(new { player_id = playerId, display_name = body.DisplayName });
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> GetPlayer(string path)
    {
        var playerId = Shared.Segment(path, 1);
        var res = await _ddb.GetItemAsync(new GetItemRequest { TableName = _playersTable, Key = Shared.Key("player_id", playerId) });
        return res.Item is not { Count: > 0 } item
            ? Shared.HttpJson(new { error = "No player " + playerId }, HttpStatusCode.NotFound)
            : Shared.HttpJson(new PlayerView(
                Shared.Str(item, "player_id") ?? playerId,
                Shared.Str(item, "display_name"),
                (int)Shared.Num(item, "games_played"),
                (int)Shared.Num(item, "wins")));
    }

    // ---------------- matchmaking ----------------

    internal static string LobbyKey(string gameId, int teamSize) => $"{gameId}#{teamSize}";

    /// <summary>
    /// Seconds a client should wait before polling again, from how long it has waited so
    /// far: 2 s at first, growing to 10 s, so a long wait does not keep hitting the API.
    /// </summary>
    internal static int NextPollSeconds(long waitedSeconds)
        => (int)Math.Clamp(2 + waitedSeconds / 10, 2, 10);

    private static APIGatewayHttpApiV2ProxyResponse Waiting(object body, int pollSeconds)
        => Shared.HttpJson(body, HttpStatusCode.OK, extraHeaders: new Dictionary<string, string> { ["retry-after"] = pollSeconds.ToString() });

    private async Task<APIGatewayHttpApiV2ProxyResponse> JoinQueue(APIGatewayHttpApiV2ProxyRequest request, string playerId)
    {
        var body = JsonSerializer.Deserialize<JoinQueueRequest>(request.Body ?? "", Shared.JsonOpts)
            ?? throw new BadRequestException("Body must be { gameId, teamSize }");
        if (string.IsNullOrWhiteSpace(body.GameId) || body.GameId.Length > 64)
            throw new BadRequestException("gameId must be 1-64 characters");
        if (body.TeamSize is < MinTeamSize or > MaxTeamSize)
            throw new BadRequestException($"teamSize must be {MinTeamSize}-{MaxTeamSize}");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await _ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _queueTable,
            Item = new Dictionary<string, AttributeValue>
            {
                ["game_id"]    = Shared.S(body.GameId),
                ["player_id"]  = Shared.S(playerId),
                ["status"]     = Shared.S("WAITING"),
                ["team_size"]  = Shared.N(body.TeamSize),
                ["created_at"] = Shared.S(now.ToString()),
                // Sparse index keys: only WAITING slots carry them, so the lobby index holds
                // exactly the players who are still waiting. lobby_ts is the join time.
                ["lobby"]      = Shared.S(LobbyKey(body.GameId, body.TeamSize)),
                ["lobby_ts"]   = Shared.N(now),
                ["ttl"]        = Shared.N(now + QueueLifetimeSeconds), // DynamoDB TTL only honours a Number attribute
            },
        });
        var poll = NextPollSeconds(0);
        return Waiting(new { game_id = body.GameId, player_id = playerId, status = "WAITING", next_poll_ms = poll * 1000 }, poll);
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> PollQueue(string path, string playerId)
    {
        // /matches/queue/{gameId}[/{playerId}] - the optional player segment must be the caller.
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var gameId = Shared.Segment(path, 2);
        if (segments.Length > 3 && Shared.Segment(path, 3) != playerId)
            return Forbidden();

        var slotRes = await _ddb.GetItemAsync(new GetItemRequest { TableName = _queueTable, Key = Shared.Key("game_id", gameId, "player_id", playerId) });
        if (slotRes.Item is not { Count: > 0 } slot)
            return Shared.HttpJson(new { status = "NOT_QUEUED", game_id = gameId, player_id = playerId });

        // Already matched?
        if (Shared.Str(slot, "status") == "READY" && Shared.Str(slot, "match_id") is { } readyMatch)
            return await MatchView(readyMatch);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var teamSize = (int)Shared.Num(slot, "team_size", MinTeamSize);
        var waited = now - Shared.Num(slot, "lobby_ts", now);
        var poll = NextPollSeconds(waited);

        // Lobby full yet? Ask the sparse lobby index for the longest-waiting players only:
        // at most teamSize items are read per poll, however many players are queued.
        var oldest = await OldestWaiting(LobbyKey(gameId, teamSize), teamSize, now - QueueLifetimeSeconds);

        // The match is the caller plus the longest-waiting others. (The caller is added
        // explicitly: the index is eventually consistent and may not list a fresh slot yet.)
        var players = oldest.Where(p => p != playerId).Take(teamSize - 1).Append(playerId).ToList();
        if (players.Count < teamSize)
            return Waiting(new { status = "WAITING", game_id = gameId, player_id = playerId, queued = players.Count, needed = teamSize, next_poll_ms = poll * 1000 }, poll);
        players.Sort(StringComparer.Ordinal);

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
                            ["match_id"]   = Shared.S(matchId),
                            ["game_id"]    = Shared.S(gameId),
                            ["status"]     = Shared.S("ACTIVE"),
                            ["players"]    = new AttributeValue { SS = players },
                            ["created_at"] = Shared.S(now.ToString()),
                        },
                        ConditionExpression = "attribute_not_exists(match_id)",
                    },
                },
                .. players.Select(p => new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = _queueTable,
                        Key = Shared.Key("game_id", gameId, "player_id", p),
                        // REMOVE the index keys: the slot leaves the sparse lobby index.
                        UpdateExpression = "SET #st = :r, match_id = :m, #t = :t REMOVE lobby, lobby_ts",
                        ConditionExpression = "#st = :w",
                        ExpressionAttributeNames = new Dictionary<string, string> { ["#st"] = "status", ["#t"] = "ttl" },
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":r"] = Shared.S("READY"),
                            [":w"] = Shared.S("WAITING"),
                            [":m"] = Shared.S(matchId),
                            [":t"] = Shared.N(now + 3600), // TTL must be a Number
                        },
                    },
                }),
            ],
        };

        try
        {
            await _ddb.TransactWriteItemsAsync(claim);
        }
        catch (TransactionCanceledException)
        {
            // Another poll claimed at least one of these players first.
            var fresh = await _ddb.GetItemAsync(new GetItemRequest
            {
                TableName = _queueTable,
                Key = Shared.Key("game_id", gameId, "player_id", playerId),
                ConsistentRead = true,
            });
            if (fresh.Item is { Count: > 0 } f && Shared.Str(f, "status") == "READY" && Shared.Str(f, "match_id") is { } m)
                return await MatchView(m);
            return Waiting(new { status = "WAITING", game_id = gameId, player_id = playerId, needed = teamSize, next_poll_ms = poll * 1000 }, poll);
        }

        return await MatchView(matchId);
    }

    /// <summary>The longest-waiting player ids of a lobby, oldest first, from the sparse index.</summary>
    private async Task<List<string>> OldestWaiting(string lobby, int count, long notBefore)
    {
        var ids = new List<string>();
        Dictionary<string, AttributeValue>? last = null;
        do
        {
            var res = await _ddb.QueryAsync(new QueryRequest
            {
                TableName = _queueTable,
                IndexName = LobbyIndex,
                KeyConditionExpression = "lobby = :l AND lobby_ts >= :since", // expired slots fall out of the key range
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":l"] = Shared.S(lobby),
                    [":since"] = Shared.N(notBefore),
                },
                ScanIndexForward = true,
                Limit = count - ids.Count,
                ExclusiveStartKey = last,
            });
            ids.AddRange(res.Items.Select(i => i["player_id"].S));
            last = res.LastEvaluatedKey is { Count: > 0 } ? res.LastEvaluatedKey : null;
        } while (last is not null && ids.Count < count);
        return ids;
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> MatchView(string matchId)
    {
        var res = await _ddb.GetItemAsync(new GetItemRequest { TableName = _matchesTable, Key = Shared.Key("match_id", matchId) });
        if (res.Item is not { Count: > 0 } doc)
            return Shared.HttpJson(new { error = "No match " + matchId }, HttpStatusCode.NotFound);
        return Shared.HttpJson(new MatchView(matchId, Shared.Str(doc, "game_id") ?? "", Shared.Str(doc, "status") ?? "", doc.TryGetValue("players", out var p) ? p.SS : []));
    }

    // ---------------- match results and scores ----------------

    /// <summary>
    /// POST /matches/{matchId}/result, called by the game server. The server reports what
    /// happened (kills, deaths, objectives, win); the points are computed here. The match is
    /// moved ACTIVE -> COMPLETED in the same transaction that adds the points, so a match is
    /// scored once: a player gets at most one score per match, and a retry changes nothing.
    /// </summary>
    private async Task<APIGatewayHttpApiV2ProxyResponse> RecordMatchResult(APIGatewayHttpApiV2ProxyRequest request, string path)
    {
        if (request.RequestContext?.Authorizer?.IAM is null)
            return Shared.HttpJson(new { error = "Only the game server may report results." }, HttpStatusCode.Forbidden);

        var matchId = Shared.Segment(path, 1);
        var body = JsonSerializer.Deserialize<MatchResultRequest>(request.Body ?? "", Shared.JsonOpts)
            ?? throw new BadRequestException("Body must be { results: [...] }");

        var res = await _ddb.GetItemAsync(new GetItemRequest { TableName = _matchesTable, Key = Shared.Key("match_id", matchId), ConsistentRead = true });
        if (res.Item is not { Count: > 0 } match)
            return Shared.HttpJson(new { error = "No match " + matchId }, HttpStatusCode.NotFound);
        if (Shared.Str(match, "status") != "ACTIVE")
            return Shared.HttpJson(new { error = "This match result was already recorded." }, HttpStatusCode.Conflict);

        var roster = match.TryGetValue("players", out var p) ? p.SS : [];
        var scores = MatchScoring.ScoreMatch(body.Results, roster);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var items = new List<TransactWriteItem>
        {
            new()
            {
                Update = new Update
                {
                    TableName = _matchesTable,
                    Key = Shared.Key("match_id", matchId),
                    UpdateExpression = "SET #st = :done, finished_at = :now, scores = :scores",
                    ConditionExpression = "#st = :active",
                    ExpressionAttributeNames = new Dictionary<string, string> { ["#st"] = "status" },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":done"] = Shared.S("COMPLETED"),
                        [":active"] = Shared.S("ACTIVE"),
                        [":now"] = Shared.N(now),
                        [":scores"] = new AttributeValue { M = scores.ToDictionary(kv => kv.Key, kv => Shared.N(kv.Value)) },
                    },
                },
            },
        };
        items.AddRange(scores.Select(kv => new TransactWriteItem
        {
            Update = new Update
            {
                TableName = _leaderboard,
                Key = Shared.Key("player_id", kv.Key),
                // The leaderboard score is the running total of match points. board is the
                // sharded index key (always the same shard for a player).
                UpdateExpression = "SET #b = :b, #u = :u ADD #s :pts, games_played :one",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#b"] = "board", ["#u"] = "updated_at", ["#s"] = "score" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":b"] = Shared.S(LeaderboardShards.KeyFor(kv.Key, _shards)),
                    [":u"] = Shared.N(now),
                    [":pts"] = Shared.N(kv.Value),
                    [":one"] = Shared.N(1),
                },
            },
        }));

        try
        {
            await _ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = items });
        }
        catch (TransactionCanceledException)
        {
            return Shared.HttpJson(new { error = "This match result was already recorded." }, HttpStatusCode.Conflict);
        }

        return Shared.HttpJson(new { match_id = matchId, scores = scores.Select(kv => new ScoreRow(kv.Key, kv.Value)).ToList() });
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> TopScores(APIGatewayHttpApiV2ProxyRequest request)
    {
        var limit = request.QueryStringParameters?.TryGetValue("limit", out var l) == true && int.TryParse(l, out var n)
            ? Math.Clamp(n, 1, 100)
            : 30;

        // The board is split over several index keys. The top N of the union is inside the
        // union of each shard's top N, so read N from every shard and merge.
        var perShard = await Task.WhenAll(LeaderboardShards.All(_shards).Select(board => TopOfShard(board, limit)));
        var top = perShard.SelectMany(r => r)
            .OrderByDescending(r => r.score).ThenBy(r => r.player_id, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        return Shared.HttpJson(top, HttpStatusCode.OK, cacheSeconds: 10); // public: cache so refreshes do not all reach DynamoDB
    }

    private async Task<List<ScoreRow>> TopOfShard(string board, int limit)
    {
        var res = await _ddb.QueryAsync(new QueryRequest
        {
            TableName = _leaderboard,
            IndexName = LeaderboardIndex,
            KeyConditionExpression = "board = :b",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":b"] = Shared.S(board) },
            ScanIndexForward = false,
            Limit = limit,
        });
        return res.Items.Select(d => new ScoreRow(Shared.Str(d, "player_id") ?? "", Shared.Num(d, "score"))).ToList();
    }
}

public sealed record PlayerView(string player_id, string? display_name, int games_played, int wins);
public sealed record MatchView(string match_id, string game_id, string status, IReadOnlyList<string> players);
public sealed record ScoreRow(string player_id, long score);
public sealed record RegisterRequest(string DisplayName);
public sealed record JoinQueueRequest(string GameId, int TeamSize);
