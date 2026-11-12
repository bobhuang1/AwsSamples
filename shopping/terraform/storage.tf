# Product catalog
resource "aws_dynamodb_table" "products" {
  name         = "${local.unique}-products"
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "product_id"

  attribute {
    name = "product_id"
    type = "S"
  }

  attribute {
    name = "category"
    type = "S"
  }

  global_secondary_index {
    name            = "category-index"
    hash_key        = "category"
    projection_type = "ALL"
  }

  point_in_time_recovery {
    enabled = true
  }
}

# Shopping carts: hash-keyed by user, items stored as a JSON list, auto-expire
# after a week of inactivity so abandoned carts collect no stale data.
resource "aws_dynamodb_table" "carts" {
  name         = "${local.unique}-carts"
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "user_id"

  attribute {
    name = "user_id"
    type = "S"
  }

  ttl {
    enabled        = true
    attribute_name = "ttl"
  }
}

# Orders: the write-side record of every purchase. Stream is on so downstream
# systems (search, BI, email) can follow the CDC feed for analytics at scale.
resource "aws_dynamodb_table" "orders" {
  name         = "${local.unique}-orders"
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "order_id"

  attribute {
    name = "order_id"
    type = "S"
  }

  # No user-orders-index yet: nothing reads orders by user, and an ALL-projection
  # index doubles the write cost of every order and status change. Add it
  # (user_id hash, created_at range) together with a "list my orders" route.

  stream_enabled   = true
  stream_view_type = "NEW_IMAGE"

  ttl {
    enabled        = true
    attribute_name = "ttl"
  }

  point_in_time_recovery {
    enabled = true
  }
}