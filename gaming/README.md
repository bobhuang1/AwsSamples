# Gaming — real-time game backend on AWS

Serverless backend for a large-scale multiplayer game: HTTP API for
matchmaking and leaderboards, a WebSocket API for live in-match events, and a
Kinesis telemetry pipeline landing raw client events into S3.

## Architecture

```
players
   |                             live path                    telemetry path
   |   ┌─────────────────────────────┐                ┌─────────────────────────────┐
   |   │ WebSocket API ($connect,    │                │ Kinesis stream (1 shard)   │
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
        ├── queue table (lobby by game, TTL, sparse lobby-index for WAITING slots)
        ├── matches table
        └── leaderboard table (player_id hash, GSI sharded board→score)

game server ──(SigV4)──► POST /matches/{id}/result ──► points computed server-side
```

| Layer | Service | Notes |
| --- | --- | --- |
| API | API Gateway HTTP (v2) | `$default` catch-all → handler dispatches on path |
| Realtime | API Gateway WebSocket (v2) | `$connect`/`$default`/`$disconnect`; `route_selection_expression = "$request.body.action"` |
| Matchmaking | DynamoDB | Waiting slots carry `lobby`/`lobby_ts` and sit in a sparse GSI (`lobby-index`); a poll reads only the `teamSize` longest-waiting players from it, and the poller that sees a full lobby creates the match |
| Realtime relay | Lambda + `postToConnection` | Query connections GSI by `match_id`, fan the event out; GRPC-style `GoneException` prunes stale sockets |
| Leaderboard | DynamoDB + GSI | `board→score` GSI with a sharded key (`global#0` .. `global#N-1`, `leaderboard_shards`, default 8); top-N = one descending query per shard, merged by the Lambda |
| Scores | HTTP API, IAM-signed route | `POST /matches/{matchId}/result` is called by the game server, not by players; the backend turns the reported stats into points |
| Telemetry | Kinesis → Lambda → S3 | 1-shard stream by default (`telemetry_shard_count`); failing batches are retried, split, then parked in an SQS queue;, batched JSONL objects partitioned by day, ready for Athena |

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
$token = (aws cognito-idp initiate-auth --client-id $client --auth-flow USER_PASSWORD_AUTH `
  --auth-parameters USERNAME=alice,PASSWORD='YourPass1!' --query AuthenticationResult.IdToken --output text)
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

# poll until a match forms; wait Retry-After seconds (next_poll_ms) between polls
Invoke-RestMethod "$api/matches/queue/duel" -Headers $auth

# the leaderboard is public (cached for 10 s)
Invoke-RestMethod "$api/leaderboard/top"
```

Players cannot post scores. When the match ends, the **game server** reports
what happened and the backend computes the points. That route needs AWS
credentials (SigV4), so attach the policy in the `report_results_policy_arn`
output to the game server's role, then for a quick test use any SigV4 client,
for example [awscurl](https://github.com/okigan/awscurl):

```bash
awscurl --service execute-api --region <region> -X POST "<api_endpoint>/matches/<matchId>/result"   -d '{"results":[{"playerId":"<alice sub>","kills":3,"deaths":1,"objectives":0,"won":true},
                  {"playerId":"<bob sub>","kills":1,"deaths":3,"objectives":0,"won":false}]}'
```

Points per player: `kills*100 + objectives*250 + 500 if won - deaths*25`, never below 0 and
never above 10,000 per match (kills and deaths 0-50, objectives 0-20, else 400). Only players
of the match are accepted, one entry per player, and a match can be scored once (a second
report gets 409), so a player has at most one score per match. The leaderboard score is the
running total of those match points.

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
- **Polls do not read the partition.** Only WAITING slots have the `lobby` and
  `lobby_ts` keys, so `lobby-index` holds exactly the waiting players. A poll
  queries it for the `teamSize` oldest, so its cost depends on the team size,
  not on how many players are queued or how many finished slots are waiting
  for their TTL.
- **Poll back-off.** Waiting responses carry a `Retry-After` header (seconds)
  and a `next_poll_ms` field: 2 s at first, growing by 1 s per 10 s waited up
  to 10 s. Clients should wait that long (plus a little random jitter) between
  polls instead of using a fixed fast interval; or replace polling with a push
  over the WebSocket.
- **Sharded leaderboard.** Every score write goes to one of `leaderboard_shards`
  index keys chosen from a hash of the player id, so a viral game does not
  throttle on a single hot index partition. `GET /leaderboard/top` queries each
  shard for its top N and merges (the top N of the union is inside the union of
  the per-shard top N), and the response is cacheable for 10 s. Raising
  `leaderboard_shards` later moves a player to their new shard on their next
  scored match; until then they are not on the board you read, so backfill the
  `board` attribute of existing rows when you change it.
- **Telemetry that doesn't spike the DB.** Clients slam Kinesis (2 shards per
  ~1k writes/s, dial it via `shard_count`), the consumer batches JSONL, and the
  data plane (DynamoDB) is never touched by the analytics stream.
- **No cold fix-it ops.** Every Lambda is stateless and ready-runs on managed
  runtimes; "scaling" is throughput ceilings on managed services.

## Grow it further

- Sanity-check matchmaking with actual latency: swap polling for an EventBridge
  "lobby full" notification to clients over the same WebSocket.
- Put a CDN or API cache in front of `GET /leaderboard/top` for very large
  audiences, or keep the merged top-N in ElastiCache and refresh it on a schedule.
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
- **Scores are server-derived.** There is no player route that takes a score.
  The game server (IAM-signed route) reports match stats; the backend bounds
  them, checks that every reported player belongs to the match, computes the
  points, and closes the match (`ACTIVE` to `COMPLETED`) in the same
  transaction that adds them, so a match is scored once. The game server is
  trusted: protect its role, and have it derive stats from its own simulation,
  not from client messages.
