using System.Net;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.SQS;
using Amazon.SQS.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ChatRelay;

internal static class Shared
{
    public static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    public static readonly Lazy<AmazonDynamoDBClient> Ddb = new(() => new AmazonDynamoDBClient());

    public static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    public static APIGatewayProxyResponse Ok(string body = "") => new() { StatusCode = 200, Body = body };
    public static APIGatewayProxyResponse Fail(int code = 500, string body = "") => new() { StatusCode = code, Body = body };
}

/// <summary>
/// WebSocket handler. $connect registers the socket in DynamoDB; $default
/// writes the message to history and hands the fan-out to an SQS queue (the
/// heavy delivery work never blocks the writer); $disconnect removes the
/// socket. Connection TTL is refreshed on activity and shadows clients that
/// vanish silently.
/// </summary>
public sealed class MessageHandler
{
    private readonly string _connectionsTable = Shared.Env("CONNECTIONS_TABLE");
    private readonly string _messagesTable    = Shared.Env("MESSAGES_TABLE");
    private readonly string _fanoutQueueUrl   = Shared.Env("FANOUT_QUEUE_URL");

    private static readonly Lazy<AmazonSQSClient> Sqs = new(() => new AmazonSQSClient());

    public async Task<APIGatewayProxyResponse> Handle(APIGatewayProxyRequest request, ILambdaContext context)
    {
        try
        {
            var connectionId = request.RequestContext.ConnectionId;
            return request.RequestContext.RouteKey switch
            {
                "$connect" => await OnConnect(request, connectionId),
                "$disconnect" => await OnDisconnect(connectionId),
                _ => await OnMessage(request, connectionId, context),
            };
        }
        catch (Exception ex)
        {
            context.Logger.LogError("WS failure: {0}", ex);
            return Shared.Fail(500, "internal error");
        }
    }

    private async Task<APIGatewayProxyResponse> OnConnect(APIGatewayProxyRequest request, string connectionId)
    {
        // Identity comes from the validated Cognito token (ConnectAuthorizer), never
        // from the query string; only the room and a display nickname are client-chosen.
        var userId = ConnectAuthorizer.FromContext(request, "userId");
        if (string.IsNullOrEmpty(userId))
            return Shared.Fail(401, "not authorized");

        string? nickname = null, roomId = null;
        if (request.QueryStringParameters is not null)
        {
            request.QueryStringParameters.TryGetValue("nickname", out nickname);
            request.QueryStringParameters.TryGetValue("roomId", out roomId);
        }
        if (string.IsNullOrEmpty(roomId) || roomId.Length > 64)
            return Shared.Fail(400, "query param roomId (+ optional nickname) required");
        if (string.IsNullOrWhiteSpace(nickname) || nickname.Length > 40)
            nickname = ConnectAuthorizer.FromContext(request, "userName") ?? userId;

        var now = DateTimeOffset.UtcNow;
        var table = Table.LoadTable(Shared.Ddb.Value, _connectionsTable);
        await table.PutItemAsync(new Document
        {
            ["connection_id"] = connectionId,
            ["user_id"]       = userId,
            ["nickname"]      = nickname,
            ["room_id"]       = roomId,
            ["connected_at"]  = now.ToUnixTimeSeconds().ToString(),
            ["ttl"]           = now.AddHours(6).ToUnixTimeSeconds(), // DynamoDB TTL only honours a Number attribute
        });
        return Shared.Ok(JsonSerializer.Serialize(new { status = "connected", room_id = roomId }));
    }

    private async Task<APIGatewayProxyResponse> OnDisconnect(string connectionId)
    {
        var table = Table.LoadTable(Shared.Ddb.Value, _connectionsTable);
        await table.DeleteItemAsync(connectionId);
        return Shared.Ok("bye");
    }

    private async Task<APIGatewayProxyResponse> OnMessage(APIGatewayProxyRequest request, string connectionId, ILambdaContext context)
    {
        using var body = JsonDocument.Parse(request.Body ?? "{}");
        var root = body.RootElement;

        if (!root.TryGetProperty("action", out var action) || action.GetString() != "send")
            return Shared.Ok(JsonSerializer.Serialize(new { error = "unsupported action" }));

        var text = root.TryGetProperty("text", out var t) ? t.GetString() : "";
        if (string.IsNullOrWhiteSpace(text)) return Shared.Ok(JsonSerializer.Serialize(new { error = "empty text" }));

        var connections = Table.LoadTable(Shared.Ddb.Value, _connectionsTable);
        var conn = await connections.GetItemAsync(connectionId);
        if (conn is null) return Shared.Fail(401, "not connected");

        var roomId = conn["room_id"].AsString();
        var userId = conn["user_id"].AsString();
        var nickname = conn["nickname"].AsString();
        var now = DateTimeOffset.UtcNow;

        var message = new Message(
            MessageId: Guid.NewGuid().ToString("N"),
            RoomId: roomId,
            UserId: userId,
            Nickname: nickname,
            Text: text,
            CreatedAt: now.ToUnixTimeMilliseconds());

        var messages = Table.LoadTable(Shared.Ddb.Value, _messagesTable);
        await messages.PutItemAsync(new Document
        {
            ["message_id"] = message.MessageId,
            ["room_id"]    = message.RoomId,
            ["user_id"]    = message.UserId,
            ["nickname"]   = message.Nickname,
            ["text"]       = message.Text,
            ["created_at"] = message.CreatedAt,
            ["ttl"]        = now.AddDays(7).ToUnixTimeSeconds(), // DynamoDB TTL only honours a Number attribute
        });

        // Fan-out is a queue, not a call chain: the writer returns instantly.
        await Sqs.Value.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = _fanoutQueueUrl,
            MessageBody = JsonSerializer.Serialize(message, Shared.JsonOpts),
        });

        // Refresh the presence TTL.
        await Shared.Ddb.Value.UpdateItemAsync(new Amazon.DynamoDBv2.Model.UpdateItemRequest
        {
            TableName = _connectionsTable,
            Key = new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>
            {
                ["connection_id"] = new() { S = connectionId },
            },
            UpdateExpression = "SET #t = :t",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#t"] = "ttl" },
            ExpressionAttributeValues = new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>
            {
                [":t"] = new() { N = now.AddHours(6).ToUnixTimeSeconds().ToString() },
            },
        });

        return Shared.Ok(JsonSerializer.Serialize(new { delivered = false, message_id = message.MessageId }));
    }
}

public sealed record Message(string MessageId, string RoomId, string UserId, string Nickname, string Text, long CreatedAt);