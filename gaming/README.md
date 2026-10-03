# Gaming — real-time game backend on AWS

Serverless backend for a large-scale multiplayer game: HTTP API for
matchmaking and leaderboards, a WebSocket API for live in-match events, and a
Kinesis telemetry pipeline landing raw client events into S3.

## Architecture

```
players
   |                             live path                    telemetry path
   |   ┌─────────────────────────────┐                ┌─────────────────────────────┐
   |   │ WebSocket API ($connect,    │                │ Kinesis stream (2 shards)   │
   |   │  $default, $disconnect)     │                │        │                    │
   |   │        │                    │                │ ESM batch read               │
   |   │   SessionHandler Lambda     │                │        │                    │
   |   │   (relay events to match)   │                │   TelemetryConsumer Lambda  │
   |   │        │                    │                │        │                    │
   |   └── DynamoDB: connections GSI │                │   S3 telemetry bucket       │
   |         by match_id             │                └─────────────────────────────┘
   |
HTTP API ($default route) ──► MatchmakingHandler Lambda
        ├── players table (profiles/stats)
        ├── queue table (lobby by game, TTL)
        ├── matches table
        └── leaderboard table (player_id hash, GSI board→score)
```

| Layer | Service | Notes |
| --- | --- | --- |
| API | API Gateway HTTP (v2) | `$default` catch-all → handler dispatches on path |
| Realtime | API Gateway WebSocket (v2) | `$connect`/`$default`/`$disconnect`; `route_selection_expression = "$request.body.action"` |
| Matchmaking | DynamoDB | Lobby partition per game; when the partition holds `teamSize` players the poller creates the match |
| Realtime relay | Lambda + `postToConnection` | Query connections GSI by `match_id`, fan the event out; GRPC-style `GoneException` prunes stale sockets |
| Leaderboard | DynamoDB + GSI | `board→score` GSI; top-N = one descending query, no scans |
| Telemetry | Kinesis → Lambda → S3 | 2-shard stream, batched JSONL objects partitioned by day, ready for Athena |

## Deploy

```powershell
./lambdas/publish.ps1
cd terraform
terraform init
terraform apply     # outputs the HTTP + WebSocket endpoints
```

## Try it

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

Repeat for a second user (`bob`, `$tokenBob`). The player id is the token's
`sub` claim; requests never name the player themselves.

```powershell
$api   = (terraform output -raw api_endpoint)
$ws    = (terraform output -raw ws_endpoint)   # wss://...
$auth  = @{ Authorization = "Bearer $token" }

# register, then join a 2-player lobby (repeat with bob's token)
Invoke-RestMethod -Method Post "$api/players" -Headers $auth -Body '{"displayName":"Alice"}'
Invoke-RestMethod -Method Post "$api/matches/queue" -Headers $auth -Body '{"gameId":"duel","teamSize":2}'

# poll until a match forms
Invoke-RestMethod "$api/matches/queue/duel" -Headers $auth

# record your own score (the path id must be your sub); the leaderboard is public
Invoke-RestMethod -Method Post "$api/leaderboard/<your sub>/score" -Headers $auth -Body '{"score":1200}'
Invoke-RestMethod "$api/leaderboard/top"
```

For the live relay, connect two browsers to `$ws` with
`?token=<ID token>&matchId=<matchId>` (only players in that match are let in)
and send

```json
{ "action": "move", "payload": { "x": 10, "y": 20 } }
```

— the other connection receives `{ "action": "move", "from_player": "<sender sub>", "payload": {...} }`.

Feed the telemetry stream from your game clients:

```powershell
aws kinesis put-record --stream-name "<telemetry_stream>" --partition-key "alice" `
  --data "$(echo '{"event":"round_end","player":"alice","score":1200,"ts":123}' | base64)"
```

## The "large scale" bits

- **Relay fan-out without a broker.** Connection state lives in DynamoDB and
  the WebSocket API does the global fan-out — no Redis or cluster of socket
  servers to run. Eight connections or eight hundred thousand, same p99.
- **TTL lobbies.** A queue slot that never fills expires in 10 minutes; idle
  sockets drop after an hour. Tables stay bounded with zero cleanup code.
- **Telemetry that doesn't spike the DB.** Clients slam Kinesis (2 shards per
  ~1k writes/s, dial it via `shard_count`), the consumer batches JSONL, and the
  data plane (DynamoDB) is never touched by the analytics stream.
- **No cold fix-it ops.** Every Lambda is stateless and ready-runs on managed
  runtimes; "scaling" is throughput ceilings on managed services.

## Grow it further

- Sanity-check matchmaking with actual latency: swap polling for an EventBridge
  "lobby full" notification to clients over the same WebSocket.
- Leaderboard sharding: when one `global` partition exceeds a partition's hot
  key ceiling, shard the board (`board#shard01...`), shard-write, then a
  `MergeLambda` produces the merged top-N.
- Redis (ElastiCache Serverless) is a natural replacement for the relay: ordered
  per-match streams, presence, and pub/sub without the DynamoDB GSI round-trip.
- Telemetry → Amazon Athena + Tableau/QuickSight for cohort analysis; Kinesis
  Data Firehose for object-partitioned delivery. Add a second consumer (email,
  anti-cheat alarms) without touching the writer.

## Security notes

- **Sign-in.** A Cognito user pool issues the tokens. The HTTP API uses a JWT
  authorizer on every route except `GET /leaderboard/top`; the WebSocket
  `$connect` route uses a Lambda REQUEST authorizer (`ConnectAuthorizer`)
  that validates the ID token passed as `?token=`, because browsers cannot set
  headers on a WebSocket handshake. Handlers take the player id from the token.
- **Matchmaking** only counts `WAITING` slots, takes exactly `teamSize`
  players, and claims them in one DynamoDB transaction conditioned on
  `status = WAITING`, so concurrent polls cannot put a player in two matches.
- **Scores are still client-reported.** A player can only post their own
  score, but nothing stops them posting a fake one. A real game computes
  scores on the server (or validates them from telemetry).
