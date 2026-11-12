using System.Globalization;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Amazon.EventBridge;
using Amazon.Lambda.Core;
using Amazon.Lambda.DynamoDBEvents;
using Amazon.Lambda.SQSEvents;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.StepFunctions;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OrderWorkflow.Tests")]

namespace OrderWorkflow;

/// <summary>
/// One deployment, four handlers sharing a zip:
///  - PublishOrder   : orders table stream (outbox) -> sends each new order to the SQS
///                     queue and the event bus. Failed records are retried by the stream.
///  - ProcessOrder   : SQS consumer -> marks order PROCESSING, starts the Step Functions
///                     execution (batch failures are reported per-message).
///  - CompleteOrder  : final Step Functions task -> marks the order COMPLETED.
///  - AuditOrder     : EventBridge target -> ships every order event to the audit bucket.
/// </summary>
public sealed class Functions
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // Clients are created on first use, so each function only builds the ones it calls.
    private readonly Lazy<IAmazonDynamoDB> _ddb;
    private readonly Lazy<IAmazonStepFunctions> _sfn;
    private readonly Lazy<IAmazonEventBridge> _events;
    private readonly Lazy<IAmazonS3> _s3;
    private readonly Lazy<IAmazonSQS> _sqs;

    private IAmazonDynamoDB Ddb => _ddb.Value;
    private IAmazonStepFunctions Sfn => _sfn.Value;
    private IAmazonEventBridge Events => _events.Value;
    private IAmazonS3 S3 => _s3.Value;
    private IAmazonSQS Sqs => _sqs.Value;

    // The Lambda runtime uses this constructor.
    public Functions()
    {
        _ddb = new(() => new AmazonDynamoDBClient());
        _sfn = new(() => new AmazonStepFunctionsClient());
        _events = new(() => new AmazonEventBridgeClient());
        _s3 = new(() => new AmazonS3Client());
        _sqs = new(() => new AmazonSQSClient());
    }

    public Functions(IAmazonDynamoDB ddb, IAmazonSQS sqs, IAmazonStepFunctions sfn, IAmazonEventBridge events, IAmazonS3 s3)
    {
        _ddb = new(ddb);
        _sqs = new(sqs);
        _sfn = new(sfn);
        _events = new(events);
        _s3 = new(s3);
    }

    private readonly string _ordersTable = Env("ORDERS_TABLE", "orders");
    private readonly string _stateMachine = Env("STATE_MACHINE", "");
    private readonly string _eventBus = Env("ORDER_EVENT_BUS", "orders");
    private readonly string _auditBucket = Env("AUDIT_BUCKET", "");
    private readonly string _ordersQueue = Env("ORDERS_QUEUE_URL", "");

    // ---------- 0. Outbox: orders table stream -> SQS + EventBridge ----------

    public async Task<StreamsEventResponse> PublishOrder(DynamoDBEvent input, ILambdaContext context)
    {
        var failures = new List<StreamsEventResponse.BatchItemFailure>();

        foreach (var record in input.Records)
        {
            if (record.EventName != "INSERT")
                continue; // status updates and TTL deletes are not new orders

            try
            {
                // The order row is the only copy of the order: the message is built from it here.
                var image = record.Dynamodb.NewImage;
                var orderId = image["order_id"].S;
                var payload = JsonSerializer.Serialize(OrderMessage.FromImage(image), JsonOpts);

                // Hand the order to the durable checkout pipeline: queue -> worker -> Step Functions.
                // The stream may deliver a record more than once; ProcessOrder's status guard
                // makes the duplicate harmless.
                await Sqs.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = _ordersQueue,
                    MessageBody = payload,
                });

                // Announce it on the event bus for downstream consumers (search, BI, email...).
                await Emit(orderId, "order.placed", payload);
            }
            catch (Exception ex)
            {
                context.Logger.LogError("Failed to publish order from stream: {0}", ex);
                failures.Add(new StreamsEventResponse.BatchItemFailure { ItemIdentifier = record.Dynamodb.SequenceNumber });
                break; // stream records must be retried in order from the first failure
            }
        }

        return new StreamsEventResponse { BatchItemFailures = failures };
    }

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

                if (!await SetStatus(orderId, "PROCESSING", "PLACED", "PROCESSING"))
                {
                    // Redelivery after the order already completed: nothing to do.
                    context.Logger.LogInformation("Order {0} is past PROCESSING; skipping", orderId);
                    continue;
                }

                await Sfn.StartExecutionAsync(new Amazon.StepFunctions.Model.StartExecutionRequest
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

        if (!await SetStatus(orderId, "COMPLETED", "PROCESSING", "COMPLETED"))
            throw new InvalidOperationException($"Order {orderId} is not in PROCESSING");
        await Emit(orderId, "order.completed", input.GetRawText());
        context.Logger.LogInformation("Completed order {0}", orderId);
        return input;
    }

    // ---------- 3. EventBridge target: archive to S3 ----------

    public async Task AuditOrder(JsonElement input, ILambdaContext context)
    {
        // EventBridge delivers the whole envelope; the order is under "detail".
        var orderId = input.TryGetProperty("detail", out var detail)
                      && detail.ValueKind == JsonValueKind.Object
                      && detail.TryGetProperty("order_id", out var id)
            ? id.GetString() ?? "unknown"
            : "unknown";
        var day = DateTimeOffset.UtcNow.ToString("yyyy/MM/dd");
        await S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _auditBucket,
            Key = $"orders/{day}/{orderId}-{Guid.NewGuid():N}.json",
            ContentBody = input.GetRawText(),
            ContentType = "application/json",
        });
    }

    // ---------------- helpers ----------------

    /// <summary>
    /// Moves the order to <paramref name="status"/> only if it is currently in one of
    /// <paramref name="allowedFrom"/>, so a redelivered message can't move it backwards.
    /// Returns false when the guard rejects the transition.
    /// </summary>
    private async Task<bool> SetStatus(string orderId, string status, params string[] allowedFrom)
    {
        var values = new Dictionary<string, AttributeValue> { [":s"] = new() { S = status } };
        for (var i = 0; i < allowedFrom.Length; i++)
            values[$":f{i}"] = new() { S = allowedFrom[i] };

        try
        {
            await Ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _ordersTable,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["order_id"] = new() { S = orderId },
                },
                UpdateExpression = "SET #s = :s",
                ConditionExpression = $"#s IN ({string.Join(", ", allowedFrom.Select((_, i) => $":f{i}"))})",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
                ExpressionAttributeValues = values,
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    private Task Emit(string orderId, string detailType, string detail) =>
        Events.PutEventsAsync(new Amazon.EventBridge.Model.PutEventsRequest
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

/// <summary>
/// The order message sent to SQS and EventBridge and passed through the state machine,
/// built from the stored order item (DynamoDB stream image).
/// </summary>
public sealed record OrderMessage(string order_id, string user_id, string status, DateTimeOffset created_at, decimal total, IReadOnlyList<OrderLine> items)
{
    public static OrderMessage FromImage(IReadOnlyDictionary<string, DynamoDBEvent.AttributeValue> image)
    {
        var lines = image.TryGetValue("items", out var items) && items.L is { } list
            ? list.Select(l => OrderLine.FromAttribute(l.M)).ToList()
            : new List<OrderLine>();
        return new OrderMessage(
            image["order_id"].S,
            image["user_id"].S,
            image["status"].S,
            DateTimeOffset.FromUnixTimeSeconds(long.Parse(image["created_at"].S, CultureInfo.InvariantCulture)),
            decimal.Parse(image["total"].S, CultureInfo.InvariantCulture),
            lines);
    }
}

public sealed record OrderLine(string ProductId, string Name, decimal Price, int Quantity)
{
    internal static OrderLine FromAttribute(IReadOnlyDictionary<string, DynamoDBEvent.AttributeValue> m) => new(
        m["product_id"].S,
        m["name"].S,
        decimal.Parse(m["price"].S, CultureInfo.InvariantCulture),
        int.Parse(m["quantity"].N, CultureInfo.InvariantCulture));
}
