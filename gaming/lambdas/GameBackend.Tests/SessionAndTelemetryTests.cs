using System.Text;
using System.Text.Json;
using Amazon.ApiGatewayManagementApi;
using Amazon.ApiGatewayManagementApi.Model;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.S3;
using Amazon.S3.Model;
using NSubstitute;
using Xunit;
using static GameBackend.Tests.TestSupport;

namespace GameBackend.Tests;

public class SessionHandlerTests
{
    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IAmazonApiGatewayManagementApi _api = Substitute.For<IAmazonApiGatewayManagementApi>();

    private static APIGatewayProxyRequest Ws(string route, string connectionId, string? body = null, string? userId = null, string? matchId = null)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = body,
            QueryStringParameters = matchId is null ? null : new Dictionary<string, string> { ["matchId"] = matchId },
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { RouteKey = route, ConnectionId = connectionId },
        };
        if (userId is not null)
            request.RequestContext.Authorizer = new APIGatewayCustomAuthorizerContext { ["userId"] = userId };
        return request;
    }

    [Fact]
    public async Task A_player_outside_the_match_cannot_connect()
    {
        _ddb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse { Item = new() { ["match_id"] = S("m1"), ["players"] = new AttributeValue { SS = ["alice", "bob"] } } });
        var handler = new SessionHandler(_ddb, _api);

        var res = await handler.Handle(Ws("$connect", "c1", userId: "mallory", matchId: "m1"), Context());

        Assert.Equal(403, res.StatusCode);
        await _ddb.DidNotReceiveWithAnyArgs().PutItemAsync(default!, default);
    }

    [Fact]
    public async Task The_match_is_read_consistently_on_connect_and_the_record_outlives_a_two_hour_socket()
    {
        GetItemRequest? read = null;
        _ddb.GetItemAsync(Arg.Do<GetItemRequest>(r => read = r), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse { Item = new() { ["match_id"] = S("m1"), ["players"] = new AttributeValue { SS = ["alice"] } } });
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(new QueryResponse { Count = 0 });
        PutItemRequest? put = null;
        _ddb.PutItemAsync(Arg.Do<PutItemRequest>(r => put = r), Arg.Any<CancellationToken>()).Returns(new PutItemResponse());

        await new SessionHandler(_ddb, _api).Handle(Ws("$connect", "c1", userId: "alice", matchId: "m1"), Context());

        Assert.True(read!.ConsistentRead);
        var ttl = long.Parse(put!.Item["ttl"].N);
        Assert.True(ttl - DateTimeOffset.UtcNow.ToUnixTimeSeconds() > 2 * 3600);
    }

    [Fact]
    public async Task A_player_cannot_hold_more_than_five_sockets()
    {
        _ddb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse { Item = new() { ["match_id"] = S("m1"), ["players"] = new AttributeValue { SS = ["alice"] } } });
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(new QueryResponse { Count = 5 });

        var res = await new SessionHandler(_ddb, _api).Handle(Ws("$connect", "c6", userId: "alice", matchId: "m1"), Context());

        Assert.Equal(429, res.StatusCode);
        await _ddb.DidNotReceiveWithAnyArgs().PutItemAsync(default!, default);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("""{"action":5}""")]
    [InlineData("{not json")]
    public async Task A_message_of_the_wrong_shape_is_a_400_not_a_500(string body)
    {
        var res = await new SessionHandler(_ddb, _api).Handle(Ws("$default", "c1", body), Context());

        Assert.Equal(400, res.StatusCode);
    }

    [Fact]
    public async Task A_player_of_the_match_is_registered_on_connect()
    {
        _ddb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse { Item = new() { ["match_id"] = S("m1"), ["players"] = new AttributeValue { SS = ["alice", "bob"] } } });
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(new QueryResponse { Count = 0 });
        PutItemRequest? put = null;
        _ddb.PutItemAsync(Arg.Do<PutItemRequest>(r => put = r), Arg.Any<CancellationToken>()).Returns(new PutItemResponse());
        var handler = new SessionHandler(_ddb, _api);

        var res = await handler.Handle(Ws("$connect", "c1", userId: "alice", matchId: "m1"), Context());

        Assert.Equal(200, res.StatusCode);
        Assert.Equal("alice", put!.Item["player_id"].S);
        Assert.Equal("m1", put.Item["match_id"].S);
    }

    [Fact]
    public async Task Events_are_relayed_to_the_other_sockets_of_the_match_and_stale_ones_are_pruned()
    {
        _ddb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse { Item = new() { ["player_id"] = S("alice"), ["match_id"] = S("m1") } });
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [new() { ["connection_id"] = S("c1") }, new() { ["connection_id"] = S("c2") }, new() { ["connection_id"] = S("c3") }] });
        var posted = new List<string>();
        _api.PostToConnectionAsync(Arg.Do<PostToConnectionRequest>(r => posted.Add(r.ConnectionId)), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<PostToConnectionRequest>().ConnectionId == "c3"
                ? throw new GoneException("gone")
                : Task.FromResult(new PostToConnectionResponse()));
        var handler = new SessionHandler(_ddb, _api);

        var res = await handler.Handle(Ws("$default", "c1", """{"action":"move","payload":{"x":1}}"""), Context());

        Assert.Equal(200, res.StatusCode);
        Assert.DoesNotContain("c1", posted); // not echoed to the sender
        Assert.Contains("c2", posted);
        await _ddb.Received(1).DeleteItemAsync(Arg.Is<DeleteItemRequest>(d => d.Key["connection_id"].S == "c3"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_connection_cannot_send()
    {
        _ddb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>()).Returns(new GetItemResponse());
        var handler = new SessionHandler(_ddb, _api);

        var res = await handler.Handle(Ws("$default", "ghost", """{"action":"move"}"""), Context());

        Assert.Equal(401, res.StatusCode);
    }
}

public class TelemetryConsumerTests
{
    private static JsonElement Kinesis(params string[] base64) => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        Records = base64.Select(d => new { kinesis = new { data = d } }),
    })).RootElement;

    [Fact]
    public async Task Valid_records_are_archived_as_one_jsonl_object_and_bad_ones_are_skipped()
    {
        var s3 = Substitute.For<IAmazonS3>();
        PutObjectRequest? put = null;
        s3.PutObjectAsync(Arg.Do<PutObjectRequest>(r => put = r), Arg.Any<CancellationToken>()).Returns(new PutObjectResponse());
        var good = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"event\":\"a\"}\n{\"event\":\"b\"}"));

        await new TelemetryConsumer(s3).Handle(Kinesis(good, "!!not base64!!"), Context());

        Assert.NotNull(put);
        Assert.Equal("{\"event\":\"a\"}\n{\"event\":\"b\"}", put!.ContentBody);
        Assert.StartsWith("telemetry/", put.Key);
    }

    [Fact]
    public async Task A_batch_with_nothing_valid_writes_nothing()
    {
        var s3 = Substitute.For<IAmazonS3>();

        await new TelemetryConsumer(s3).Handle(Kinesis("!!not base64!!"), Context());

        await s3.DidNotReceiveWithAnyArgs().PutObjectAsync((PutObjectRequest)default!, default);
    }
}
