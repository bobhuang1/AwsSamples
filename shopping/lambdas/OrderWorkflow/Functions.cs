using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.EventBridge;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.StepFunctions;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace OrderWorkflow;

/// <summary>
/// One deployment, three handlers sharing a zip:
///  - ProcessOrder   : SQS consumer -> marks order PROCESSING, starts the Step Functions
///                     execution (batch failures are reported per-message).
///  - CompleteOrder  : final Step Functions task -> marks the order COMPLETED.
///  - AuditOrder     : EventBridge target -> ships every order event to the audit bucket.
/// </summary>
public sealed class Functions
{
    private static readonly Lazy<AmazonDynamoDBClient> Ddb = new(() => new AmazonDynamoDBClient());
    private static readonly Lazy<AmazonStepFunctionsClient> Sfn = new(() => new AmazonStepFunctionsClient());
    private static readonly Lazy<AmazonEventBridgeClient> Events = new(() => new AmazonEventBridgeClient());
    private static readonly Lazy<AmazonS3Client> S3 = new(() => new AmazonS3Client());

    private readonly string _ordersTable = Env("ORDERS_TABLE", "orders");
    private readonly string _stateMachine = Env("STATE_MACHINE", "");
    private readonly string _eventBus = Env("ORDER_EVENT_BUS", "orders");
    private readonly string _auditBucket = Env("AUDIT_BUCKET", "");

    // ---------- 1. SQS consumer (with ReportBatchItemFailures) ----------

    public async Task<SQSBatchResponse> ProcessOrder(SQSEvent input, ILambdaContext context)
    {
        var failures = new List<SQSBatchResponse.BatchItemFailure>();

        foreach (var record in input.Records)
        {
            try
            {
                using var job = JsonDocument.Parse(record.Body);
                var orderId = job.RootElement.GetProperty("order_id").GetString()
                    ?? throw new InvalidOperationException("Missing order_id in message");

                await SetStatus(orderId, "PROCESSING");

                await Sfn.Value.StartExecutionAsync(new Amazon.StepFunctions.Model.StartExecutionRequest
                {
                    StateMachineArn = _stateMachine,
                    Name = $"order-{orderId.Substring(0, 12)}",
                    Input = record.Body,
                });

                await Emit(orderId, "order.processing", record.Body);
                context.Logger.LogInformation("Queued order {0}", orderId);
            }
            catch (Exception ex)
            {
                context.Logger.LogError("Failed to process message: {0}", ex);
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = record.MessageId });
            }
        }

        return new SQSBatchResponse { BatchItemFailures = failures };
    }

    // ---------- 2. Last Step Functions task: mark COMPLETED ----------

    public async Task<JsonElement> CompleteOrder(JsonElement input, ILambdaContext context)
    {
        var orderId = input.TryGetProperty("order_id", out var id) ? id.GetString() : null;
        if (orderId is null) throw new InvalidOperationException("Missing order_id in input");

        await SetStatus(orderId, "COMPLETED");
        await Emit(orderId, "order.completed", input.GetRawText());
        context.Logger.LogInformation("Completed order {0}", orderId);
        return input;
    }

    // ---------- 3. EventBridge target: archive to S3 ----------

    public async Task AuditOrder(JsonElement input, ILambdaContext context)
    {
        var orderId = input.TryGetProperty("order_id", out var id) ? id.GetString() : "unknown";
        var day = DateTimeOffset.UtcNow.ToString("yyyy/MM/dd");
        await S3.Value.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _auditBucket,
            Key = $"orders/{day}/{orderId}-{Guid.NewGuid():N}.json",
            ContentBody = input.GetRawText(),
            ContentType = "application/json",
        });
    }

    // ---------------- helpers ----------------

    private async Task SetStatus(string orderId, string status)
    {
        await Ddb.Value.UpdateItemAsync(new Amazon.DynamoDBv2.Model.UpdateItemRequest
        {
            TableName = _ordersTable,
            Key = new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>
            {
                ["order_id"] = new() { S = orderId },
            },
            UpdateExpression = "SET #s = :s",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
            ExpressionAttributeValues = new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>
            {
                [":s"] = new() { S = status },
            },
        });
    }

    private Task Emit(string orderId, string detailType, string detail) =>
        Events.Value.PutEventsAsync(new Amazon.EventBridge.Model.PutEventsRequest
        {
            Entries = new List<Amazon.EventBridge.Model.PutEventsRequestEntry>
            {
                new()
                {
                    Source = "com.sample.shop",
                    DetailType = detailType,
                    EventBusName = _eventBus,
                    Detail = detail,
                }
            }
        });

    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;
}