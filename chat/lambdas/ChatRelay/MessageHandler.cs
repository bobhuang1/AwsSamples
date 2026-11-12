using System.Net;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.SQS;
using Amazon.SQS.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ChatRelay.Tests")]

namespace ChatRelay;

internal static class Shared
{
    public static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    public static readonly Lazy<IAmazonDynamoDB> Ddb = new(() => new AmazonDynamoDBClient());

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

    private const int MaxTextLength = 2000;

    // Sockets one user may hold at once. Each message is posted to every socket in the room,
    // so an uncapped user could multiply the cost of every message.
    private const int MaxSocketsPerUser = 5;
    private static readonly TimeSpan PresenceLease = TimeSpan.FromHours(6);

    private static readonly Lazy<IAmazonSQS> DefaultSqs = new(() => new AmazonSQSClient());

    private readonly IAmazonDynamoDB _ddb;
    private readonly IAmazonSQS _sqs;

    // The Lambda runtime uses this constructor.
    public MessageHandler() : this(Shared.Ddb.Value, DefaultSqs.Value) { }

    public MessageHandler(IAmazonDynamoDB ddb, IAmazonSQS sqs)
    {
        _ddb = ddb;
        _sqs = sqs;
    }

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
        catch (JsonException)
        {
            return Shared.Fail(400, "malformed message");
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

        var open = await _ddb.QueryAsync(new QueryRequest
        {
            TableName = _connectionsTable,
            IndexName = "user-index",
            KeyConditionExpression = "user_id = :u",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":u"] = new() { S = userId } },
            Select = Select.COUNT,
            Limit = MaxSocketsPerUser,
        });
        if (open.Count >= MaxSocketsPerUser)
            return Shared.Fail(429, "too many open connections");

        var now = DateTimeOffset.UtcNow;
        await _ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _connectionsTable,
            Item = new Dictionary<string, AttributeValue>
            {
                ["connection_id"] = new() { S = connectionId },
                ["user_id"]       = new() { S = userId },
                ["nickname"]      = new() { S = nickname },
                ["room_id"]       = new() { S = roomId },
                ["connected_at"]  = new() { S = now.ToUnixTimeSeconds().ToString() },
                ["ttl"]           = new() { N = now.Add(PresenceLease).ToUnixTimeSeconds().ToString() }, // DynamoDB TTL only honours a Number attribute
            },
        });
        return Shared.Ok(JsonSerializer.Serialize(new { status = "connected", room_id = roomId }));
    }

    private async Task<APIGatewayProxyResponse> OnDisconnect(string connectionId)
    {
        await _ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _connectionsTable,
            Key = new Dictionary<string, AttributeValue> { ["connection_id"] = new() { S = connectionId } },
        });
        return Shared.Ok("bye");
    }

    private async Task<APIGatewayProxyResponse> OnMessage(APIGatewayProxyRequest request, string connectionId, ILambdaContext context)
    {
        using var body = JsonDocument.Parse(request.Body ?? "{}");
        var root = body.RootElement;

        // Valid JSON of the wrong shape (an array, a number, "text": 5) is a client error, not a failure here.
        if (root.ValueKind != JsonValueKind.Object)
            return Shared.Fail(400, "message must be a JSON object");
        if (!root.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String || action.GetString() != "send")
            return Shared.Ok(JsonSerializer.Serialize(new { error = "unsupported action" }));

        string? text = "";
        if (root.TryGetProperty("text", out var t))
        {
            if (t.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return Shared.Fail(400, "text must be a string");
            text = t.GetString();
        }
        if (string.IsNullOrWhiteSpace(text)) return Shared.Ok(JsonSerializer.Serialize(new { error = "empty text" }));
        if (text.Length > MaxTextLength) return Shared.Ok(JsonSerializer.Serialize(new { error = $"text too long (max {MaxTextLength})" }));

        var connRes = await _ddb.GetItemAsync(new GetItemRequest
        {
            TableName = _connectionsTable,
            Key = new Dictionary<string, AttributeValue> { ["connection_id"] = new() { S = connectionId } },
        });
        if (connRes.Item is not { Count: > 0 } conn) return Shared.Fail(401, "not connected");

        var roomId = conn["room_id"].S;
        var userId = conn["user_id"].S;
        var nickname = conn["nickname"].S;
        var now = DateTimeOffset.UtcNow;

        var message = new Message(
            MessageId: Guid.NewGuid().ToString("N"),
            RoomId: roomId,
            UserId: userId,
            Nickname: nickname,
            Text: text,
            CreatedAt: now.ToUnixTimeMilliseconds());

        await _ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _messagesTable,
            Item = new Dictionary<string, AttributeValue>
            {
                ["message_id"] = new() { S = message.MessageId },
                ["room_id"]    = new() { S = message.RoomId },
                ["user_id"]    = new() { S = message.UserId },
                ["nickname"]   = new() { S = message.Nickname },
                ["text"]       = new() { S = message.Text },
                ["created_at"] = new() { N = message.CreatedAt.ToString() },
                ["ttl"]        = new() { N = now.AddDays(7).ToUnixTimeSeconds().ToString() }, // DynamoDB TTL only honours a Number attribute
            },
        });

        // Fan-out is a queue, not a call chain: the writer returns instantly.
        await _sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = _fanoutQueueUrl,
            MessageBody = JsonSerializer.Serialize(message, Shared.JsonOpts),
        });

        // Refresh the presence TTL, but only once half the lease is gone: a write
        // on the connections table for every chat line is not needed.
        var currentTtl = conn.TryGetValue("ttl", out var ttlEntry) && long.TryParse(ttlEntry.N, out var ttlSeconds) ? ttlSeconds : 0;
        if (currentTtl - now.ToUnixTimeSeconds() < PresenceLease.TotalSeconds / 2)
        {
            await _ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _connectionsTable,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["connection_id"] = new() { S = connectionId },
                },
                UpdateExpression = "SET #t = :t",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#t"] = "ttl" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":t"] = new() { N = now.Add(PresenceLease).ToUnixTimeSeconds().ToString() },
                },
            });
        }

        return Shared.Ok(JsonSerializer.Serialize(new { delivered = false, message_id = message.MessageId }));
    }
}

public sealed record Message(string MessageId, string RoomId, string UserId, string Nickname, string Text, long CreatedAt);