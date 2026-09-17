# ---------- Player profiles ----------

resource "aws_dynamodb_table" "players" {
  name         = "${local.unique}-players"
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "player_id"

  attribute {
    name = "player_id"
    type = "S"
  }
}

# ---------- Matchmaking queue: one partition per game; waiting players have a
# TTL so a never-filled lobby leaks nothing. ----------

resource "aws_dynamodb_table" "queue" {
  name         = "${local.unique}-queue"
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "game_id"
  range_key    = "player_id"

  attribute {
    name = "game_id"
    type = "S"
  }

  attribute {
    name = "player_id"
    type = "S"
  }

  ttl {
    enabled        = true
    attribute_name = "ttl"
  }
}

# ---------- Matches: sessions that the WebSocket layer fans events to ----------

resource "aws_dynamodb_table" "matches" {
  name         = "${local.unique}-matches"
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "match_id"

  attribute {
    name = "match_id"
    type = "S"
  }
}

# ---------- Leaderboard: hash player_id, GSI on (board, score) so the global
# top-N is a single descending Query instead of a table scan. ----------

resource "aws_dynamodb_table" "leaderboard" {
  name         = "${local.unique}-leaderboard"
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "player_id"

  attribute {
    name = "player_id"
    type = "S"
  }

  attribute {
    name = "board"
    type = "S"
  }

  attribute {
    name = "score"
    type = "N"
  }

  global_secondary_index {
    name            = "board-score-index"
    hash_key        = "board"
    range_key       = "score"
    projection_type = "ALL"
  }
}

# ---------- WebSocket connections: keyed by connection, GSI by match ----------

resource "aws_dynamodb_table" "connections" {
  name         = "${local.unique}-connections"
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "connection_id"

  attribute {
    name = "connection_id"
    type = "S"
  }

  attribute {
    name = "match_id"
    type = "S"
  }

  global_secondary_index {
    name            = "match-index"
    hash_key        = "match_id"
    projection_type = "ALL"
  }

  ttl {
    enabled        = true
    attribute_name = "ttl"
  }
}