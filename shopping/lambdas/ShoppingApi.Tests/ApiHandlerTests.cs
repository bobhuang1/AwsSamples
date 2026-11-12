using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using NSubstitute;
using Xunit;

namespace ShoppingApi.Tests;

public class ApiHandlerTests
{
    private const string User = "user-1";

    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly ApiHandler _handler;

    public ApiHandlerTests()
    {
        _handler = new ApiHandler(_ddb);
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(new QueryResponse { Items = [] });
    }

    // ---------------- helpers ----------------

    private static ILambdaContext Context()
    {
        var ctx = Substitute.For<ILambdaContext>();
        ctx.Logger.Returns(Substitute.For<ILambdaLogger>());
        return ctx;
    }

    private static APIGatewayHttpApiV2ProxyRequest Http(string method, string path, string? body = null, string? sub = User,
        Dictionary<string, string>? headers = null)
    {
        var request = new APIGatewayHttpApiV2ProxyRequest
        {
            RawPath = path,
            Body = body,
            Headers = headers,
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                Stage = "$default",
                Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription { Method = method, Path = path },
            },
        };
        if (sub is not null)
        {
            request.RequestContext.Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
            {
                Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription { Claims = new Dictionary<string, string> { ["sub"] = sub } },
            };
        }
        return request;
    }

    private static AttributeValue S(string v) => new() { S = v };
    private static AttributeValue N(long v) => new() { N = v.ToString() };

    private static Dictionary<string, AttributeValue> Line(string productId, int quantity, string price = "10.00", long updated = 100) => new()
    {
        ["user_id"] = S(User), ["product_id"] = S(productId), ["name"] = S("Item " + productId),
        ["price"] = S(price), ["quantity"] = N(quantity), ["updated_at"] = N(updated),
    };

    private void GivenCart(params Dictionary<string, AttributeValue>[] lines)
        => _ddb.QueryAsync(Arg.Is<QueryRequest>(q => q.TableName == "carts"), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = lines.ToList() });

    private void GivenProduct(string id, string price = "10.00", int stock = 5)
        => _ddb.GetItemAsync(Arg.Is<GetItemRequest>(r => r.TableName == "products" && r.Key["product_id"].S == id), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse
            {
                Item = new() { ["product_id"] = S(id), ["name"] = S("Item " + id), ["price"] = S(price), ["stock"] = N(stock) },
            });

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    // ---------------- add to cart ----------------

    [Fact]
    public async Task Adding_to_the_cart_is_one_atomic_update_and_never_reads_then_writes_the_cart()
    {
        GivenProduct("sku-1", "49.99");
        UpdateItemRequest? update = null;
        _ddb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => update = r), Arg.Any<CancellationToken>()).Returns(new UpdateItemResponse());
        GivenCart(Line("sku-1", 2, "49.99"));

        var res = await _handler.Handle(Http("POST", "/cart/user-1", """{"productId":"sku-1","quantity":2}"""), Context());

        Assert.Equal(200, res.StatusCode);
        Assert.Equal("carts", update!.TableName);
        Assert.Equal("user-1", update.Key["user_id"].S);
        Assert.Equal("sku-1", update.Key["product_id"].S);
        Assert.Contains("ADD quantity :q", update.UpdateExpression); // atomic increment, no read-modify-write
        Assert.Equal("49.99", update.ExpressionAttributeValues[":p"].S); // price from the catalog
        Assert.NotNull(update.ExpressionAttributeValues[":t"].N); // ttl is written on every change
        await _ddb.DidNotReceiveWithAnyArgs().PutItemAsync(default(PutItemRequest)!, default);
        await _ddb.DidNotReceive().GetItemAsync(Arg.Is<GetItemRequest>(r => r.TableName == "carts"), Arg.Any<CancellationToken>());
        await _ddb.Received(1).UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Concurrent_adds_each_issue_their_own_increment()
    {
        GivenProduct("sku-1");
        var seen = new List<string>();
        _ddb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => seen.Add(r.ExpressionAttributeValues[":q"].N)), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());

        await Task.WhenAll(
            _handler.Handle(Http("POST", "/cart/user-1", """{"productId":"sku-1","quantity":1}"""), Context()),
            _handler.Handle(Http("POST", "/cart/user-1", """{"productId":"sku-1","quantity":3}"""), Context()));

        Assert.Equal(["1", "3"], seen.Order().ToList()); // both counted; nothing overwrites a whole cart
    }

    [Fact]
    public async Task A_line_that_would_pass_the_cap_is_pinned_at_99()
    {
        GivenProduct("sku-1");
        var calls = new List<UpdateItemRequest>();
        _ddb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => calls.Add(r)), Arg.Any<CancellationToken>())
            .Returns(call => calls.Count == 1
                ? throw new ConditionalCheckFailedException("over the cap")
                : Task.FromResult(new UpdateItemResponse()));

        var res = await _handler.Handle(Http("POST", "/cart/user-1", """{"productId":"sku-1","quantity":50}"""), Context());

        Assert.Equal(200, res.StatusCode);
        Assert.Equal(2, calls.Count);
        Assert.Equal("49", calls[0].ExpressionAttributeValues[":room"].N); // 99 - 50
        Assert.Equal("99", calls[1].ExpressionAttributeValues[":max"].N);
        Assert.Null(calls[1].ConditionExpression);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"quantity":1}""")]
    [InlineData("""{"productId":"sku-1","quantity":0}""")]
    [InlineData("""{"productId":"sku-1","quantity":100}""")]
    public async Task Bad_add_requests_are_a_400_not_a_500(string? body)
    {
        var res = await _handler.Handle(Http("POST", "/cart/user-1", body), Context());

        Assert.Equal(400, res.StatusCode);
        await _ddb.DidNotReceiveWithAnyArgs().UpdateItemAsync(default(UpdateItemRequest)!, default);
    }

    [Fact]
    public async Task Adding_an_unknown_product_is_a_404()
    {
        _ddb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>()).Returns(new GetItemResponse());

        var res = await _handler.Handle(Http("POST", "/cart/user-1", """{"productId":"nope","quantity":1}"""), Context());

        Assert.Equal(404, res.StatusCode);
    }

    [Fact]
    public async Task Another_shoppers_cart_is_forbidden_and_a_missing_token_is_unauthorized()
    {
        Assert.Equal(403, (await _handler.Handle(Http("GET", "/cart/someone-else"), Context())).StatusCode);
        Assert.Equal(401, (await _handler.Handle(Http("GET", "/cart/user-1", sub: null), Context())).StatusCode);
    }

    // ---------------- cart read / remove ----------------

    [Fact]
    public async Task The_cart_is_the_shoppers_lines()
    {
        GivenCart(Line("b", 1, updated: 120), Line("a", 2, "5.50", updated: 150));

        var res = await _handler.Handle(Http("GET", "/cart/user-1"), Context());

        var cart = Json(res.Body);
        Assert.Equal("150", cart.GetProperty("updated_at").GetString());
        Assert.Equal(["a", "b"], cart.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("productId").GetString()));
        await _ddb.Received().QueryAsync(Arg.Is<QueryRequest>(q => q.ConsistentRead == true && q.KeyConditionExpression == "user_id = :u"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Removing_a_line_deletes_just_that_item()
    {
        DeleteItemRequest? del = null;
        _ddb.DeleteItemAsync(Arg.Do<DeleteItemRequest>(r => del = r), Arg.Any<CancellationToken>()).Returns(new DeleteItemResponse());

        var res = await _handler.Handle(Http("DELETE", "/cart/user-1/items/sku-1"), Context());

        Assert.Equal(200, res.StatusCode);
        Assert.Equal("sku-1", del!.Key["product_id"].S);
        Assert.Equal("user-1", del.Key["user_id"].S);
    }

    // ---------------- checkout ----------------

    [Fact]
    public async Task Checkout_places_the_order_empties_the_cart_and_takes_the_stock_in_one_transaction()
    {
        GivenCart(Line("a", 2, "10.00"), Line("b", 1, "4.50"));
        TransactWriteItemsRequest? sent = null;
        _ddb.TransactWriteItemsAsync(Arg.Do<TransactWriteItemsRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new TransactWriteItemsResponse());

        var res = await _handler.Handle(Http("POST", "/orders"), Context());

        Assert.Equal(201, res.StatusCode);
        Assert.Equal("24.50", Json(res.Body).GetProperty("total").GetDecimal().ToString("0.00"));
        Assert.Equal(5, sent!.TransactItems.Count); // order + 2 cart lines + 2 stock updates

        var order = sent.TransactItems[0].Put;
        Assert.Equal("orders", order.TableName);
        Assert.Equal("attribute_not_exists(order_id)", order.ConditionExpression);
        Assert.False(order.Item.ContainsKey("payload")); // no second copy of the order
        Assert.Equal(2, order.Item["items"].L.Count);
        Assert.Equal("PLACED", order.Item["status"].S);
        Assert.Equal("24.50", order.Item["total"].S);

        var deletes = sent.TransactItems.Where(i => i.Delete is not null).Select(i => i.Delete).ToList();
        Assert.All(deletes, d => Assert.Equal("quantity = :q", d.ConditionExpression)); // only the lines we priced
        var stock = sent.TransactItems.Where(i => i.Update is not null).Select(i => i.Update).ToList();
        Assert.Equal(2, stock.Count);
        Assert.All(stock, u =>
        {
            Assert.Equal("products", u.TableName);
            Assert.Equal("stock >= :q", u.ConditionExpression);
            Assert.Equal("SET stock = stock - :q", u.UpdateExpression);
        });
        Assert.Equal("2", stock.Single(u => u.Key["product_id"].S == "a").ExpressionAttributeValues[":q"].N);
    }

    [Fact]
    public async Task Checkout_fails_cleanly_when_an_item_is_out_of_stock()
    {
        GivenCart(Line("a", 2), Line("b", 1));
        _ddb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .Returns<TransactWriteItemsResponse>(_ => throw new TransactionCanceledException("cancelled")
            {
                CancellationReasons =
                [
                    new() { Code = "None" },                       // order
                    new() { Code = "None" }, new() { Code = "None" }, // cart lines
                    new() { Code = "None" },                       // stock a
                    new() { Code = "ConditionalCheckFailed" },     // stock b
                ],
            });

        var res = await _handler.Handle(Http("POST", "/orders"), Context());

        Assert.Equal(409, res.StatusCode);
        var body = Json(res.Body);
        Assert.Contains("out of stock", body.GetProperty("error").GetString());
        Assert.Equal(["b"], body.GetProperty("outOfStock").EnumerateArray().Select(i => i.GetProperty("productId").GetString()));
    }

    [Fact]
    public async Task Checkout_reports_a_changed_cart_as_a_retryable_conflict()
    {
        GivenCart(Line("a", 2));
        _ddb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .Returns<TransactWriteItemsResponse>(_ => throw new TransactionCanceledException("cancelled")
            {
                CancellationReasons = [new() { Code = "None" }, new() { Code = "ConditionalCheckFailed" }, new() { Code = "None" }],
            });

        var res = await _handler.Handle(Http("POST", "/orders"), Context());

        Assert.Equal(409, res.StatusCode);
        Assert.Contains("cart changed", Json(res.Body).GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_empty_cart_cannot_be_checked_out()
    {
        var res = await _handler.Handle(Http("POST", "/orders"), Context());

        Assert.Equal(400, res.StatusCode);
        await _ddb.DidNotReceiveWithAnyArgs().TransactWriteItemsAsync(default!, default);
    }

    [Fact]
    public async Task A_cart_too_large_for_one_transaction_is_refused_before_anything_is_written()
    {
        GivenCart(Enumerable.Range(0, ApiHandler.MaxLinesAtCheckout + 1).Select(i => Line("sku-" + i, 1)).ToArray());

        var res = await _handler.Handle(Http("POST", "/orders"), Context());

        Assert.Equal(400, res.StatusCode);
        await _ddb.DidNotReceiveWithAnyArgs().TransactWriteItemsAsync(default!, default);
    }

    [Fact]
    public async Task A_retry_with_the_same_idempotency_key_returns_the_first_order_without_touching_stock()
    {
        _ddb.GetItemAsync(Arg.Is<GetItemRequest>(r => r.TableName == "orders"), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse { Item = new() { ["order_id"] = S("o-1"), ["total"] = S("24.50"), ["status"] = S("PROCESSING") } });

        var res = await _handler.Handle(Http("POST", "/orders", headers: new() { ["idempotency-key"] = "k-1" }), Context());

        Assert.Equal(200, res.StatusCode);
        Assert.Equal("o-1", Json(res.Body).GetProperty("orderId").GetString());
        await _ddb.DidNotReceiveWithAnyArgs().TransactWriteItemsAsync(default!, default);
    }

    [Fact]
    public async Task Checkout_needs_a_signed_in_shopper()
    {
        var res = await _handler.Handle(Http("POST", "/orders", sub: null), Context());

        Assert.Equal(401, res.StatusCode);
    }

    // ---------------- catalog / routing ----------------

    [Fact]
    public async Task The_catalog_lists_products_with_stock_and_a_short_cache()
    {
        _ddb.ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ScanResponse { Items = [new() { ["product_id"] = S("a"), ["name"] = S("A"), ["price"] = N(5), ["stock"] = N(3) }] });

        var res = await _handler.Handle(Http("GET", "/products", sub: null), Context());

        Assert.Equal(200, res.StatusCode);
        Assert.Equal("public, max-age=30", res.Headers["cache-control"]);
        var p = Json(res.Body)[0];
        Assert.Equal(5m, p.GetProperty("price").GetDecimal());
        Assert.Equal(3, p.GetProperty("stock").GetInt32());
    }

    [Fact]
    public async Task A_named_stage_prefix_is_ignored_when_routing()
    {
        _ddb.ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>()).Returns(new ScanResponse { Items = [] });
        var request = Http("GET", "/v1/products", sub: null);
        request.RequestContext.Stage = "v1";

        var res = await _handler.Handle(request, Context());

        Assert.Equal(200, res.StatusCode);
    }

    [Fact]
    public async Task A_storage_failure_is_a_generic_500()
    {
        _ddb.ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>()).Returns<ScanResponse>(_ => throw new InvalidOperationException("secret detail"));

        var res = await _handler.Handle(Http("GET", "/products", sub: null), Context());

        Assert.Equal(500, res.StatusCode);
        Assert.DoesNotContain("secret", res.Body);
    }
}
