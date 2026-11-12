using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ShoppingApi.Tests")]

namespace ShoppingApi;

public sealed class ApiHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private const int MaxQuantityPerLine = 99;

    /// <summary>
    /// Checkout is one DynamoDB transaction: the order, one cart-line delete and one stock
    /// update per product. A transaction holds at most 100 items, so a cart can have at most
    /// (100 - 1) / 2 = 49 different products at checkout.
    /// </summary>
    internal const int MaxLinesAtCheckout = 49;

    private const int ProductPageSize = 50;

    // The catalog is public and read far more often than it changes; stock is live, so keep this short.
    private const int CatalogCacheSeconds = 30;

    private static readonly Lazy<IAmazonDynamoDB> DefaultDdb = new(() => new AmazonDynamoDBClient());

    private readonly IAmazonDynamoDB _ddb;
    private readonly string _productsTable = Env("PRODUCTS_TABLE", "products");
    private readonly string _cartsTable    = Env("CARTS_TABLE", "carts");
    private readonly string _ordersTable   = Env("ORDERS_TABLE", "orders");

    // The Lambda runtime uses this constructor.
    public ApiHandler() : this(DefaultDdb.Value) { }

    public ApiHandler(IAmazonDynamoDB ddb) => _ddb = ddb;

    public async Task<APIGatewayHttpApiV2ProxyResponse> Handle(APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        try
        {
            var (method, path) = (request.RequestContext.Http.Method, RoutePath(request));

            return (method, path) switch
            {
                ("GET", "/products")                            => await ListProducts(request),
                ("GET", _) when path.StartsWith("/products/")    => await GetProduct(path),
                ("GET", _) when path.StartsWith("/cart/")        => await WithOwnCart(request, path, () => GetCart(path)),
                ("POST", _) when path.StartsWith("/cart/")       => await WithOwnCart(request, path, () => AddToCart(path, request)),
                ("DELETE", _) when path.StartsWith("/cart/")     => await WithOwnCart(request, path, () => RemoveFromCart(path)),
                ("POST", "/orders")                              => await CreateOrder(request),
                _                                                => NotFound("No route for " + method + " " + path),
            };
        }
        catch (Exception ex)
        {
            context.Logger.LogError("Handler failure: {0}", ex);
            return Json(new { error = "Internal error" }, HttpStatusCode.InternalServerError);
        }
    }

    /// <summary>
    /// The request path without the API stage. With a named stage (not $default) HTTP APIs
    /// put the stage in front of rawPath (/v1/products); routes are matched without it.
    /// </summary>
    internal static string RoutePath(APIGatewayHttpApiV2ProxyRequest request)
    {
        var path = request.RawPath ?? "/";
        var stage = request.RequestContext?.Stage;
        if (!string.IsNullOrEmpty(stage) && stage != "$default")
        {
            var prefix = "/" + stage;
            if (path == prefix) return "/";
            if (path.StartsWith(prefix + "/", StringComparison.Ordinal)) return path[prefix.Length..];
        }
        return path;
    }

    // ---------------- caller identity ----------------

    /// <summary>
    /// The signed-in shopper, from the Cognito JWT the API Gateway authorizer already
    /// validated. Identity always comes from the token, never from the URL or body: the
    /// authorizer only proves that *someone* is signed in.
    /// </summary>
    private static string? CallerId(APIGatewayHttpApiV2ProxyRequest request) =>
        request.RequestContext?.Authorizer?.Jwt?.Claims is { } claims && claims.TryGetValue("sub", out var sub) && !string.IsNullOrEmpty(sub)
            ? sub
            : null;

    /// <summary>/cart/{userId}/... is only reachable for the caller's own cart.</summary>
    private static async Task<APIGatewayHttpApiV2ProxyResponse> WithOwnCart(
        APIGatewayHttpApiV2ProxyRequest request, string path, Func<Task<APIGatewayHttpApiV2ProxyResponse>> next)
    {
        var caller = CallerId(request);
        if (caller is null) return Json(new { error = "Sign in required." }, HttpStatusCode.Unauthorized);
        if (!string.Equals(Segment(path, 1), caller, StringComparison.Ordinal))
            return Json(new { error = "You can only access your own cart." }, HttpStatusCode.Forbidden);
        return await next();
    }

    // ---------------- catalog ----------------

    private async Task<APIGatewayHttpApiV2ProxyResponse> ListProducts(APIGatewayHttpApiV2ProxyRequest request)
    {
        string? category = request.QueryStringParameters != null && request.QueryStringParameters.TryGetValue("category", out var c) ? c : null;
        var items = new List<Dictionary<string, AttributeValue>>();

        if (string.IsNullOrEmpty(category))
        {
            // One explicit page: the first ProductPageSize products.
            var scan = await _ddb.ScanAsync(new ScanRequest { TableName = _productsTable, Limit = ProductPageSize });
            items.AddRange(scan.Items);
        }
        else
        {
            Dictionary<string, AttributeValue>? last = null;
            do
            {
                var q = await _ddb.QueryAsync(new QueryRequest
                {
                    TableName = _productsTable,
                    IndexName = "category-index",
                    KeyConditionExpression = "category = :c",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":c"] = new() { S = category } },
                    ExclusiveStartKey = last,
                });
                items.AddRange(q.Items);
                last = q.LastEvaluatedKey is { Count: > 0 } ? q.LastEvaluatedKey : null;
            } while (last is not null);
        }

        return Json(items.Select(ProductFromItem).ToList(), HttpStatusCode.OK, CatalogCacheSeconds);
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> GetProduct(string path)
    {
        var id = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
        var res = await _ddb.GetItemAsync(new GetItemRequest { TableName = _productsTable, Key = Key("product_id", id) });
        return res.Item is not { Count: > 0 } item
            ? NotFound("No product " + id)
            : Json(ProductFromItem(item), HttpStatusCode.OK, CatalogCacheSeconds);
    }

    // ---------------- cart ----------------
    //
    // One DynamoDB item per cart line (hash user_id, range product_id). Every cart
    // change is a single atomic request on one line, so two requests at the same time
    // can never overwrite each other's change the way a read-modify-write of the
    // whole cart could.

    private async Task<List<Dictionary<string, AttributeValue>>> CartLines(string userId)
    {
        var lines = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? last = null;
        do
        {
            var q = await _ddb.QueryAsync(new QueryRequest
            {
                TableName = _cartsTable,
                KeyConditionExpression = "user_id = :u",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":u"] = new() { S = userId } },
                ConsistentRead = true,
                ExclusiveStartKey = last,
            });
            lines.AddRange(q.Items);
            last = q.LastEvaluatedKey is { Count: > 0 } ? q.LastEvaluatedKey : null;
        } while (last is not null);
        return lines;
    }

    private static Cart ToCart(string userId, IEnumerable<Dictionary<string, AttributeValue>> lines)
    {
        var list = lines.OrderBy(l => l["product_id"].S, StringComparer.Ordinal).ToList();
        var updated = list.Count == 0 ? null : list.Max(l => Num(l, "updated_at")).ToString(CultureInfo.InvariantCulture);
        return new Cart(userId, list.Select(LineToItem).ToList(), updated);
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> GetCart(string path)
    {
        var userId = Segment(path, 1);
        return Ok(ToCart(userId, await CartLines(userId)));
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> AddToCart(string path, APIGatewayHttpApiV2ProxyRequest request)
    {
        var userId = Segment(path, 1);
        AddItemRequest? body;
        try { body = string.IsNullOrWhiteSpace(request.Body) ? null : JsonSerializer.Deserialize<AddItemRequest>(request.Body, JsonOpts); }
        catch (JsonException) { body = null; }
        if (body is null || string.IsNullOrEmpty(body.ProductId))
            return BadRequest("Body must be { productId, quantity }");
        if (body.Quantity is < 1 or > MaxQuantityPerLine)
            return BadRequest($"quantity must be between 1 and {MaxQuantityPerLine}");

        // Look up the product so prices are always taken from the catalog.
        var res = await _ddb.GetItemAsync(new GetItemRequest { TableName = _productsTable, Key = Key("product_id", body.ProductId) });
        if (res.Item is not { Count: > 0 } product) return NotFound("No product " + body.ProductId);

        var now = DateTimeOffset.UtcNow;
        // One line per product: adding the same product again raises its quantity. ADD makes
        // that increment atomic in DynamoDB, so concurrent adds are both counted. The
        // condition keeps the line within MaxQuantityPerLine.
        Task<UpdateItemResponse> ApplyLineUpdate(string expression, string? condition, Dictionary<string, AttributeValue> extraValues)
        {
            var values = new Dictionary<string, AttributeValue>
            {
                [":n"] = new() { S = Str(product, "name") },
                [":p"] = new() { S = Dec(product, "price").ToString("0.00", CultureInfo.InvariantCulture) },
                [":u"] = new() { N = now.ToUnixTimeSeconds().ToString() },
                [":t"] = new() { N = now.AddDays(7).ToUnixTimeSeconds().ToString() }, // DynamoDB TTL only honours a Number attribute
            };
            foreach (var (k, v) in extraValues) values[k] = v;
            return _ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _cartsTable,
                Key = Key("user_id", userId, "product_id", body.ProductId),
                UpdateExpression = expression,
                ConditionExpression = condition,
                ExpressionAttributeNames = new Dictionary<string, string> { ["#n"] = "name", ["#t"] = "ttl" },
                ExpressionAttributeValues = values,
            });
        }

        try
        {
            await ApplyLineUpdate("SET #n = :n, price = :p, updated_at = :u, #t = :t ADD quantity :q",
                "attribute_not_exists(quantity) OR quantity <= :room",
                new() { [":q"] = new() { N = body.Quantity.ToString() }, [":room"] = new() { N = (MaxQuantityPerLine - body.Quantity).ToString() } });
        }
        catch (ConditionalCheckFailedException)
        {
            // Adding would pass the cap: pin the line at the cap (idempotent, so no race either).
            await ApplyLineUpdate("SET #n = :n, price = :p, updated_at = :u, #t = :t, quantity = :max", null,
                new() { [":max"] = new() { N = MaxQuantityPerLine.ToString() } });
        }

        return Ok(ToCart(userId, await CartLines(userId)));
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> RemoveFromCart(string path)
    {
        var userId = Segment(path, 1);
        var productId = Segment(path, 3);

        await _ddb.DeleteItemAsync(new DeleteItemRequest { TableName = _cartsTable, Key = Key("user_id", userId, "product_id", productId) });
        return Ok(ToCart(userId, await CartLines(userId)));
    }

    // ---------------- checkout ----------------

    private async Task<APIGatewayHttpApiV2ProxyResponse> CreateOrder(APIGatewayHttpApiV2ProxyRequest request)
    {
        // The order is always placed for the signed-in caller; a userId in the body is ignored.
        var userId = CallerId(request);
        if (userId is null) return Json(new { error = "Sign in required." }, HttpStatusCode.Unauthorized);

        // An Idempotency-Key header makes checkout safe to retry: the same key from the same
        // shopper always maps to the same order id, so a retry returns the first order
        // instead of placing a second one.
        string? idempotencyKey = null;
        if (request.Headers?.TryGetValue("idempotency-key", out var rawKey) == true
            && !string.IsNullOrWhiteSpace(rawKey) && rawKey.Length <= 128)
        {
            idempotencyKey = rawKey.Trim();
        }

        var orderId = idempotencyKey is null ? Guid.NewGuid().ToString("N") : OrderIdFor(userId, idempotencyKey);
        // Look the key up before reading the cart: on a retry of a checkout that already
        // succeeded the cart is empty, and the client must get the original order back.
        if (idempotencyKey is not null && await FindOrder(orderId) is { } placed)
            return PlacedOrder(placed);

        var lines = await CartLines(userId);
        if (lines.Count == 0) return BadRequest("Cart is empty");
        if (lines.Count > MaxLinesAtCheckout)
            return BadRequest($"A cart can hold at most {MaxLinesAtCheckout} different products at checkout");

        var items = lines.Select(LineToItem).ToList();
        var total = items.Sum(i => i.Price * i.Quantity);
        var now = DateTimeOffset.UtcNow;

        // The stored order is the only copy of the order: the stream consumer (OrderWorkflow
        // PublishOrder) builds the queue and event message from this item, so an order row
        // can never exist without being processed and nothing is stored twice.
        var order = new Dictionary<string, AttributeValue>
        {
            ["order_id"]   = new() { S = orderId },
            ["user_id"]    = new() { S = userId },
            ["status"]     = new() { S = "PLACED" },
            ["created_at"] = new() { S = now.ToUnixTimeSeconds().ToString() },
            ["total"]      = new() { S = total.ToString("0.00", CultureInfo.InvariantCulture) },
            ["items"]      = new() { L = items.Select(ItemToAttribute).ToList() },
            ["ttl"]        = new() { N = now.AddDays(30).ToUnixTimeSeconds().ToString() }, // DynamoDB TTL only honours a Number attribute
        };

        // One transaction: place the order, empty the cart lines we priced, and take the
        // stock. A line is deleted only if its quantity is still what we priced, and stock
        // is decremented only if enough is left, so a changed cart or an item that sold out
        // meanwhile cancels the whole checkout and nothing is half-done.
        var transact = new List<TransactWriteItem>
        {
            new()
            {
                Put = new Put
                {
                    TableName = _ordersTable,
                    Item = order,
                    ConditionExpression = "attribute_not_exists(order_id)",
                },
            },
        };
        transact.AddRange(items.Select(i => new TransactWriteItem
        {
            Delete = new Delete
            {
                TableName = _cartsTable,
                Key = Key("user_id", userId, "product_id", i.ProductId),
                ConditionExpression = "quantity = :q",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":q"] = new() { N = i.Quantity.ToString() } },
            },
        }));
        transact.AddRange(items.Select(i => new TransactWriteItem
        {
            Update = new Update
            {
                TableName = _productsTable,
                Key = Key("product_id", i.ProductId),
                UpdateExpression = "SET stock = stock - :q",
                ConditionExpression = "stock >= :q",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":q"] = new() { N = i.Quantity.ToString() } },
            },
        }));

        try
        {
            await _ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = transact });
        }
        catch (TransactionCanceledException ex)
        {
            // A concurrent retry with the same key may have won: return its order.
            if (idempotencyKey is not null && await FindOrder(orderId) is { } winner)
                return PlacedOrder(winner);

            // Reasons line up with the transaction items: [0] order, [1..n] cart lines, [n+1..2n] stock.
            var reasons = ex.CancellationReasons ?? [];
            bool Failed(int index) => index < reasons.Count && reasons[index].Code == "ConditionalCheckFailed";
            var cartChanged = Enumerable.Range(1, items.Count).Any(Failed);
            var soldOut = items.Where((_, i) => Failed(1 + items.Count + i)).ToList();

            if (!cartChanged && soldOut.Count > 0)
            {
                return Json(new
                {
                    error = "Some items are out of stock. Nothing was ordered.",
                    outOfStock = soldOut.Select(i => new { productId = i.ProductId, name = i.Name }).ToList(),
                }, HttpStatusCode.Conflict);
            }
            return Json(new { error = "Your cart changed while checking out. Please try again." }, HttpStatusCode.Conflict);
        }

        return Json(new { orderId, total, status = "PLACED" }, HttpStatusCode.Created);
    }

    private async Task<Dictionary<string, AttributeValue>?> FindOrder(string orderId)
    {
        var res = await _ddb.GetItemAsync(new GetItemRequest { TableName = _ordersTable, Key = Key("order_id", orderId), ConsistentRead = true });
        return res.Item is { Count: > 0 } item ? item : null;
    }

    private static string OrderIdFor(string userId, string idempotencyKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{userId}\n{idempotencyKey}")))[..32].ToLowerInvariant();

    private static APIGatewayHttpApiV2ProxyResponse PlacedOrder(Dictionary<string, AttributeValue> placed)
        => Json(new { orderId = Str(placed, "order_id"), total = Str(placed, "total"), status = Str(placed, "status") }, HttpStatusCode.OK);

    // ---------------- helpers ----------------

    private static string Segment(string path, int index)
        => Uri.UnescapeDataString(path.Split('/', StringSplitOptions.RemoveEmptyEntries)[index]);

    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;

    private static Dictionary<string, AttributeValue> Key(string name, string value) => new() { [name] = new() { S = value } };

    private static Dictionary<string, AttributeValue> Key(string n1, string v1, string n2, string v2)
        => new() { [n1] = new() { S = v1 }, [n2] = new() { S = v2 } };

    private static string Str(Dictionary<string, AttributeValue> d, string key) => d.TryGetValue(key, out var v) ? v.S ?? "" : "";

    private static long Num(Dictionary<string, AttributeValue> d, string key)
        => d.TryGetValue(key, out var v) && long.TryParse(v.N, CultureInfo.InvariantCulture, out var n) ? n : 0;

    // A price may be stored as a string ("49.99") or as a Number.
    private static decimal Dec(Dictionary<string, AttributeValue> d, string key)
        => d.TryGetValue(key, out var v) && decimal.TryParse(v.S ?? v.N, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : 0m;

    private static Product ProductFromItem(Dictionary<string, AttributeValue> d) => new(
        Str(d, "product_id"),
        string.IsNullOrEmpty(Str(d, "category")) ? null : Str(d, "category"),
        Str(d, "name"),
        Dec(d, "price"),
        (int)Num(d, "stock"));

    private static CartItem LineToItem(Dictionary<string, AttributeValue> d) => new(
        Str(d, "product_id"), Str(d, "name"), Dec(d, "price"), (int)Num(d, "quantity"));

    private static AttributeValue ItemToAttribute(CartItem i) => new()
    {
        M = new Dictionary<string, AttributeValue>
        {
            ["product_id"] = new() { S = i.ProductId },
            ["name"]       = new() { S = i.Name },
            ["price"]      = new() { S = i.Price.ToString("0.00", CultureInfo.InvariantCulture) },
            ["quantity"]   = new() { N = i.Quantity.ToString() },
            ["subtotal"]   = new() { S = (i.Price * i.Quantity).ToString("0.00", CultureInfo.InvariantCulture) },
        },
    };

    private static APIGatewayHttpApiV2ProxyResponse Ok<T>(T value) => Json(value, HttpStatusCode.OK);
    private static APIGatewayHttpApiV2ProxyResponse NotFound(string message) => Json(new { error = message }, HttpStatusCode.NotFound);
    private static APIGatewayHttpApiV2ProxyResponse BadRequest(string message) => Json(new { error = message }, HttpStatusCode.BadRequest);

    private static APIGatewayHttpApiV2ProxyResponse Json<T>(T value, HttpStatusCode code = HttpStatusCode.OK, int cacheSeconds = 0)
    {
        var headers = new Dictionary<string, string> { ["content-type"] = "application/json" };
        if (cacheSeconds > 0) headers["cache-control"] = $"public, max-age={cacheSeconds}";
        return new()
        {
            StatusCode = (int)code,
            Headers = headers,
            Body = JsonSerializer.Serialize(value, JsonOpts),
        };
    }
}

// ---------------- records ----------------

public sealed record Product(string product_id, string? category, string name, decimal price, int stock);
public sealed record Cart(string user_id, IReadOnlyList<CartItem> items, string? updated_at);
public sealed record CartItem(string ProductId, string Name, decimal Price, int Quantity);
public sealed record AddItemRequest(string ProductId, int Quantity);
