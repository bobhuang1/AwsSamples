using System.Net;
using System.Text.Json;
using Amazon.ApiGatewayManagementApi;
using Amazon.ApiGatewayManagementApi.Model;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;

namespace GameBackend;

/// <summary>
/// WebSocket handler. The API has three routes ($connect / $default / $disconnect);
/// on $default we inspect the body ("send to my match") and relay the event to
/// every connection registered to the same match via the API Gateway
/// postToConnection endpoint.
/// </summary>
public sealed class SessionHandler
{
    private readonly string _connectionsTable = Shared.Env("CONNECTIONS_TABLE", "connections");
    private readonly string _matchesTable     = Shared.Env("MATCHES_TABLE", "matches");
    private readonly string _wsEndpoint       = Shared.Env("WS_ENDPOINT", "");

    private const int MaxParallelPosts = 16;

    // Sockets one player may hold at once. Each message is posted to every socket of the
    // match, so an uncapped player could multiply the cost of every message.
    private const int MaxSocketsPerPlayer = 5;

    // API Gateway ends a WebSocket after 2 hours, so a record that lives 3 hours covers the
    // longest session without refreshing it (DynamoDB deletes expired items lazily, later).
    private static readonly TimeSpan ConnectionLease = TimeSpan.FromHours(3);

    // One client for the life of the container, so the TLS connection is reused.
    private static IAmazonApiGatewayManagementApi? _sharedApi;

    private readonly IAmazonDynamoDB _ddb;
    private readonly IAmazonApiGatewayManagementApi? _injectedApi;

    private IAmazonApiGatewayManagementApi Api =>
        _injectedApi ?? (_sharedApi ??= new AmazonApiGatewayManagementApiClient(new AmazonApiGatewayManagementApiConfig { ServiceURL = _wsEndpoint }));

    // The Lambda runtime uses this constructor.
    public SessionHandler() : this(Shared.Ddb.Value, null) { }

    public SessionHandler(IAmazonDynamoDB ddb, IAmazonApiGatewayManagementApi? api)
    {
        _ddb = ddb;
        _injectedApi = api;
    }

    public async Task<APIGatewayProxyResponse> Handle(APIGatewayProxyRequest request, ILambdaContext context)
    {
        try
        {
            var rc = request.RequestContext;
            var connectionId = rc.ConnectionId;

            return rc.RouteKey switch
            {
                "$connect" => await OnConnect(request, connectionId),
                "$disconnect" => await OnDisconnect(connectionId),
                _ => await OnMessage(request, connectionId, context),
            };
        }
        catch (JsonException)
        {
            return new APIGatewayProxyResponse { StatusCode = 400, Body = "malformed message" };
        }
        catch (Exception ex)
        {
            context.Logger.LogError("WS failure: {0}", ex);
            return new APIGatewayProxyResponse { StatusCode = 500, Body = "internal error" };
        }
    }

    private async Task<APIGatewayProxyResponse> OnConnect(APIGatewayProxyRequest request, string connectionId)
    {
        // The player is whoever the Cognito token (checked by ConnectAuthorizer) says,
        // and they may only join the live channel of a match they are actually in.
        var playerId = ConnectAuthorizer.FromContext(request, "userId");
        if (string.IsNullOrEmpty(playerId))
            return new APIGatewayProxyResponse { StatusCode = 401, Body = "not authorized" };

        string? matchId = null;
        request.QueryStringParameters?.TryGetValue("matchId", out matchId);
        if (string.IsNullOrEmpty(matchId))
            return new APIGatewayProxyResponse { StatusCode = 400, Body = "query param matchId required" };

        // Consistent read: a client often connects right after the match forms.
        var match = await _ddb.GetItemAsync(new GetItemRequest { TableName = _matchesTable, Key = Shared.Key("match_id", matchId), ConsistentRead = true });
        var players = match.Item is { Count: > 0 } m && m.TryGetValue("players", out var pl) ? pl.SS : [];
        if (!players.Contains(playerId))
            return new APIGatewayProxyResponse { StatusCode = 403, Body = "not a player in this match" };

        var open = await _ddb.QueryAsync(new QueryRequest
        {
            TableName = _connectionsTable,
            IndexName = "player-index",
            KeyConditionExpression = "player_id = :p",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":p"] = Shared.S(playerId) },
            Select = Select.COUNT,
            Limit = MaxSocketsPerPlayer,
        });
        if (open.Count >= MaxSocketsPerPlayer)
            return new APIGatewayProxyResponse { StatusCode = 429, Body = "too many open connections" };

        var now = DateTimeOffset.UtcNow;
        await _ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _connectionsTable,
            Item = new Dictionary<string, AttributeValue>
            {
                ["connection_id"] = Shared.S(connectionId),
                ["player_id"]     = Shared.S(playerId),
                ["match_id"]      = Shared.S(matchId),
                ["connected_at"]  = Shared.S(now.ToUnixTimeSeconds().ToString()),
                ["ttl"]           = Shared.N(now.Add(ConnectionLease).ToUnixTimeSeconds()), // DynamoDB TTL only honours a Number attribute
            },
        });
        return Ok(connectionId, playerId, matchId);
    }

    private async Task<APIGatewayProxyResponse> OnDisconnect(string connectionId)
    {
        await _ddb.DeleteItemAsync(new DeleteItemRequest { TableName = _connectionsTable, Key = Shared.Key("connection_id", connectionId) });
        return new APIGatewayProxyResponse { StatusCode = 200, Body = "bye" };
    }

    private async Task<APIGatewayProxyResponse> OnMessage(APIGatewayProxyRequest request, string connectionId, ILambdaContext context)
    {
        using var body = JsonDocument.Parse(request.Body ?? "{}");
        var root = body.RootElement;

        // Valid JSON of the wrong shape is a client error, not a failure of this function.
        if (root.ValueKind != JsonValueKind.Object)
            return new APIGatewayProxyResponse { StatusCode = 400, Body = "message must be a JSON object" };
        string? action = null;
        if (root.TryGetProperty("action", out var a))
        {
            if (a.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return new APIGatewayProxyResponse { StatusCode = 400, Body = "action must be a string" };
            action = a.GetString();
        }
        var payload = root.TryGetProperty("payload", out var p) ? p.GetRawText() : "{}";

        // Sender and match come from the connection record written at $connect, not from
        // the message body, so a client cannot speak for another player or another match.
        var senderRes = await _ddb.GetItemAsync(new GetItemRequest { TableName = _connectionsTable, Key = Shared.Key("connection_id", connectionId) });
        if (senderRes.Item is not { Count: > 0 } sender || Shared.Str(sender, "player_id") is not { } senderId || Shared.Str(sender, "match_id") is not { } matchId)
            return new APIGatewayProxyResponse { StatusCode = 401, Body = "not connected" };

        // Relay to everyone else in the match (KEYS_ONLY index: connection_id is all we read).
        var peerIds = new List<string>();
        Dictionary<string, AttributeValue>? lastKey = null;
        do
        {
            var q = await _ddb.QueryAsync(new QueryRequest
            {
                TableName = _connectionsTable,
                IndexName = "match-index",
                KeyConditionExpression = "match_id = :m",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":m"] = Shared.S(matchId) },
                ExclusiveStartKey = lastKey,
            });
            peerIds.AddRange(q.Items.Select(i => i["connection_id"].S));
            lastKey = q.LastEvaluatedKey is { Count: > 0 } ? q.LastEvaluatedKey : null;
        } while (lastKey is not null);

        var data = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            action = action,
            from_player = senderId,
            payload = payload,
        }, Shared.JsonOpts));

        await Parallel.ForEachAsync(peerIds.Where(id => id != connectionId),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelPosts }, async (targetId, ct) =>
        {
            try
            {
                await Api.PostToConnectionAsync(new PostToConnectionRequest
                {
                    ConnectionId = targetId,
                    Data = new MemoryStream(data),
                }, ct);
            }
            catch (GoneException gex)
            {
                context.Logger.LogInformation("Stale connection removed: {0} ({1})", targetId, gex.Message);
                await _ddb.DeleteItemAsync(new DeleteItemRequest { TableName = _connectionsTable, Key = Shared.Key("connection_id", targetId) }, ct);
            }
        });
        return new APIGatewayProxyResponse { StatusCode = 200, Body = "relayed" };
    }

    private static APIGatewayProxyResponse Ok(string connectionId, string playerId, string matchId)
        => new() { StatusCode = 200, Body = JsonSerializer.Serialize(new { status = "connected", connection_id = connectionId, player_id = playerId, match_id = matchId }) };
}