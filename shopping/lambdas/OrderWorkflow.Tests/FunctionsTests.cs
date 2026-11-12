using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.DynamoDBEvents;
using Amazon.Lambda.SQSEvents;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using NSubstitute;
using Xunit;

namespace OrderWorkflow.Tests;

public class FunctionsTests
{
    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IAmazonSQS _sqs = Substitute.For<IAmazonSQS>();
    private readonly IAmazonStepFunctions _sfn = Substitute.For<IAmazonStepFunctions>();
    private readonly IAmazonEventBridge _events = Substitute.For<IAmazonEventBridge>();
    private readonly IAmazonS3 _s3 = Substitute.For<IAmazonS3>();
    private readonly Functions _fn;

    public FunctionsTests()
    {
        _fn = new Functions(_ddb, _sqs, _sfn, _events, _s3);
        _sqs.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>()).Returns(new SendMessageResponse());
        _events.PutEventsAsync(Arg.Any<PutEventsRequest>(), Arg.Any<CancellationToken>()).Returns(new PutEventsResponse());
        _ddb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>()).Returns(new UpdateItemResponse());
    }

    private static ILambdaContext Context()
    {
        var ctx = Substitute.For<ILambdaContext>();
        ctx.Logger.Returns(Substitute.For<ILambdaLogger>());
        return ctx;
    }

    private static DynamoDBEvent.AttributeValue S(string v) => new() { S = v };

    /// <summary>An order row as the checkout writes it: no payload copy.</summary>
    private static Dictionary<string, DynamoDBEvent.AttributeValue> OrderImage(string orderId = "o-1") => new()
    {
        ["order_id"] = S(orderId),
        ["user_id"] = S("u-1"),
        ["status"] = S("PLACED"),
        ["created_at"] = S("1760000000"),
        ["total"] = S("24.50"),
        ["ttl"] = new() { N = "1770000000" },
        ["items"] = new()
        {
            L =
            [
                new() { M = new() { ["product_id"] = S("a"), ["name"] = S("Item a"), ["price"] = S("10.00"), ["quantity"] = new() { N = "2" }, ["subtotal"] = S("20.00") } },
                new() { M = new() { ["product_id"] = S("b"), ["name"] = S("Item b"), ["price"] = S("4.50"), ["quantity"] = new() { N = "1" }, ["subtotal"] = S("4.50") } },
            ],
        },
    };

    private static DynamoDBEvent Stream(params (string Name, Dictionary<string, DynamoDBEvent.AttributeValue> Image)[] records) => new()
    {
        Records = records.Select((r, i) => new DynamoDBEvent.DynamodbStreamRecord
        {
            EventName = r.Name,
            Dynamodb = new DynamoDBEvent.StreamRecord { NewImage = r.Image, SequenceNumber = "seq-" + i },
        }).ToList(),
    };

    // ---------------- outbox ----------------

    [Fact]
    public async Task The_message_is_built_from_the_order_item_and_sent_to_the_queue_and_the_bus()
    {
        SendMessageRequest? queued = null;
        _sqs.SendMessageAsync(Arg.Do<SendMessageRequest>(r => queued = r), Arg.Any<CancellationToken>()).Returns(new SendMessageResponse());
        PutEventsRequest? announced = null;
        _events.PutEventsAsync(Arg.Do<PutEventsRequest>(r => announced = r), Arg.Any<CancellationToken>()).Returns(new PutEventsResponse());

        var res = await _fn.PublishOrder(Stream(("INSERT", OrderImage())), Context());

        Assert.Empty(res.BatchItemFailures);
        var msg = JsonDocument.Parse(queued!.MessageBody).RootElement;
        Assert.Equal("o-1", msg.GetProperty("order_id").GetString());
        Assert.Equal("u-1", msg.GetProperty("user_id").GetString());
        Assert.Equal(24.5m, msg.GetProperty("total").GetDecimal());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000000), msg.GetProperty("created_at").GetDateTimeOffset());
        var items = msg.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("a", items[0].GetProperty("productId").GetString());
        Assert.Equal(2, items[0].GetProperty("quantity").GetInt32());
        Assert.Equal(10m, items[0].GetProperty("price").GetDecimal());

        var entry = announced!.Entries.Single();
        Assert.Equal("order.placed", entry.DetailType);
        Assert.Equal(queued.MessageBody, entry.Detail); // same message on both
    }

    [Fact]
    public async Task Status_changes_and_ttl_deletes_are_not_new_orders()
    {
        var res = await _fn.PublishOrder(Stream(("MODIFY", OrderImage()), ("REMOVE", OrderImage())), Context());

        Assert.Empty(res.BatchItemFailures);
        await _sqs.DidNotReceiveWithAnyArgs().SendMessageAsync(default(SendMessageRequest)!, default);
    }

    [Fact]
    public async Task A_failed_publish_is_reported_and_stops_the_batch_so_the_stream_retries_in_order()
    {
        _sqs.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns<SendMessageResponse>(_ => throw new AmazonSQSException("down"));

        var res = await _fn.PublishOrder(Stream(("INSERT", OrderImage("o-1")), ("INSERT", OrderImage("o-2"))), Context());

        Assert.Equal(["seq-0"], res.BatchItemFailures.Select(f => f.ItemIdentifier));
        await _sqs.Received(1).SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void An_order_image_without_items_still_maps()
    {
        var image = OrderImage();
        image.Remove("items");

        var msg = OrderMessage.FromImage(image);

        Assert.Empty(msg.items);
        Assert.Equal(24.5m, msg.total);
    }

    // ---------------- worker ----------------

    private static SQSEvent Queue(string body, string id = "m-1") => new() { Records = [new SQSEvent.SQSMessage { MessageId = id, Body = body }] };

    [Fact]
    public async Task The_worker_marks_the_order_processing_and_starts_the_workflow()
    {
        StartExecutionRequest? started = null;
        _sfn.StartExecutionAsync(Arg.Do<StartExecutionRequest>(r => started = r), Arg.Any<CancellationToken>()).Returns(new StartExecutionResponse());
        UpdateItemRequest? update = null;
        _ddb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => update = r), Arg.Any<CancellationToken>()).Returns(new UpdateItemResponse());
        const string body = """{"order_id":"abcdef0123456789","user_id":"u"}""";

        var res = await _fn.ProcessOrder(Queue(body), Context());

        Assert.Empty(res.BatchItemFailures);
        Assert.Equal("#s IN (:f0, :f1)", update!.ConditionExpression); // only from PLACED (or PROCESSING, when a retry follows a failed start)
        Assert.Equal("PLACED", update.ExpressionAttributeValues[":f0"].S);
        Assert.Equal("order-abcdef012345", started!.Name);
        Assert.Equal(body, started.Input);
    }

    [Fact]
    public async Task A_redelivered_message_for_an_order_past_placed_is_skipped()
    {
        _ddb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns<UpdateItemResponse>(_ => throw new ConditionalCheckFailedException("not PLACED"));

        var res = await _fn.ProcessOrder(Queue("""{"order_id":"abcdef0123456789"}"""), Context());

        Assert.Empty(res.BatchItemFailures);
        await _sfn.DidNotReceiveWithAnyArgs().StartExecutionAsync(default!, default);
    }

    [Fact]
    public async Task A_message_that_fails_is_reported_per_message()
    {
        var res = await _fn.ProcessOrder(Queue("not json", "bad-1"), Context());

        Assert.Equal(["bad-1"], res.BatchItemFailures.Select(f => f.ItemIdentifier));
    }

    // ---------------- complete / audit ----------------

    [Fact]
    public async Task Completing_an_order_moves_it_from_processing_only()
    {
        UpdateItemRequest? update = null;
        _ddb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => update = r), Arg.Any<CancellationToken>()).Returns(new UpdateItemResponse());

        await _fn.CompleteOrder(JsonDocument.Parse("""{"order_id":"o-1"}""").RootElement, Context());

        Assert.Equal("PROCESSING", update!.ExpressionAttributeValues[":f0"].S);
        Assert.Equal("COMPLETED", update.ExpressionAttributeValues[":s"].S);
    }

    [Fact]
    public async Task Completing_an_order_that_is_not_processing_throws_so_the_workflow_retries_or_fails()
    {
        _ddb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns<UpdateItemResponse>(_ => throw new ConditionalCheckFailedException("no"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _fn.CompleteOrder(JsonDocument.Parse("""{"order_id":"o-1"}""").RootElement, Context()));
    }

    [Fact]
    public async Task Audit_archives_the_event_under_the_order_id()
    {
        PutObjectRequest? put = null;
        _s3.PutObjectAsync(Arg.Do<PutObjectRequest>(r => put = r), Arg.Any<CancellationToken>()).Returns(new PutObjectResponse());

        await _fn.AuditOrder(JsonDocument.Parse("""{"detail":{"order_id":"o-9"}}""").RootElement, Context());

        Assert.StartsWith("orders/", put!.Key);
        Assert.Contains("o-9", put.Key);
        Assert.Equal("application/json", put.ContentType);
    }
}
