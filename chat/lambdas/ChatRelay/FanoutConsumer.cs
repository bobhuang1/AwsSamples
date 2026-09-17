using System.Net;
using System.Text.Json;
using Amazon.ApiGatewayManagementApi;
using Amazon.ApiGatewayManagementApi.Model;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;

namespace ChatRelay;

/// <summary>
/// SQS consumer that delivers a message to every live socket in its room.
/// Delivery failures prune dead connections (a GoneException means the socket
/// no longer exists); anything else is reported as a batch-item failure and
/// redrives to the DLQ after 5 attempts.
/// </summary>
public sealed class FanoutConsumer
{
    private readonly string _connectionsTable = Shared.Env("CONNECTIONS_TABLE");
    private readonly string _wsEndpoint       = Shared.Env("WS_ENDPOINT");

    private static readonly Lazy<AmazonDynamoDBClient> Ddb = new(() => new AmazonDynamoDBClient());

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
        // All sockets in the room (GSI, KEYS_ONLY projection).
        var peersIds = new List<string>();
        string? lastKey = null;
        do
        {
            var q = await Ddb.Value.QueryAsync(new QueryRequest
            {
                TableName = _connectionsTable,
                IndexName = "room-index",
                KeyConditionExpression = "room_id = :r",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":r"] = new() { S = roomId } },
                ProjectionExpression = "connection_id",
                Limit = 50,
                ExclusiveStartKey = lastKey is null ? null : new Dictionary<string, AttributeValue>
                {
                    ["connection_id"] = new() { S = lastKey },
                    ["room_id"] = new() { S = roomId },
                },
            });
            peersIds.AddRange(q.Items.Select(i => i["connection_id"].S));
            lastKey = q.LastEvaluatedKey.ContainsKey("connection_id") ? q.LastEvaluatedKey["connection_id"].S : null;
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

        using var api = new AmazonApiGatewayManagementApiClient(new AmazonApiGatewayManagementApiConfig { ServiceURL = _wsEndpoint });
        var roomeTable = Table.LoadTable(Ddb.Value, _connectionsTable);

        foreach (var connectionId in peersIds)
        {
            try
            {
                await api.PostToConnectionAsync(new PostToConnectionRequest
                {
                    ConnectionId = connectionId,
                    Data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload)),
                });
            }
            catch (GoneException gex)
            {
                context.Logger.LogInformation("Pruning stale connection {0} ({1})", connectionId, gex.Message);
                await roomeTable.DeleteItemAsync(connectionId);
            }
        }
    }
}