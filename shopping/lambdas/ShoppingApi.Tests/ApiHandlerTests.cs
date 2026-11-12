using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using NSubstitute;
using NSubstitute.Core;
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

    private static Dictionary<string, AttributeValue> Line(string productId, int quantity, string price = "10.00") => new()
    {
        ["product_id"] = S(productId), ["name"] = S("Item " + productId), ["price"] = S(price), ["quantity"] = N(quantity),
    };

    /// <summary>The shopper's single cart item: a map of lines keyed by product id.</summary>
    private void GivenCart(long version = 7, long updated = 100, params Dictionary<string, AttributeValue>[] lines)
        => _ddb.GetItemAsync(Arg.Is<GetItemRequest>(r => r.TableName == "carts"), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse
            {
                Item = lines.Length == 0 ? [] : new()
                {
                    ["user_id"] = S(User), ["version"] = N(version), ["updated_at"] = N(updated),
                    ["lines"] = new() { M = lines.ToDictionary(l => l["product_id"].S, l => new AttributeValue { M = l }) },
                },
            });

    private void GivenProduct(string id, string price = "10.00", int stock = 5)
        => _ddb.GetItemAsync(Arg.Is<GetItemRequest>(r => r.TableName == "products" && r.Key["product_id"].S == id), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse
            {
                Item = new() { ["product_id"] = S(id), ["name"] = S("Item " + id), ["price"] = S(price), ["stock"] = N(stock) },
            });

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    // ---------------- add to cart ----------------

    private static Func<CallInfo, Task<UpdateItemResponse>> FailFirst(int failures, List<UpdateItemRequest> seen) => call =>
    {
        seen.Add(call.Arg<UpdateItemRequest>());
        return seen.Count <= failures
            ? throw new ConditionalCheckFailedException("condition not met")
            : Task.FromResult(new UpdateItemResponse());
    };

    [Fact]
    public async Task Adding_a_line_that_is_in_the_cart_is_one_atomic_update_and_never_reads_then_writes_the_cart()
    {
        GivenProduct("sku-1", "49.99");
        UpdateItemRequest? update = null;
        _ddb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => update = r), Arg.Any<CancellationToken>()).Returns(new UpdateItemResponse());
        GivenCart(lines: Line("sku-1", 2, "49.99"));

        var res = await _handler.Handle(Http("POST", "/cart/user-1", """{"productId":"sku-1","quantity":2}"""), Context());

        Assert.Equal(200, res.StatusCode);
        Assert.Equal("carts", update!.TableName);
        Assert.Equal(["user_id"], update.Key.Keys); // the carts key schema is just user_id
        Assert.Equal("user-1", update.Key["user_id"].S);
        Assert.Equal("sku-1", update.ExpressionAttributeNames["#p"]);
        Assert.Contains("#l.#p.#q = #l.#p.#q + :q", update.UpdateExpression); // atomic increment inside the map
        Assert.Contains("ADD version :one", update.UpdateExpression);
        Assert.Equal("49.99", update.ExpressionAttributeValues[":pr"].S); // price from the catalog
        Assert.NotNull(update.ExpressionAttributeValues[":t"].N); // ttl is written on every change
        await _ddb.DidNotReceiveWithAnyArgs().PutItemAsync(default(PutItemRequest)!, default);
        await _ddb.Received(1).UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_product_that_is_not_in_the_cart_yet_is_added_as_a_new_line()
    {
        GivenProduct("sku-1");
        GivenCart(lines: Line("sku-1", 1));
        var seen = new List<UpdateItemRequest>();
        _ddb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>()).Returns(FailFirst(1, seen)); // increment fails: no such line

        var res = await _handler.Handle(Http("POST", "/cart/user-1", """{"productId":"sku-1","quantity":2}"""), Context());

        Assert.Equal(200, res.StatusCode);
        Assert.Equal(2, seen.Count);
        Assert.Equal("SET #l.#p = :line, updated_at = :u, #t = :t ADD version :one", seen[1].UpdateExpression);
        Assert.Equal("attribute_exists(#l) AND attribute_not_exists(#l.#p)", seen[1].ConditionExpression);
        Assert.Equal("2", seen[1].ExpressionAttributeValues[":line"].M["quantity"].N);
    }

    [Fact]
    public async Task The_first_line_of_a_new_cart_creates_the_lines_map()
    {
        GivenProduct("sku-1");
        GivenCart(lines: Line("sku-1", 1));
        var seen = new List<UpdateItemRequest>();
        _ddb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>()).Returns(FailFirst(2, seen));

        var res = await _handler.Handle(Http("POST", "/cart/user-1", """{"productId":"sku-1","quantity":1}"""), Context());

        Assert.Equal(200, res.StatusCode);
        Assert.Equal(3, seen.Count);
        Assert.Equal("attribute_not_exists(#l)", seen[2].ConditionExpression);
        Assert.True(seen[2].ExpressionAttributeValues[":lines"].M.ContainsKey("sku-1"));
    }

    [Fact]
    public async Task Concurrent_adds_each_issue_their_own_increment()
    {
        GivenProduct("sku-1");
        GivenCart();
        var seen = new List<string>();
        _ddb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => { lock (seen) seen.Add(r.ExpressionAttributeValues[":q"].N); }), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());

        await Task.WhenAll(
            _handler.Handle(Http("POST", "/cart/user-1", """{"productId":"sku-1","quantity":1}"""), Context()),
            _handler.Handle(Http("POST", "/cart/user-1", """{"productId":"sku-1","quantity":3}"""), Context()));

        Assert.Equal(["1", "3"], seen.Order().ToList()); // both counted; nothing overwrites the whole cart
    }

    [Fact]
    public async Task A_line_that_would_pass_the_cap_is_pinned_at_99()
    {
        GivenProduct("sku-1");
        GivenCart(lines: Line("sku-1", 1));
        var seen = new List<UpdateItemRequest>();
        _ddb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>()).Returns(FailFirst(3, seen)); // increment, add line and create map all fail

        var res = await _handler.Handle(Http("POST", "/cart/user-1", """{"productId":"sku-1","quantity":50}"""), Context());

        Assert.Equal(200, res.StatusCode);
        Assert.Equal(4, seen.Count);
        Assert.Equal("49", seen[0].ExpressionAttributeValues[":room"].N); // 99 - 50
        Assert.Equal("99", seen[3].ExpressionAttributeValues[":max"].N);
        Assert.Equal("attribute_exists(#l.#p)", seen[3].ConditionExpression);
    }

    [Fact]
    public async Task A_cart_that_keeps_changing_under_the_request_gives_a_retryable_409()
    {
        GivenProduct("sku-1");
        _ddb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns<UpdateItemResponse>(_ => throw new ConditionalCheckFailedException("busy"));

        var res = await _handler.Handle(Http("POST", "/cart/user-1", """{"productId":"sku-1","quantity":1}"""), Context());

        Assert.Equal(409, res.StatusCode);
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
    public async Task The_cart_is_the_lines_of_the_shoppers_cart_item()
    {
        GivenCart(7, 150, Line("b", 1), Line("a", 2, "5.50"));

        var res = await _handler.Handle(Http("GET", "/cart/user-1"), Context());

        var cart = Json(res.Body);
        Assert.Equal("150", cart.GetProperty("updated_at").GetString());
        Assert.Equal(["a", "b"], cart.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("productId").GetString()));
        await _ddb.Received().GetItemAsync(Arg.Is<GetItemRequest>(r => r.TableName == "carts" && r.ConsistentRead == true), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_cart_is_an_empty_cart()
    {
        GivenCart();

        var res = await _handler.Handle(Http("GET", "/cart/user-1"), Context());

        Assert.Equal(0, Json(res.Body).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Removing_a_line_is_one_atomic_remove_of_that_map_entry()
    {
        UpdateItemRequest? update = null;
        _ddb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => update = r), Arg.Any<CancellationToken>()).Returns(new UpdateItemResponse());
        GivenCart();

        var res = await _handler.Handle(Http("DELETE", "/cart/user-1/items/sku-1"), Context());

        Assert.Equal(200, res.StatusCode);
        Assert.StartsWith("REMOVE #l.#p", update!.UpdateExpression);
        Assert.Equal("sku-1", update.ExpressionAttributeNames["#p"]);
        Assert.Equal("attribute_exists(#l.#p)", update.ConditionExpression); // removing a missing line creates nothing
        Assert.Equal("user-1", update.Key["user_id"].S);
    }

    [Fact]
    public async Task Removing_a_line_that_is_not_there_is_a_no_op()
    {
        _ddb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns<UpdateItemResponse>(_ => throw new ConditionalCheckFailedException("no such line"));
        GivenCart();

        var res = await _handler.Handle(Http("DELETE", "/cart/user-1/items/sku-1"), Context());

        Assert.Equal(200, res.StatusCode);
    }

    // ---------------- checkout ----------------

    [Fact]
    public async Task Checkout_places_the_order_deletes_the_cart_and_takes_the_stock_in_one_transaction()
    {
        GivenCart(7, 100, Line("a", 2, "10.00"), Line("b", 1, "4.50"));
        TransactWriteItemsRequest? sent = null;
        _ddb.TransactWriteItemsAsync(Arg.Do<TransactWriteItemsRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new TransactWriteItemsResponse());

        var res = await _handler.Handle(Http("POST", "/orders"), Context());

        Assert.Equal(201, res.StatusCode);
        Assert.Equal("24.50", Json(res.Body).GetProperty("total").GetDecimal().ToString("0.00"));
        Assert.Equal(4, sent!.TransactItems.Count); // order + cart + 2 stock updates

        var order = sent.TransactItems[0].Put;
        Assert.Equal("orders", order.TableName);
        Assert.Equal("attribute_not_exists(order_id)", order.ConditionExpression);
        Assert.False(order.Item.ContainsKey("payload")); // no second copy of the order
        Assert.Equal(2, order.Item["items"].L.Count);
        Assert.Equal("PLACED", order.Item["status"].S);
        Assert.Equal("24.50", order.Item["total"].S);

        var cart = sent.TransactItems[1].Delete;
        Assert.Equal("carts", cart.TableName);
        Assert.Equal(["user_id"], cart.Key.Keys);
        Assert.Equal("version = :v", cart.ConditionExpression); // only the cart version we priced
        Assert.Equal("7", cart.ExpressionAttributeValues[":v"].N);

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
        GivenCart(7, 100, Line("a", 2), Line("b", 1));
        _ddb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .Returns<TransactWriteItemsResponse>(_ => throw new TransactionCanceledException("cancelled")
            {
                CancellationReasons =
                [
                    new() { Code = "None" },                    // order
                    new() { Code = "None" },                    // cart
                    new() { Code = "None" },                    // stock a
                    new() { Code = "ConditionalCheckFailed" },  // stock b
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
        GivenCart(7, 100, Line("a", 2));
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
        GivenCart();

        var res = await _handler.Handle(Http("POST", "/orders"), Context());

        Assert.Equal(400, res.StatusCode);
        await _ddb.DidNotReceiveWithAnyArgs().TransactWriteItemsAsync(default!, default);
    }

    [Fact]
    public async Task A_cart_too_large_for_one_transaction_is_refused_before_anything_is_written()
    {
        GivenCart(7, 100, Enumerable.Range(0, ApiHandler.MaxLinesAtCheckout + 1).Select(i => Line("sku-" + i, 1)).ToArray());

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
