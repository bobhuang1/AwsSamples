using System.Net;
using System.Text.Json;
using Amazon.ApiGatewayManagementApi;
using Amazon.ApiGatewayManagementApi.Model;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
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

        var match = await Table.LoadTable(Shared.Ddb.Value, _matchesTable).GetItemAsync(matchId);
        var players = match is not null && match.Contains("players") && match["players"] is PrimitiveList pl
            ? pl.Entries.Select(e => e.AsString()).ToList()
            : new List<string>();
        if (!players.Contains(playerId))
            return new APIGatewayProxyResponse { StatusCode = 403, Body = "not a player in this match" };

        var now = DateTimeOffset.UtcNow;
        var table = Table.LoadTable(Shared.Ddb.Value, _connectionsTable);
        await table.PutItemAsync(new Document
        {
            ["connection_id"] = connectionId,
            ["player_id"]     = playerId,
            ["match_id"]      = matchId,
            ["connected_at"]  = now.ToUnixTimeSeconds().ToString(),
            ["ttl"]           = now.AddHours(1).ToUnixTimeSeconds(), // DynamoDB TTL only honours a Number attribute
        });
        return Ok(connectionId, playerId, matchId);
    }

    private async Task<APIGatewayProxyResponse> OnDisconnect(string connectionId)
    {
        var table = Table.LoadTable(Shared.Ddb.Value, _connectionsTable);
        await table.DeleteItemAsync(connectionId);
        return new APIGatewayProxyResponse { StatusCode = 200, Body = "bye" };
    }

    private async Task<APIGatewayProxyResponse> OnMessage(APIGatewayProxyRequest request, string connectionId, ILambdaContext context)
    {
        using var body = JsonDocument.Parse(request.Body ?? "{}");
        var root = body.RootElement;

        var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
        var payload = root.TryGetProperty("payload", out var p) ? p.GetRawText() : "{}";

        // Sender and match come from the connection record written at $connect, not from
        // the message body, so a client cannot speak for another player or another match.
        var sender = await Table.LoadTable(Shared.Ddb.Value, _connectionsTable).GetItemAsync(connectionId);
        if (sender is null || !sender.Contains("player_id") || !sender.Contains("match_id"))
            return new APIGatewayProxyResponse { StatusCode = 401, Body = "not connected" };
        var senderId = sender["player_id"].AsString();
        var matchId = sender["match_id"].AsString();

        // Relay to everyone else in the match.
        var connections = Table.LoadTable(Shared.Ddb.Value, _connectionsTable);
        var peers = await connections.Query(new QueryOperationConfig
        {
            IndexName     = "match-index",
            KeyExpression = new Expression
            {
                ExpressionStatement = "match_id = :m",
                ExpressionAttributeValues = new Dictionary<string, DynamoDBEntry> { [":m"] = matchId },
            },
        }).GetRemainingAsync();

        var api = new AmazonApiGatewayManagementApiClient(new AmazonApiGatewayManagementApiConfig { ServiceURL = _wsEndpoint });

        foreach (var conn in peers)
        {
            var targetId = conn["connection_id"].AsString();
            if (targetId == connectionId) continue;
            try
            {
                await api.PostToConnectionAsync(new PostToConnectionRequest
                {
                    ConnectionId = targetId,
                    Data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                    {
                        action = action,
                        from_player = senderId,
                        payload = payload,
                    }, Shared.JsonOpts))),
                });
            }
            catch (GoneException gex)
            {
                context.Logger.LogInformation("Stale connection removed: {0} ({1})", targetId, gex.Message);
                await connections.DeleteItemAsync(targetId);
            }
        }
        api.Dispose();
        return new APIGatewayProxyResponse { StatusCode = 200, Body = "relayed" };
    }

    private static APIGatewayProxyResponse Ok(string connectionId, string playerId, string matchId)
        => new() { StatusCode = 200, Body = JsonSerializer.Serialize(new { status = "connected", connection_id = connectionId, player_id = playerId, match_id = matchId }) };
}