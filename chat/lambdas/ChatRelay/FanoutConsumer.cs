using System.Net;
using System.Text.Json;
using Amazon.ApiGatewayManagementApi;
using Amazon.ApiGatewayManagementApi.Model;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Runtime;
using Amazon.Lambda.SQSEvents;

namespace ChatRelay;

/// <summary>
/// SQS consumer that delivers a message to every live socket in its room.
/// Delivery failures prune dead connections (a GoneException means the socket
/// no longer exists); a failure to read the room is reported as a batch-item
/// failure and redrives to the DLQ after 5 attempts. Delivery is at-least-once,
/// so clients de-duplicate on message_id.
/// </summary>
public sealed class FanoutConsumer
{
    private readonly string _connectionsTable = Shared.Env("CONNECTIONS_TABLE");
    private readonly string _wsEndpoint       = Shared.Env("WS_ENDPOINT");

    // Sockets written to at the same time per message; keeps a big room from
    // taking one round trip after another without flooding the Management API.
    private const int MaxParallelPosts = 32;

    // One client for the life of the container, so the TLS connection is reused.
    private static IAmazonApiGatewayManagementApi? _sharedApi;

    private readonly IAmazonDynamoDB _ddb;
    private readonly IAmazonApiGatewayManagementApi? _injectedApi;

    private IAmazonApiGatewayManagementApi Api =>
        _injectedApi ?? (_sharedApi ??= new AmazonApiGatewayManagementApiClient(new AmazonApiGatewayManagementApiConfig { ServiceURL = _wsEndpoint }));

    // The Lambda runtime uses this constructor.
    public FanoutConsumer() : this(Shared.Ddb.Value, null) { }

    public FanoutConsumer(IAmazonDynamoDB ddb, IAmazonApiGatewayManagementApi? api)
    {
        _ddb = ddb;
        _injectedApi = api;
    }

    public async Task<SQSBatchResponse> Handle(SQSEvent input, ILambdaContext context)
    {
        var failures = new List<SQSBatchResponse.BatchItemFailure>();

        foreach (var record in input.Records)
        {
            try
            {
                using var message = JsonDocument.Parse(record.Body);
                var roomId = message.RootElement.GetProperty("RoomId").GetString();
                await Deliver(message.RootElement, roomId!, context);
            }
            catch (Exception ex)
            {
                context.Logger.LogError("Fanout failed: {0}", ex);
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = record.MessageId });
            }
        }

        return new SQSBatchResponse { BatchItemFailures = failures };
    }

    private async Task Deliver(JsonElement message, string roomId, ILambdaContext context)
    {
        // All sockets in the room (GSI, KEYS_ONLY projection, so pages are small).
        var peersIds = new List<string>();
        Dictionary<string, AttributeValue>? lastKey = null;
        do
        {
            var q = await _ddb.QueryAsync(new QueryRequest
            {
                TableName = _connectionsTable,
                IndexName = "room-index",
                KeyConditionExpression = "room_id = :r",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":r"] = new() { S = roomId } },
                ProjectionExpression = "connection_id",
                ExclusiveStartKey = lastKey,
            });
            peersIds.AddRange(q.Items.Select(i => i["connection_id"].S));
            lastKey = q.LastEvaluatedKey is { Count: > 0 } ? q.LastEvaluatedKey : null;
        } while (lastKey is not null);

        if (peersIds.Count == 0) return;

        var payload = JsonSerializer.Serialize(new
        {
            type = "message",
            message = new
            {
                message_id = message.GetProperty("MessageId").GetString(),
                user_id = message.GetProperty("UserId").GetString(),
                nickname = message.GetProperty("Nickname").GetString(),
                text = message.GetProperty("Text").GetString(),
                created_at = message.GetProperty("CreatedAt").GetInt64(),
            },
        });

        var data = System.Text.Encoding.UTF8.GetBytes(payload);

        // A failure for one socket must not fail the whole message: SQS would
        // redeliver it and every socket already served would see it again.
        // Gone sockets are pruned; other per-socket errors are logged and skipped.
        await Parallel.ForEachAsync(peersIds, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelPosts }, async (connectionId, ct) =>
        {
            try
            {
                await Api.PostToConnectionAsync(new PostToConnectionRequest
                {
                    ConnectionId = connectionId,
                    Data = new MemoryStream(data),
                }, ct);
            }
            catch (GoneException gex)
            {
                context.Logger.LogInformation("Pruning stale connection {0} ({1})", connectionId, gex.Message);
                await _ddb.DeleteItemAsync(new DeleteItemRequest
                {
                    TableName = _connectionsTable,
                    Key = new Dictionary<string, AttributeValue> { ["connection_id"] = new() { S = connectionId } },
                }, ct);
            }
            catch (Exception ex) when (ex is AmazonServiceException)
            {
                context.Logger.LogWarning("Delivery to {0} failed: {1}", connectionId, ex.Message);
            }
        });
    }
}
