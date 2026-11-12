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
    /// Checkout is one DynamoDB transaction: the order, the cart delete and one stock
    /// update per product. A transaction holds at most 100 items, so a cart can have at most
    /// 100 - 2 = 98 different products at checkout.
    /// </summary>
    internal const int MaxLinesAtCheckout = 98;

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
    // One DynamoDB item per shopper (hash user_id). Its "lines" attribute is a map keyed by
    // product id, and every cart change is a single atomic UpdateItem on one entry of that
    // map (plus a version counter), so two requests at the same time can never overwrite
    // each other's change the way a read-modify-write of the whole cart could.

    private const int AddAttempts = 3;

    private async Task<Dictionary<string, AttributeValue>?> LoadCart(string userId)
    {
        var res = await _ddb.GetItemAsync(new GetItemRequest { TableName = _cartsTable, Key = Key("user_id", userId), ConsistentRead = true });
        return res.Item is { Count: > 0 } item ? item : null;
    }

    private static List<CartItem> CartLines(Dictionary<string, AttributeValue>? cart)
        => cart is not null && cart.TryGetValue("lines", out var lines) && lines.M is { } map
            ? map.Values.Select(v => LineToItem(v.M)).OrderBy(i => i.ProductId, StringComparer.Ordinal).ToList()
            : [];

    private static Cart ToCart(string userId, Dictionary<string, AttributeValue>? cart)
        => new(userId, CartLines(cart), cart is not null && cart.ContainsKey("updated_at") ? Num(cart, "updated_at").ToString(CultureInfo.InvariantCulture) : null);

    private async Task<APIGatewayHttpApiV2ProxyResponse> GetCart(string path)
    {
        var userId = Segment(path, 1);
        return Ok(ToCart(userId, await LoadCart(userId)));
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
        var name = Str(product, "name");
        var price = Dec(product, "price").ToString("0.00", CultureInfo.InvariantCulture);
        AttributeValue Line(int quantity) => new()
        {
            M = new Dictionary<string, AttributeValue>
            {
                ["product_id"] = new() { S = body.ProductId },
                ["name"]       = new() { S = name },
                ["price"]      = new() { S = price },
                ["quantity"]   = new() { N = quantity.ToString() },
            },
        };

        // Each attempt is one conditional UpdateItem, so whichever state the cart is in only
        // the matching one applies, and no attempt can overwrite a concurrent change:
        //  1. the line exists: add to its quantity (ADD-style increment, kept within the cap)
        //  2. the cart has lines but not this one: add the line
        //  3. the cart has no lines map yet: create it (and the item)
        //  4. the line exists and the increment would pass the cap: pin it at the cap
        // A concurrent change between attempts just sends the loop around again.
        Task<UpdateItemResponse> Apply(string set, string condition, Dictionary<string, AttributeValue> values)
        {
            values[":u"] = new() { N = now.ToUnixTimeSeconds().ToString() };
            values[":t"] = new() { N = now.AddDays(7).ToUnixTimeSeconds().ToString() }; // DynamoDB TTL only honours a Number attribute
            values[":one"] = new() { N = "1" };
            return _ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _cartsTable,
                Key = Key("user_id", userId),
                UpdateExpression = $"SET {set}, updated_at = :u, #t = :t ADD version :one",
                ConditionExpression = condition,
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#l"] = "lines", ["#p"] = body.ProductId, ["#q"] = "quantity", ["#nm"] = "name", ["#t"] = "ttl",
                },
                ExpressionAttributeValues = values,
            });
        }

        async Task<bool> TryApply(string set, string condition, Dictionary<string, AttributeValue> values)
        {
            try { await Apply(set, condition, values); return true; }
            catch (ConditionalCheckFailedException) { return false; }
        }

        var applied = false;
        for (var attempt = 0; attempt < AddAttempts && !applied; attempt++)
        {
            applied =
                await TryApply("#l.#p.#q = #l.#p.#q + :q, #l.#p.#nm = :n, #l.#p.price = :pr",
                    "attribute_exists(#l.#p) AND #l.#p.#q <= :room",
                    new() { [":q"] = new() { N = body.Quantity.ToString() }, [":room"] = new() { N = (MaxQuantityPerLine - body.Quantity).ToString() },
                            [":n"] = new() { S = name }, [":pr"] = new() { S = price } })
                || await TryApply("#l.#p = :line", "attribute_exists(#l) AND attribute_not_exists(#l.#p)",
                    new() { [":line"] = Line(body.Quantity) })
                || await TryApply("#l = :lines", "attribute_not_exists(#l)",
                    new() { [":lines"] = new() { M = new Dictionary<string, AttributeValue> { [body.ProductId] = Line(body.Quantity) } } })
                || await TryApply("#l.#p.#q = :max, #l.#p.#nm = :n, #l.#p.price = :pr", "attribute_exists(#l.#p)",
                    new() { [":max"] = new() { N = MaxQuantityPerLine.ToString() }, [":n"] = new() { S = name }, [":pr"] = new() { S = price } });
        }
        if (!applied)
            return Json(new { error = "The cart is changing too fast. Please try again." }, HttpStatusCode.Conflict);

        return Ok(ToCart(userId, await LoadCart(userId)));
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> RemoveFromCart(string path)
    {
        var userId = Segment(path, 1);
        var productId = Segment(path, 3);

        var now = DateTimeOffset.UtcNow;
        try
        {
            // One atomic REMOVE of this line. The condition makes removing a line that is not
            // there a no-op instead of creating an empty cart item.
            await _ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _cartsTable,
                Key = Key("user_id", userId),
                UpdateExpression = "REMOVE #l.#p SET updated_at = :u, #t = :t ADD version :one",
                ConditionExpression = "attribute_exists(#l.#p)",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#l"] = "lines", ["#p"] = productId, ["#t"] = "ttl" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":u"] = new() { N = now.ToUnixTimeSeconds().ToString() },
                    [":t"] = new() { N = now.AddDays(7).ToUnixTimeSeconds().ToString() },
                    [":one"] = new() { N = "1" },
                },
            });
        }
        catch (ConditionalCheckFailedException)
        {
            // The line was not in the cart: nothing to remove.
        }
        return Ok(ToCart(userId, await LoadCart(userId)));
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

        var cartItem = await LoadCart(userId);
        var items = CartLines(cartItem);
        if (items.Count == 0) return BadRequest("Cart is empty");
        if (items.Count > MaxLinesAtCheckout)
            return BadRequest($"A cart can hold at most {MaxLinesAtCheckout} different products at checkout");
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

        // One transaction: place the order, delete the cart we priced, and take the stock.
        // The cart is deleted only if its version is still the one we read (every add and
        // remove bumps it), and stock is decremented only if enough is left, so a changed
        // cart or an item that sold out meanwhile cancels the whole checkout and nothing is
        // half-done.
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
        transact.Add(new TransactWriteItem
        {
            Delete = new Delete
            {
                TableName = _cartsTable,
                Key = Key("user_id", userId),
                ConditionExpression = "version = :v",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":v"] = new() { N = Num(cartItem!, "version").ToString(CultureInfo.InvariantCulture) } },
            },
        });
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

            // Reasons line up with the transaction items: [0] order, [1] cart, [2..] stock.
            var reasons = ex.CancellationReasons ?? [];
            bool Failed(int index) => index < reasons.Count && reasons[index].Code == "ConditionalCheckFailed";
            var cartChanged = Failed(1);
            var soldOut = items.Where((_, i) => Failed(2 + i)).ToList();

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
