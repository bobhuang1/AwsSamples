# ---------- Live connections: hash connection_id, GSI by room ----------
# TTL drops clients that never sent $disconnect; the handler refreshes it
# on activity.

resource "aws_dynamodb_table" "connections" {
  name         = "${local.unique}-connections"
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "connection_id"

  attribute {
    name = "connection_id"
    type = "S"
  }

  attribute {
    name = "room_id"
    type = "S"
  }

  global_secondary_index {
    name            = "room-index"
    hash_key        = "room_id"
    projection_type = "KEYS_ONLY"
  }

  ttl {
    enabled        = true
    attribute_name = "ttl"
  }
}

# ---------- Message history: hash message_id, GSI (room_id, created_at) ----------
# The GSI is the per-room feed; creation time as range key keeps one Query as
# the read path (descending). Retry/amplification protection is not needed
# because there is exactly one writer per message.

resource "aws_dynamodb_table" "messages" {
  name         = "${local.unique}-messages"
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "message_id"

  attribute {
    name = "message_id"
    type = "S"
  }

  attribute {
    name = "room_id"
    type = "S"
  }

  attribute {
    name = "created_at"
    type = "N"
  }

  global_secondary_index {
    name            = "room-messages-index"
    hash_key        = "room_id"
    range_key       = "created_at"
    projection_type = "ALL"
  }

  ttl {
    enabled        = true
    attribute_name = "ttl"
  }
}