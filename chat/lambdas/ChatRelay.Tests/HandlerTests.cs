using System.Text.Json;
using Amazon.ApiGatewayManagementApi;
using Amazon.ApiGatewayManagementApi.Model;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SQS;
using Amazon.SQS.Model;
using NSubstitute;
using Xunit;

namespace ChatRelay.Tests;

internal static class Support
{
    public static ILambdaContext Context()
    {
        var ctx = Substitute.For<ILambdaContext>();
        ctx.Logger.Returns(Substitute.For<ILambdaLogger>());
        return ctx;
    }

    public static APIGatewayProxyRequest Ws(string route, string connectionId, string? body = null, string? userId = null,
        Dictionary<string, string>? query = null)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = body,
            QueryStringParameters = query,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { RouteKey = route, ConnectionId = connectionId },
        };
        if (userId is not null)
            request.RequestContext.Authorizer = new APIGatewayCustomAuthorizerContext { ["userId"] = userId };
        return request;
    }

    public static AttributeValue S(string v) => new() { S = v };
    public static AttributeValue N(long v) => new() { N = v.ToString() };
}

public class MessageHandlerTests
{
    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IAmazonSQS _sqs = Substitute.For<IAmazonSQS>();
    private readonly MessageHandler _handler;

    public MessageHandlerTests()
    {
        _handler = new MessageHandler(_ddb, _sqs);
        _sqs.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>()).Returns(new SendMessageResponse());
        _ddb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>()).Returns(new PutItemResponse());
        _ddb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>()).Returns(new UpdateItemResponse());
    }

    private void GivenConnection(long ttl)
        => _ddb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>()).Returns(new GetItemResponse
        {
            Item = new()
            {
                ["connection_id"] = Support.S("c1"), ["room_id"] = Support.S("room-1"), ["user_id"] = Support.S("u-1"),
                ["nickname"] = Support.S("Ann"), ["ttl"] = Support.N(ttl),
            },
        });

    private static long InHours(int hours) => DateTimeOffset.UtcNow.AddHours(hours).ToUnixTimeSeconds();

    [Fact]
    public async Task Connect_registers_the_socket_for_the_authorized_user_and_room()
    {
        PutItemRequest? put = null;
        _ddb.PutItemAsync(Arg.Do<PutItemRequest>(r => put = r), Arg.Any<CancellationToken>()).Returns(new PutItemResponse());

        var res = await _handler.Handle(
            Support.Ws("$connect", "c1", userId: "u-1", query: new() { ["roomId"] = "room-1", ["nickname"] = "Ann" }), Support.Context());

        Assert.Equal(200, res.StatusCode);
        Assert.Equal("u-1", put!.Item["user_id"].S);
        Assert.Equal("room-1", put.Item["room_id"].S);
        Assert.Equal("Ann", put.Item["nickname"].S);
        Assert.NotNull(put.Item["ttl"].N);
    }

    [Fact]
    public async Task Connect_without_an_authorized_user_is_refused()
    {
        var res = await _handler.Handle(Support.Ws("$connect", "c1", query: new() { ["roomId"] = "room-1" }), Support.Context());

        Assert.Equal(401, res.StatusCode);
        await _ddb.DidNotReceiveWithAnyArgs().PutItemAsync(default(PutItemRequest)!, default);
    }

    [Fact]
    public async Task Connect_needs_a_reasonable_room_id()
    {
        Assert.Equal(400, (await _handler.Handle(Support.Ws("$connect", "c1", userId: "u-1"), Support.Context())).StatusCode);
        Assert.Equal(400, (await _handler.Handle(
            Support.Ws("$connect", "c1", userId: "u-1", query: new() { ["roomId"] = new string('r', 65) }), Support.Context())).StatusCode);
    }

    [Fact]
    public async Task A_message_is_stored_and_queued_for_fanout_with_the_identity_of_the_connection()
    {
        GivenConnection(InHours(6));
        PutItemRequest? stored = null;
        _ddb.PutItemAsync(Arg.Do<PutItemRequest>(r => stored = r), Arg.Any<CancellationToken>()).Returns(new PutItemResponse());
        SendMessageRequest? queued = null;
        _sqs.SendMessageAsync(Arg.Do<SendMessageRequest>(r => queued = r), Arg.Any<CancellationToken>()).Returns(new SendMessageResponse());

        // "user" in the body is ignored: sender and room come from the connection record.
        var res = await _handler.Handle(Support.Ws("$default", "c1", """{"action":"send","text":"hello","user":"mallory","roomId":"other"}"""), Support.Context());

        Assert.Equal(200, res.StatusCode);
        Assert.Equal("u-1", stored!.Item["user_id"].S);
        Assert.Equal("room-1", stored.Item["room_id"].S);
        Assert.Equal("hello", stored.Item["text"].S);
        var msg = JsonDocument.Parse(queued!.MessageBody).RootElement;
        Assert.Equal("room-1", msg.GetProperty("roomId").GetString());
        Assert.Equal("u-1", msg.GetProperty("userId").GetString());
    }

    [Fact]
    public async Task Empty_oversized_and_unsupported_messages_are_rejected_without_writes()
    {
        GivenConnection(InHours(6));
        var tooLong = new string('x', 2001);

        foreach (var body in new[] { """{"action":"send","text":"  "}""", $$"""{"action":"send","text":"{{tooLong}}"}""", """{"action":"delete"}""" })
        {
            var res = await _handler.Handle(Support.Ws("$default", "c1", body), Support.Context());
            Assert.Contains("error", res.Body);
        }

        await _sqs.DidNotReceiveWithAnyArgs().SendMessageAsync(default(SendMessageRequest)!, default);
        await _ddb.DidNotReceiveWithAnyArgs().PutItemAsync(default(PutItemRequest)!, default);
    }

    [Fact]
    public async Task An_unknown_connection_cannot_send()
    {
        _ddb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>()).Returns(new GetItemResponse());

        var res = await _handler.Handle(Support.Ws("$default", "ghost", """{"action":"send","text":"hi"}"""), Support.Context());

        Assert.Equal(401, res.StatusCode);
    }

    [Fact]
    public async Task The_presence_lease_is_refreshed_only_when_half_of_it_is_gone()
    {
        GivenConnection(InHours(5)); // 5 of 6 hours left: no write
        await _handler.Handle(Support.Ws("$default", "c1", """{"action":"send","text":"hi"}"""), Support.Context());
        await _ddb.DidNotReceiveWithAnyArgs().UpdateItemAsync(default(UpdateItemRequest)!, default);

        GivenConnection(InHours(1)); // 1 of 6 hours left: refresh
        await _handler.Handle(Support.Ws("$default", "c1", """{"action":"send","text":"hi"}"""), Support.Context());
        await _ddb.Received(1).UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disconnect_removes_the_socket()
    {
        DeleteItemRequest? del = null;
        _ddb.DeleteItemAsync(Arg.Do<DeleteItemRequest>(r => del = r), Arg.Any<CancellationToken>()).Returns(new DeleteItemResponse());

        var res = await _handler.Handle(Support.Ws("$disconnect", "c1"), Support.Context());

        Assert.Equal(200, res.StatusCode);
        Assert.Equal("c1", del!.Key["connection_id"].S);
    }
}

public class FanoutConsumerTests
{
    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IAmazonApiGatewayManagementApi _api = Substitute.For<IAmazonApiGatewayManagementApi>();

    private static SQSEvent Queue(string body, string id = "m-1") => new() { Records = [new SQSEvent.SQSMessage { MessageId = id, Body = body }] };

    private const string Message =
        """{"MessageId":"msg-1","RoomId":"room-1","UserId":"u-1","Nickname":"Ann","Text":"hello","CreatedAt":1760000000000}""";

    private void GivenRoom(params string[] connectionIds)
        => _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = connectionIds.Select(c => new Dictionary<string, AttributeValue> { ["connection_id"] = Support.S(c) }).ToList() });

    [Fact]
    public async Task The_message_reaches_every_socket_in_the_room()
    {
        GivenRoom("c1", "c2", "c3");
        var posted = new List<string>();
        string? payload = null;
        _api.PostToConnectionAsync(Arg.Do<PostToConnectionRequest>(r =>
        {
            lock (posted) posted.Add(r.ConnectionId);
            payload = new StreamReader(new MemoryStream(((MemoryStream)r.Data).ToArray())).ReadToEnd();
        }), Arg.Any<CancellationToken>()).Returns(new PostToConnectionResponse());

        var res = await new FanoutConsumer(_ddb, _api).Handle(Queue(Message), Support.Context());

        Assert.Empty(res.BatchItemFailures);
        Assert.Equal(["c1", "c2", "c3"], posted.Order());
        var sent = JsonDocument.Parse(payload!).RootElement.GetProperty("message");
        Assert.Equal("msg-1", sent.GetProperty("message_id").GetString());
        Assert.Equal("hello", sent.GetProperty("text").GetString());
        await _ddb.Received().QueryAsync(Arg.Is<QueryRequest>(q => q.IndexName == "room-index" && q.ExpressionAttributeValues[":r"].S == "room-1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_room_larger_than_one_page_is_read_to_the_end()
    {
        var calls = 0;
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(_ => calls++ == 0
            ? new QueryResponse { Items = [new() { ["connection_id"] = Support.S("c1") }], LastEvaluatedKey = new() { ["connection_id"] = Support.S("c1") } }
            : new QueryResponse { Items = [new() { ["connection_id"] = Support.S("c2") }] });
        var posted = new List<string>();
        _api.PostToConnectionAsync(Arg.Do<PostToConnectionRequest>(r => { lock (posted) posted.Add(r.ConnectionId); }), Arg.Any<CancellationToken>())
            .Returns(new PostToConnectionResponse());

        await new FanoutConsumer(_ddb, _api).Handle(Queue(Message), Support.Context());

        Assert.Equal(["c1", "c2"], posted.Order());
    }

    [Fact]
    public async Task A_gone_socket_is_pruned_and_one_failing_socket_does_not_fail_the_message()
    {
        GivenRoom("c1", "c2", "c3");
        _api.PostToConnectionAsync(Arg.Any<PostToConnectionRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            return call.Arg<PostToConnectionRequest>().ConnectionId switch
            {
                "c2" => throw new GoneException("gone"),
                "c3" => throw new AmazonApiGatewayManagementApiException("throttled"),
                _ => Task.FromResult(new PostToConnectionResponse()),
            };
        });

        var res = await new FanoutConsumer(_ddb, _api).Handle(Queue(Message), Support.Context());

        Assert.Empty(res.BatchItemFailures); // no redelivery: c1 would see the message twice
        await _ddb.Received(1).DeleteItemAsync(Arg.Is<DeleteItemRequest>(d => d.Key["connection_id"].S == "c2"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failure_to_read_the_room_is_reported_for_that_message_only()
    {
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns<QueryResponse>(_ => throw new AmazonDynamoDBException("down"));

        var res = await new FanoutConsumer(_ddb, _api).Handle(Queue(Message, "m-9"), Support.Context());

        Assert.Equal(["m-9"], res.BatchItemFailures.Select(f => f.ItemIdentifier));
    }

    [Fact]
    public async Task An_empty_room_posts_nothing()
    {
        GivenRoom();

        var res = await new FanoutConsumer(_ddb, _api).Handle(Queue(Message), Support.Context());

        Assert.Empty(res.BatchItemFailures);
        await _api.DidNotReceiveWithAnyArgs().PostToConnectionAsync(default!, default);
    }
}
