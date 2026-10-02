using System.Net;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.EventBridge;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.SQS;
using Amazon.SQS.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ShoppingApi;

public sealed class ApiHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private const int MaxQuantityPerLine = 99;

    private static readonly Lazy<AmazonDynamoDBClient> Ddb = new(() => new AmazonDynamoDBClient());
    private static readonly Lazy<AmazonSQSClient> Sqs = new(() => new AmazonSQSClient());
    private static readonly Lazy<AmazonEventBridgeClient> Events = new(() => new AmazonEventBridgeClient());

    private readonly string _productsTable = Env("PRODUCTS_TABLE", "products");
    private readonly string _cartsTable    = Env("CARTS_TABLE", "carts");
    private readonly string _ordersTable   = Env("ORDERS_TABLE", "orders");
    private readonly string _ordersQueue   = Env("ORDERS_QUEUE_URL", "");
    private readonly string _eventBus      = Env("ORDER_EVENT_BUS", "orders");

    public async Task<APIGatewayHttpApiV2ProxyResponse> Handle(APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        try
        {
            var (method, path) = (request.RequestContext.Http.Method, request.RawPath);

            return (method, path) switch
            {
                ("GET", "/products")                            => await ListProducts(request),
                ("GET", _) when path.StartsWith("/products/")    => await GetProduct(path),
                ("GET", _) when path.StartsWith("/cart/")        => await WithOwnCart(request, path, () => GetCart(path)),
                ("POST", _) when path.StartsWith("/cart/")       => await WithOwnCart(request, path, () => AddToCart(path, request)),
                ("DELETE", _) when path.StartsWith("/cart/")     => await WithOwnCart(request, path, () => RemoveFromCart(path)),
                ("POST", "/orders")                              => await CreateOrder(request),
                _                                                => Json(NotFound("No route for " + method + " " + path)),
            };
        }
        catch (Exception ex)
        {
            context.Logger.LogError("Handler failure: {0}", ex);
            return Json(new { error = ex.Message }, HttpStatusCode.InternalServerError);
        }
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
        if (!string.Equals(Segment(path, 2), caller, StringComparison.Ordinal))
            return Json(new { error = "You can only access your own cart." }, HttpStatusCode.Forbidden);
        return await next();
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> ListProducts(APIGatewayHttpApiV2ProxyRequest request)
    {
        var table = Table.LoadTable(Ddb.Value, _productsTable);
        string? category = request.QueryStringParameters != null && request.QueryStringParameters.TryGetValue("category", out var c) ? c : null;

        IEnumerable<Document> docs = string.IsNullOrEmpty(category)
            ? await table.Scan(new ScanOperationConfig { ConsistentRead = false, Limit = 50 }).GetRemainingAsync()
            : await table.Query($"category-index", new QueryFilter("category", QueryOperator.Equal, category)).GetRemainingAsync();

        return Json(Ok(docs.Select(DocToProduct).ToList()));
    }

    private Task<APIGatewayHttpApiV2ProxyResponse> GetProduct(string path)
    {
        var id = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
        return FindProduct(id);
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> FindProduct(string id)
    {
        var table = Table.LoadTable(Ddb.Value, _productsTable);
        var doc = await table.GetItemAsync(id);
        return doc is null ? Json(NotFound("No product " + id)) : Json(Ok(DocToProduct(doc)));
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> GetCart(string path)
    {
        var userId = Segment(path, 2);
        var table = Table.LoadTable(Ddb.Value, _cartsTable);
        var doc = await table.GetItemAsync(userId);
        if (doc is null) return Json(Ok(new Cart(userId, new List<CartItem>(), null)));
        var items = (doc.Contains("items") ? doc["items"] as DynamoDBList : null)?.AsListOfDocument().Select(ItemFromDoc).ToList() ?? new List<CartItem>();
        return Json(Ok(new Cart(userId, items, doc["updated_at"].AsString())));
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> AddToCart(string path, APIGatewayHttpApiV2ProxyRequest request)
    {
        var userId = Segment(path, 2);
        var body = JsonSerializer.Deserialize<AddItemRequest>(request.Body, JsonOpts)
            ?? throw new InvalidOperationException("Body must be { productId, quantity }");
        if (body.Quantity is < 1 or > MaxQuantityPerLine)
            return Json(BadRequest($"quantity must be between 1 and {MaxQuantityPerLine}"));

        // Look up the product so prices are always taken from the catalog.
        var products = Table.LoadTable(Ddb.Value, _productsTable);
        var product = await products.GetItemAsync(body.ProductId);
        if (product is null) return Json(NotFound("No product " + body.ProductId));

        var item = new CartItem(body.ProductId, product["name"].AsString(), Decimal.Parse(product["price"].AsString()), body.Quantity);
        var cart = Table.LoadTable(Ddb.Value, _cartsTable);
        var existing = await cart.GetItemAsync(userId);

        var items = existing is { Count: > 0 } && existing.Contains("items")
            ? (existing["items"] as DynamoDBList)!.AsListOfDocument().ToList()
            : new List<Document>();

        // One line per product: adding the same product again raises its quantity.
        var current = items.FirstOrDefault(d => d["product_id"].AsString() == body.ProductId);
        if (current is not null)
        {
            var merged = Math.Min(MaxQuantityPerLine, current["quantity"].AsInt() + body.Quantity);
            items.Remove(current);
            item = item with { Quantity = merged };
        }
        items.Add(ItemToDoc(item));

        var doc = new Document
        {
            ["user_id"]    = userId,
            ["items"]      = items,
            ["updated_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            ["ttl"]        = (DateTimeOffset.UtcNow.AddDays(7)).ToUnixTimeSeconds(), // DynamoDB TTL only honours a Number attribute
        };
        await cart.PutItemAsync(doc);
        return Json(Ok(new Cart(userId, items.Select(ItemFromDoc).ToList(), doc["updated_at"].AsString())));
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> RemoveFromCart(string path)
    {
        var userId = Segment(path, 2);
        var productId = Uri.UnescapeDataString(Segment(path, 4));

        var cart = Table.LoadTable(Ddb.Value, _cartsTable);
        var existing = await cart.GetItemAsync(userId);
        var items = existing is { Count: > 0 } && existing.Contains("items")
            ? (existing["items"] as DynamoDBList)!.AsListOfDocument().Where(d => d["product_id"].AsString() != productId).ToList()
            : new List<Document>();

        var doc = new Document
        {
            ["user_id"]    = userId,
            ["items"]      = items,
            ["updated_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
        };
        await cart.PutItemAsync(doc);
        return Json(Ok(new Cart(userId, items.Select(ItemFromDoc).ToList(), doc["updated_at"].AsString())));
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> CreateOrder(APIGatewayHttpApiV2ProxyRequest request)
    {
        // The order is always placed for the signed-in caller; a userId in the body is ignored.
        var userId = CallerId(request);
        if (userId is null) return Json(new { error = "Sign in required." }, HttpStatusCode.Unauthorized);

        var cart = Table.LoadTable(Ddb.Value, _cartsTable);
        var existing = await cart.GetItemAsync(userId);
        var items = existing is { Count: > 0 } && existing.Contains("items")
            ? (existing["items"] as DynamoDBList)!.AsListOfDocument().Select(ItemFromDoc).ToList()
            : new List<CartItem>();
        if (items.Count == 0) return Json(BadRequest("Cart is empty"));

        var orderId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var order = new Order(orderId, userId, "PLACED", now, items.Sum(i => i.Price * i.Quantity), items);

        var orders = Table.LoadTable(Ddb.Value, _ordersTable);
        var doc = new Document
        {
            ["order_id"]   = orderId,
            ["user_id"]    = userId,
            ["status"]     = order.status,
            ["created_at"] = now.ToUnixTimeSeconds().ToString(),
            ["total"]      = order.total.ToString("0.00"),
            ["items"]      = items.Select(ItemToDoc).ToList(),
            ["ttl"]        = (now.AddDays(30)).ToUnixTimeSeconds(), // DynamoDB TTL only honours a Number attribute
        };
        await orders.PutItemAsync(doc);

        // Hand the order to the durable checkout pipeline: queue -> worker -> Step Functions.
        await Sqs.Value.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = _ordersQueue,
            MessageBody = JsonSerializer.Serialize(order, JsonOpts),
            MessageGroupId = userId,
        });

        // Announce it on the event bus for downstream consumers (search, BI, email...).
        await Events.Value.PutEventsAsync(new Amazon.EventBridge.Model.PutEventsRequest
        {
            Entries = new List<Amazon.EventBridge.Model.PutEventsRequestEntry>
            {
                new()
                {
                    Source = "com.sample.shop",
                    DetailType = "order.placed",
                    EventBusName = _eventBus,
                    Detail = JsonSerializer.Serialize(order, JsonOpts),
                }
            }
        });

        await cart.DeleteItemAsync(userId);
        return Json(new { orderId, total = order.total, status = order.status }, HttpStatusCode.Created);
    }

    // ---------------- helpers ----------------

    private static string Segment(string path, int index)
        => Uri.UnescapeDataString(path.Split('/', StringSplitOptions.RemoveEmptyEntries)[index]);

    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;

    private static string Str(Document d, string key) => d.Contains(key) ? d[key].AsString() : "";

    private static Product DocToProduct(Document d) => new(
        d["product_id"].AsString(),
        string.IsNullOrEmpty(Str(d, "category")) ? null : Str(d, "category"),
        d["name"].AsString(),
        Decimal.Parse(d["price"].AsString()),
        d.Contains("stock") ? d["stock"].AsInt() : 0);

    private static CartItem ItemFromDoc(Document d) => new(
        d["product_id"].AsString(),
        d["name"].AsString(),
        Decimal.Parse(d["price"].AsString()),
        d["quantity"].AsInt());

    private static Document ItemToDoc(CartItem i) => new()
    {
        ["product_id"] = i.ProductId,
        ["name"] = i.Name,
        ["price"] = i.Price.ToString("0.00"),
        ["quantity"] = i.Quantity,
        ["subtotal"] = (i.Price * i.Quantity).ToString("0.00"),
    };

    private static APIGatewayHttpApiV2ProxyResponse Ok<T>(T value) => Json(value, HttpStatusCode.OK);
    private static APIGatewayHttpApiV2ProxyResponse NotFound(string message) => Json(new { error = message }, HttpStatusCode.NotFound);
    private static APIGatewayHttpApiV2ProxyResponse BadRequest(string message) => Json(new { error = message }, HttpStatusCode.BadRequest);

    private static APIGatewayHttpApiV2ProxyResponse Json<T>(T value, HttpStatusCode code = HttpStatusCode.OK)
        => new()
        {
            StatusCode = (int)code,
            Headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
            Body = JsonSerializer.Serialize(value, JsonOpts),
        };
}

// ---------------- records ----------------

public sealed record Product(string product_id, string? category, string name, decimal price, int stock);
public sealed record Cart(string user_id, IReadOnlyList<CartItem> items, string? updated_at);
public sealed record CartItem(string ProductId, string Name, decimal Price, int Quantity);
public sealed record Order(string order_id, string user_id, string status, DateTimeOffset created_at, decimal total, IReadOnlyList<CartItem> items);
public sealed record AddItemRequest(string ProductId, int Quantity);
public sealed record CreateOrderRequest(string? UserId); // kept for compatibility; ignored