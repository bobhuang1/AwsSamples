# Shopping — large-scale e-commerce on AWS

Serverless e-commerce: browse a catalog, fill a cart, check out, and watch the
order flow through a durable SQS -> Lambda -> Step Functions pipeline. The
whole workload is PAY_PER_REQUEST and stateless; scale is a property of the
managed services, not the code.

## Architecture

```
Static frontend (S3)
   |
   +-- CloudFront  ......... WAF (AWS managed rules + rate limit)
   |
Browser ──► API Gateway HTTP API ──► ShoppingApi Lambda
              |  (GET /products public; everything else Cognito JWT)          online path
              +--► DynamoDB  products | carts (TTL 7d) | orders (TTL 30d)
                                                    |
                                       orders stream ──► OrderWorkflow.PublishOrder (outbox)
                                                    |
              SQS orders queue (+DLQ) ◄─────────────+──► OrderWorkflow.ProcessOrder ──► Step Functions
                                                    |                                   |
                                                    |                      CompleteOrder (marks COMPLETED)
              EventBridge "orders" bus ◄────────────+──► AuditOrder → S3 audit bucket
```

| Layer | Service | Notes |
| --- | --- | --- |
| Edge | CloudFront + AWS WAF | Managed Core rule set + IP rate limit; serves the S3 website |
| Auth | Cognito user pool | JWT authorizer on the API (`$default` route) |
| API | API Gateway HTTP API | Catch-all `$default` + public `GET /products`; CORS wide open for the sample |
| Compute | 2 Lambda functions (`.NET 8`) | `ShoppingApi` (online), `OrderWorkflow` (SQS + SFN + EventBridge handlers, one zip) |
| Data | DynamoDB | `products`, `carts` (TTL), `orders` (TTL + CDC stream + GSI by user) |
| Async | SQS (+DLQ) | Orders queue buffers checkout; partial batch failures via `ReportBatchItemFailures` |
| Ops | Step Functions | Standard state machine: wait, then complete |
| Events | EventBridge | Custom bus `shop-...-orders`; `order.placed/processing/completed` events → audit |
| Archive | S3 | Audit bucket, versioned, public access blocked |

## Deploy

```powershell
# 1. build Lambda zips
./lambdas/publish.ps1

# 2. provision everything
cd terraform
terraform init
terraform apply
```

`terraform outputs` prints the website URL, API endpoint, and Cognito ids.
Upload the frontend, seed the catalog, then try the API:

```powershell
# seed a few products (replace the payload with your own)
aws dynamodb batch-write-item `
  --request-items '{"PRODUCTS_TABLE":[{ "PutRequest": { "Item": {
     "product_id":{"S":"sku-001"},"category":{"S":"books"},"name":{"S":"Scalable Systems"},"price":{"N":"49.99"},"stock":{"N":"100"} }}}]}'
```

```powershell
# API smoke tests (products route is public)
curl "$(terraform output -raw api_endpoint)/products"
```

## The "large scale" bits

- **Catch-all route + path dispatch.** One `$default` route, one integration,
  one deployment. The handler routes on `RawPath`, which keeps the API Gateway
  route table flat — fine up to hundreds of endpoints.
- **Async checkout with an outbox.** `POST /orders` writes the order and
  deletes the cart in one DynamoDB transaction and nothing else. The orders
  table stream feeds `PublishOrder`, which sends the order to SQS (90s
  visibility, 5 max receives, DLQ) and EventBridge, retrying from the stream on
  failure, so a stored order is always processed. Send an `Idempotency-Key`
  header to make checkout retries return the original order. Status changes
  are guarded (`PLACED → PROCESSING → COMPLETED`), so redelivered messages
  can't move an order backwards.
- **TTLs everywhere.** Carts expire in 7 days, orders in 30, so the tables stay
  tiny without any janitor code.
- **Events, not calls.** Checkout emits events on a custom bus; the audit trail,
  email, search, and BI consumers subscribe separately and fail independently.
- **WAF at the edge.** Managed rule set plus an IP rate limit on CloudFront
  covers the static tier; a regional ACL can be joined to the API later via
  `aws_wafv2_web_acl_association`.

## Grow it further

- Multi-region: make `orders` a global table, add regional replicas, one state
  machine per region.
- Auth everywhere: enforce JWT on the public catalog route too, or swap to
  Amazon Verified Permissions for fine-grained ABAC.
- Checkout hardening: more stream consumers (search, email) next to the
  outbox, and `Restricted` `placement` for the stack.
- Cost: under a few dollars/month in dev; scale is almost purely DynamoDB
  RCU/WCU + Lambda invocations.

## Identity

The API Gateway JWT authorizer only proves that *a* shopper is signed in, so the
handler takes the shopper's id from the token's `sub` claim: `/cart/{userId}` answers
403 unless `{userId}` is the caller's own `sub`, and `POST /orders` always checks out
the caller's own cart (a `userId` in the body is ignored). Cart quantities must be
1-99, and adding a product that is already in the cart raises its quantity.
