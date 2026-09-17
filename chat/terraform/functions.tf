# Fan-out: buffered via SQS so delivery backlogs never slow the writer.
resource "aws_sqs_queue" "fanout" {
  name                       = "${local.unique}-fanout"
  visibility_timeout_seconds = 30
  message_retention_seconds  = 86400
  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.fanout_dlq.arn
    maxReceiveCount     = 5
  })
}

resource "aws_sqs_queue" "fanout_dlq" {
  name                      = "${local.unique}-fanout-dlq"
  message_retention_seconds = 1209600
}

resource "aws_lambda_event_source_mapping" "fanout" {
  event_source_arn                   = aws_sqs_queue.fanout.arn
  function_name                      = aws_lambda_function.fanout.arn
  enabled                            = true
  batch_size                         = 10
  maximum_batching_window_in_seconds = 5
  function_response_types            = ["ReportBatchItemFailures"]
}

# ---------- Lambda roles ----------

resource "aws_cloudwatch_log_group" "chat" {
  name              = "/aws/lambda/${local.unique}-chat"
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

resource "aws_iam_role" "message" {
  name               = "${local.unique}-message-role"
  assume_role_policy = data.aws_iam_policy_document.lambda_assume.json
}

resource "aws_iam_role_policy" "message" {
  name = "message-policy"
  role = aws_iam_role.message.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      { Effect = "Allow", Action = "logs:CreateLogStream", Resource = aws_cloudwatch_log_group.chat.arn },
      { Effect = "Allow", Action = ["logs:PutLogEvents"], Resource = "${aws_cloudwatch_log_group.chat.arn}:*" },
      { Effect = "Allow", Action = ["dynamodb:PutItem", "dynamodb:GetItem", "dynamodb:UpdateItem", "dynamodb:DeleteItem"],
      Resource = [aws_dynamodb_table.connections.arn, aws_dynamodb_table.messages.arn] },
      { Effect = "Allow", Action = "sqs:SendMessage", Resource = aws_sqs_queue.fanout.arn },
    ]
  })
}

resource "aws_iam_role" "fanout" {
  name               = "${local.unique}-fanout-role"
  assume_role_policy = data.aws_iam_policy_document.lambda_assume.json
}

resource "aws_iam_role_policy" "fanout" {
  name = "fanout-policy"
  role = aws_iam_role.fanout.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      { Effect = "Allow", Action = "logs:CreateLogStream", Resource = aws_cloudwatch_log_group.chat.arn },
      { Effect = "Allow", Action = ["logs:PutLogEvents"], Resource = "${aws_cloudwatch_log_group.chat.arn}:*" },
      { Effect = "Allow", Action = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:GetQueueAttributes"],
      Resource = aws_sqs_queue.fanout.arn },
      { Effect = "Allow", Action = ["dynamodb:Query", "dynamodb:DeleteItem", "dynamodb:GetItem"],
      Resource = [aws_dynamodb_table.connections.arn, aws_dynamodb_table.messages.arn] },
      { Effect = "Allow", Action = "execute-api:ManageConnections",
      Resource = aws_apigatewayv2_stage.chat.execution_arn },
    ]
  })
}

# ---------- One zip, two handlers ----------

resource "aws_lambda_function" "message" {
  function_name    = "${local.unique}-message"
  role             = aws_iam_role.message.arn
  runtime          = "dotnet8"
  handler          = "ChatRelay::ChatRelay.MessageHandler::Handle"
  filename         = "${path.module}/dist/ChatRelay.zip"
  source_code_hash = filebase64sha256("${path.module}/dist/ChatRelay.zip")
  timeout          = 30
  memory_size      = 512

  environment {
    variables = {
      CONNECTIONS_TABLE = aws_dynamodb_table.connections.name
      MESSAGES_TABLE    = aws_dynamodb_table.messages.name
      FANOUT_QUEUE_URL  = aws_sqs_queue.fanout.id
    }
  }
}

resource "aws_lambda_function" "fanout" {
  function_name    = "${local.unique}-fanout"
  role             = aws_iam_role.fanout.arn
  runtime          = "dotnet8"
  handler          = "ChatRelay::ChatRelay.FanoutConsumer::Handle"
  filename         = "${path.module}/dist/ChatRelay.zip"
  source_code_hash = filebase64sha256("${path.module}/dist/ChatRelay.zip")
  timeout          = 60
  memory_size      = 512

  environment {
    variables = {
      CONNECTIONS_TABLE = aws_dynamodb_table.connections.name
      WS_ENDPOINT       = "https://${aws_apigatewayv2_api.chat.api_endpoint}/${local.api_stage}"
    }
  }
}