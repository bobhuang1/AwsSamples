# Chat — large-scale realtime chat on AWS

A WebSocket chat service that separates the *write* path (fast, cheap) from the
*delivery* path (slow, retryable) so a busy room never blocks the writer.

## Architecture

```
browser (client/chat.html)
   │
   │  wss://…/v1  (token,roomId,nickname query params)
   ▼
WebSocket API ($connect / $default / $disconnect)
   │
   ▼ MessageHandler Lambda (write path)
   ├── DynamoDB messages   (message_id, GSI room→created_at = per-room feed)
   ├── DynamoDB connections(refreshed ttl = presence)
   └── SQS fanout queue (+DLQ)
             │
             ▼ FanoutConsumer Lambda (delivery path)
             └── Query room sockets (GSI)  →  API Gateway postToConnection
                     └── GoneException → prune stale socket
```

| Layer | Service | Notes |
| --- | --- | --- |
| Transport | API Gateway WebSocket API | Three routes; the browser client is a plain HTML file |
| Write path | `MessageHandler` Lambda | Put message + enqueue. Returns before delivery is finished |
| Storage | DynamoDB | `messages` with `room-messages-index` feed; `connections` with `room-index` and a 6h/1h TTL |
| Buffer | SQS + DLQ | Fan-out storms backlog in the queue instead of throttling the writer |
| Delivery | `FanoutConsumer` Lambda | Batch read, room sockets via GSI, `postToConnection` per socket, prunes gone sockets, redrives on failure |

## Deploy

```powershell
./lambdas/publish.ps1
cd terraform
terraform init
terraform apply        # prints the wss:// endpoint
```

Both APIs need a Cognito **ID token**. Create a test user and sign in with
the AWS CLI (`terraform output` prints the pool and client ids):

```powershell
$pool   = (terraform output -raw cognito_pool_id)
$client = (terraform output -raw cognito_client_id)

aws cognito-idp admin-create-user --user-pool-id $pool --username alice --temporary-password 'TempPass1!' --message-action SUPPRESS
aws cognito-idp admin-set-user-password --user-pool-id $pool --username alice --password 'YourPass1!' --permanent
$token = (aws cognito-idp initiate-auth --client-id $client --auth-flow alice_PASSWORD_AUTH `
  --auth-parameters aliceNAME=alice,PASSWORD='YourPass1!' --query AuthenticationResult.IdToken --output text)
```

Open `client/chat.html`, paste the endpoint and the ID token, pick a nickname
and room, and connect. Sign in as a second user in another tab and join the
same room — messages relay live between them.

## The "large scale" bits

- **Writer/deliverer split.** `$default` is two DynamoDB writes and one SQS
  send — O(1) regardless of how many people are in the room. Fan-out is the
  consumer's problem, and if it falls behind the queue absorbs the backlog.
- **Feed without a hot row.** The per-room feed is a GSI, not a list item;
  there's no single record that every writer contends on, so room size cannot
  cap throughput.
- **TTL presence.** Sockets that were never `$disconnect`ed age out; the
  handler extends their lease on activity. No janitor process.
- **Dead-lettering.** A message that keeps failing (throttle, bad target) is
  retried five times then lands in the DLQ with full context, not silently
  dropped at the edge.

## Grow it further

- Modes: support `{"action":"history","roomId":...}` to replay `room-messages-index`;
  unread counts as a per-user counter table.
- Delivery semantics: batch fan-out with API Gateway's
  `@connections/{connectionId}` compatibility later via Kinesis if rooms hit a
  million members (Kinesis Data Firehose → per-room streams).
- Presence: today it's "sockets in the room"; move it to Redis (ElastiCache
  Serverless) sorted sets for online-count and typing indicators without
  touching the message table.
- Rooms vs topics: swap the single GSI for an SNS topic per room to get
  pub/sub semantics and automatic fan-out with FIFO guarantees for
  moderation.

## Security notes

The `$connect` route uses a Lambda REQUEST authorizer (`ConnectAuthorizer`)
that validates a Cognito ID token passed as `?token=` (browsers cannot set
headers on a WebSocket handshake). The user id stored for the connection is the
token's `sub`; the nickname is only a display name. Tokens in query strings can
end up in access logs, so keep API Gateway access logging free of query
strings, and keep token lifetimes short.
