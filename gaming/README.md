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

```powershell
$api   = (terraform output -raw api_endpoint)
$ws    = (terraform output -raw ws_endpoint)   # wss://...

# register two players
curl -X POST "$api/players" -d '{"playerId":"alice","displayName":"Alice"}'
curl -X POST "$api/players" -d '{"playerId":"bob","displayName":"Bob"}'

# both join a 2-player lobby
curl -X POST "$api/matches/queue" -d '{"playerId":"alice","gameId":"duel","teamSize":2}'
curl -X POST "$api/matches/queue" -d '{"playerId":"bob","gameId":"duel","teamSize":2}'

# poll until a match forms
curl "$api/matches/queue/duel/alice"
curl "$api/matches/queue/duel/bob"

# record a score, read the leaderboard
curl -X POST "$api/leaderboard/alice/score" -d '{"score":1200}'
curl -X POST "$api/leaderboard/bob/score"   -d '{"score":900}'
curl "$api/leaderboard/top"
```

For the live relay, connect two browsers to `$ws` with
`?playerId=alice&matchId=<matchId>` and send

```json
{ "action": "move", "matchId": "<matchId>", "payload": { "x": 10, "y": 20 } }
```

— the other connection receives `{ "action": "move", "from_player": "alice", "payload": {...} }`.

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