# One zip, three roles, three handlers:
#  - MatchmakingHandler.Handle   HTTP API (queueing, matches, leaderboard)
#  - SessionHandler.Handle       WebSocket API (connect / relay / disconnect)
#  - TelemetryConsumer.Handle    Kinesis stream -> S3 (raw game telemetry)

resource "aws_cloudwatch_log_group" "game" {
  name              = "/aws/lambda/${local.unique}-game"
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
      { Effect = "Allow", Action = "logs:CreateLogStream", Resource = aws_cloudwatch_log_group.game.arn },
      { Effect = "Allow", Action = ["logs:PutLogEvents"], Resource = "${aws_cloudwatch_log_group.game.arn}:*" },
      { Effect = "Allow", Action = ["dynamodb:GetItem", "dynamodb:Query", "dynamodb:PutItem", "dynamodb:UpdateItem", "dynamodb:BatchWriteItem", "dynamodb:DeleteItem"],
      Resource = [aws_dynamodb_table.players.arn, aws_dynamodb_table.queue.arn, aws_dynamodb_table.matches.arn, aws_dynamodb_table.leaderboard.arn] },
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
      { Effect = "Allow", Action = "logs:CreateLogStream", Resource = aws_cloudwatch_log_group.game.arn },
      { Effect = "Allow", Action = ["logs:PutLogEvents"], Resource = "${aws_cloudwatch_log_group.game.arn}:*" },
      { Effect = "Allow", Action = ["dynamodb:GetItem", "dynamodb:Query", "dynamodb:PutItem", "dynamodb:DeleteItem"],
      Resource = [aws_dynamodb_table.connections.arn, aws_dynamodb_table.matches.arn] },
      { Effect = "Allow", Action = "execute-api:ManageConnections", Resource = aws_apigatewayv2_stage.ws.execution_arn },
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
      { Effect = "Allow", Action = "logs:CreateLogStream", Resource = aws_cloudwatch_log_group.game.arn },
      { Effect = "Allow", Action = ["logs:PutLogEvents"], Resource = "${aws_cloudwatch_log_group.game.arn}:*" },
      { Effect = "Allow", Action = ["kinesis:GetRecords", "kinesis:GetShardIterator", "kinesis:DescribeStream", "kinesis:ListStreams", "kinesis:DescribeStreamSummary"],
      Resource = aws_kinesis_stream.telemetry.arn },
      { Effect = "Allow", Action = "s3:PutObject", Resource = "${aws_s3_bucket.telemetry.arn}/*" },
    ]
  })
}

resource "aws_lambda_function" "http" {
  function_name    = "${local.unique}-http"
  role             = aws_iam_role.http.arn
  runtime          = "dotnet8"
  handler          = "GameBackend::GameBackend.MatchmakingHandler::Handle"
  filename         = "${path.module}/dist/GameBackend.zip"
  source_code_hash = filebase64sha256("${path.module}/dist/GameBackend.zip")
  timeout          = 30
  memory_size      = 512

  environment {
    variables = {
      PLAYERS_TABLE     = aws_dynamodb_table.players.name
      QUEUE_TABLE       = aws_dynamodb_table.queue.name
      MATCHES_TABLE     = aws_dynamodb_table.matches.name
      LEADERBOARD_TABLE = aws_dynamodb_table.leaderboard.name
    }
  }
}

resource "aws_lambda_function" "ws" {
  function_name    = "${local.unique}-ws"
  role             = aws_iam_role.ws.arn
  runtime          = "dotnet8"
  handler          = "GameBackend::GameBackend.SessionHandler::Handle"
  filename         = "${path.module}/dist/GameBackend.zip"
  source_code_hash = filebase64sha256("${path.module}/dist/GameBackend.zip")
  timeout          = 30
  memory_size      = 512

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
  runtime          = "dotnet8"
  handler          = "GameBackend::GameBackend.TelemetryConsumer::Handle"
  filename         = "${path.module}/dist/GameBackend.zip"
  source_code_hash = filebase64sha256("${path.module}/dist/GameBackend.zip")
  timeout          = 60
  memory_size      = 512

  environment {
    variables = {
      TELEMETRY_BUCKET = aws_s3_bucket.telemetry.id
    }
  }
}

# Kinesis stream for game telemetry; the Lambda reads it in batches and lands
# the raw JSONL in S3 for later analytics (Athena/QuickSight on top).
resource "aws_kinesis_stream" "telemetry" {
  name             = "${local.unique}-telemetry"
  shard_count      = 2
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
}

resource "aws_s3_bucket" "telemetry" {
  bucket        = "${local.unique}-telemetry"
  force_destroy = true
}

resource "aws_s3_bucket_versioning" "telemetry" {
  bucket = aws_s3_bucket.telemetry.id
  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_public_access_block" "telemetry" {
  bucket                  = aws_s3_bucket.telemetry.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}