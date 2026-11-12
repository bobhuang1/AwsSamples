# One zip, three roles, three handlers:
#  - MatchmakingHandler.Handle   HTTP API (queueing, matches, leaderboard)
#  - SessionHandler.Handle       WebSocket API (connect / relay / disconnect)
#  - TelemetryConsumer.Handle    Kinesis stream -> S3 (raw game telemetry)

# One log group per function, named exactly /aws/lambda/<function name>:
# that is the only group Lambda writes to.
resource "aws_cloudwatch_log_group" "lambda" {
  for_each          = toset(["http", "ws", "telemetry", "authorizer"])
  name              = "/aws/lambda/${local.unique}-${each.key}"
  retention_in_days = 14
}

data "aws_iam_policy_document" "lambda_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["lambda.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "http" {
  name               = "${local.unique}-http-role"
  assume_role_policy = data.aws_iam_policy_document.lambda_assume.json
}

resource "aws_iam_role_policy" "http" {
  name = "http-policy"
  role = aws_iam_role.http.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      { Effect = "Allow", Action = "logs:CreateLogStream", Resource = aws_cloudwatch_log_group.lambda["http"].arn },
      { Effect = "Allow", Action = ["logs:PutLogEvents"], Resource = "${aws_cloudwatch_log_group.lambda["http"].arn}:*" },
      { Effect = "Allow", Action = ["dynamodb:GetItem", "dynamodb:UpdateItem"], Resource = aws_dynamodb_table.players.arn },
      { Effect = "Allow", Action = ["dynamodb:GetItem", "dynamodb:PutItem", "dynamodb:UpdateItem"], Resource = aws_dynamodb_table.queue.arn },
      { Effect = "Allow", Action = "dynamodb:Query", Resource = "${aws_dynamodb_table.queue.arn}/index/lobby-index" },
      # Matches: created by the matchmaker (Put), read, and closed when the game server reports the result (Update).
      { Effect = "Allow", Action = ["dynamodb:GetItem", "dynamodb:PutItem", "dynamodb:UpdateItem"], Resource = aws_dynamodb_table.matches.arn },
      # Scores are only ever added by the match-result route (UpdateItem inside a transaction).
      { Effect = "Allow", Action = "dynamodb:UpdateItem", Resource = aws_dynamodb_table.leaderboard.arn },
      { Effect = "Allow", Action = "dynamodb:Query", Resource = "${aws_dynamodb_table.leaderboard.arn}/index/board-score-index" },
    ]
  })
}

resource "aws_iam_role" "ws" {
  name               = "${local.unique}-ws-role"
  assume_role_policy = data.aws_iam_policy_document.lambda_assume.json
}

resource "aws_iam_role_policy" "ws" {
  name = "ws-policy"
  role = aws_iam_role.ws.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      { Effect = "Allow", Action = "logs:CreateLogStream", Resource = aws_cloudwatch_log_group.lambda["ws"].arn },
      { Effect = "Allow", Action = ["logs:PutLogEvents"], Resource = "${aws_cloudwatch_log_group.lambda["ws"].arn}:*" },
      { Effect = "Allow", Action = ["dynamodb:GetItem", "dynamodb:PutItem", "dynamodb:DeleteItem"], Resource = aws_dynamodb_table.connections.arn },
      { Effect = "Allow", Action = "dynamodb:Query", Resource = [
        "${aws_dynamodb_table.connections.arn}/index/match-index",
        "${aws_dynamodb_table.connections.arn}/index/player-index",
      ] },
      { Effect = "Allow", Action = "dynamodb:GetItem", Resource = aws_dynamodb_table.matches.arn },
      # PostToConnection is authorized against <api>/<stage>/POST/@connections/<id>,
      # not against the bare stage ARN.
      { Effect = "Allow", Action = "execute-api:ManageConnections", Resource = "${aws_apigatewayv2_stage.ws.execution_arn}/POST/@connections/*" },
    ]
  })
}

resource "aws_iam_role" "telemetry" {
  name               = "${local.unique}-telemetry-role"
  assume_role_policy = data.aws_iam_policy_document.lambda_assume.json
}

resource "aws_iam_role_policy" "telemetry" {
  name = "telemetry-policy"
  role = aws_iam_role.telemetry.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      { Effect = "Allow", Action = "logs:CreateLogStream", Resource = aws_cloudwatch_log_group.lambda["telemetry"].arn },
      { Effect = "Allow", Action = ["logs:PutLogEvents"], Resource = "${aws_cloudwatch_log_group.lambda["telemetry"].arn}:*" },
      { Effect = "Allow", Action = ["kinesis:GetRecords", "kinesis:GetShardIterator", "kinesis:DescribeStream", "kinesis:DescribeStreamSummary", "kinesis:ListShards"],
      Resource = aws_kinesis_stream.telemetry.arn },
      { Effect = "Allow", Action = "kinesis:ListStreams", Resource = "*" },
      { Effect = "Allow", Action = "s3:PutObject", Resource = "${aws_s3_bucket.telemetry.arn}/*" },
      { Effect = "Allow", Action = "sqs:SendMessage", Resource = aws_sqs_queue.telemetry_failures.arn },
    ]
  })
}

resource "aws_lambda_function" "http" {
  function_name    = "${local.unique}-http"
  role             = aws_iam_role.http.arn
  runtime          = "dotnet10"
  handler          = "GameBackend::GameBackend.MatchmakingHandler::Handle"
  filename         = "${path.module}/dist/GameBackend.zip"
  source_code_hash = filebase64sha256("${path.module}/dist/GameBackend.zip")
  timeout          = 30
  memory_size      = 512
  depends_on       = [aws_cloudwatch_log_group.lambda["http"]]

  environment {
    variables = {
      PLAYERS_TABLE      = aws_dynamodb_table.players.name
      QUEUE_TABLE        = aws_dynamodb_table.queue.name
      MATCHES_TABLE      = aws_dynamodb_table.matches.name
      LEADERBOARD_TABLE  = aws_dynamodb_table.leaderboard.name
      LEADERBOARD_SHARDS = tostring(var.leaderboard_shards)
    }
  }
}

resource "aws_lambda_function" "ws" {
  function_name    = "${local.unique}-ws"
  role             = aws_iam_role.ws.arn
  runtime          = "dotnet10"
  handler          = "GameBackend::GameBackend.SessionHandler::Handle"
  filename         = "${path.module}/dist/GameBackend.zip"
  source_code_hash = filebase64sha256("${path.module}/dist/GameBackend.zip")
  timeout          = 30
  memory_size      = 512
  depends_on       = [aws_cloudwatch_log_group.lambda["ws"]]

  environment {
    variables = {
      CONNECTIONS_TABLE = aws_dynamodb_table.connections.name
      MATCHES_TABLE     = aws_dynamodb_table.matches.name
      WS_ENDPOINT       = "https://${aws_apigatewayv2_api.ws.api_endpoint}/${local.api_stage}"
    }
  }
}

resource "aws_lambda_function" "telemetry" {
  function_name    = "${local.unique}-telemetry"
  role             = aws_iam_role.telemetry.arn
  runtime          = "dotnet10"
  handler          = "GameBackend::GameBackend.TelemetryConsumer::Handle"
  filename         = "${path.module}/dist/GameBackend.zip"
  source_code_hash = filebase64sha256("${path.module}/dist/GameBackend.zip")
  timeout          = 60
  memory_size      = 512
  depends_on       = [aws_cloudwatch_log_group.lambda["telemetry"]]

  environment {
    variables = {
      TELEMETRY_BUCKET = aws_s3_bucket.telemetry.id
    }
  }
}

# Kinesis stream for game telemetry; the Lambda reads it in batches and lands
# the raw JSONL in S3 for later analytics (Athena/QuickSight on top).
resource "aws_kinesis_stream" "telemetry" {
  # checkov:skip=CKV_AWS_185: Encrypted with the AWS managed key (aws/kinesis); a customer managed key adds a monthly fee and key policy for a demo stream
  # AWS managed key: producers and consumers need no extra key permissions.
  encryption_type  = "KMS"
  kms_key_id       = "alias/aws/kinesis"
  name             = "${local.unique}-telemetry"
  shard_count      = var.telemetry_shard_count
  retention_period = 24
  stream_mode_details {
    stream_mode = "PROVISIONED"
  }
}

resource "aws_lambda_event_source_mapping" "telemetry" {
  event_source_arn       = aws_kinesis_stream.telemetry.arn
  function_name          = aws_lambda_function.telemetry.arn
  starting_position      = "LATEST"
  batch_size             = 100
  parallelization_factor = 2

  # Wait up to a minute to fill a batch: at low volume this means one invocation (and one
  # S3 object) per minute instead of one per handful of records.
  maximum_batching_window_in_seconds = 60

  # A bad record must not block the shard until the 24 h retention drops it:
  # retry a few times, split the batch to isolate the record, then park it.
  maximum_retry_attempts         = 3
  maximum_record_age_in_seconds  = 3600
  bisect_batch_on_function_error = true

  destination_config {
    on_failure {
      destination_arn = aws_sqs_queue.telemetry_failures.arn
    }
  }
}

# Metadata of batches that kept failing (not the records themselves).
resource "aws_sqs_queue" "telemetry_failures" {
  sqs_managed_sse_enabled   = true
  name                      = "${local.unique}-telemetry-failures"
  message_retention_seconds = 1209600
}

resource "aws_s3_bucket" "telemetry" {
  # checkov:skip=CKV_AWS_21: No versioning on purpose: objects are written once under random keys and expire by lifecycle rule, so old versions would only add cost
  bucket        = "${local.unique}-telemetry"
  force_destroy = true
}

# Telemetry objects are written once under random keys, so versioning would only
# multiply storage. Expire them instead of letting the bucket grow forever.
resource "aws_s3_bucket_lifecycle_configuration" "telemetry" {
  bucket = aws_s3_bucket.telemetry.id

  rule {
    id     = "expire-telemetry"
    status = "Enabled"
    filter {}

    expiration {
      days = var.telemetry_retention_days
    }

    abort_incomplete_multipart_upload {
      days_after_initiation = 7
    }
  }
}

resource "aws_s3_bucket_public_access_block" "telemetry" {
  bucket                  = aws_s3_bucket.telemetry.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}