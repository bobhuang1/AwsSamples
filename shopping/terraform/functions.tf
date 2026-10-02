# ---------- Shared pieces ----------

resource "aws_cloudwatch_log_group" "api" {
  name              = "/aws/lambda/${local.unique}-api"
  retention_in_days = 14
}

resource "aws_cloudwatch_log_group" "orders" {
  name              = "/aws/lambda/${local.unique}-orders"
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

resource "aws_iam_role" "api" {
  name               = "${local.unique}-api-role"
  assume_role_policy = data.aws_iam_policy_document.lambda_assume.json
}

resource "aws_iam_role_policy" "api" {
  name = "api-policy"
  role = aws_iam_role.api.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      { Effect = "Allow", Action = "logs:CreateLogStream", Resource = aws_cloudwatch_log_group.api.arn },
      { Effect = "Allow", Action = ["logs:PutLogEvents"], Resource = "${aws_cloudwatch_log_group.api.arn}:*" },
      { Effect = "Allow", Action = ["dynamodb:GetItem", "dynamodb:Query", "dynamodb:Scan", "dynamodb:PutItem", "dynamodb:UpdateItem", "dynamodb:DeleteItem"],
      Resource = [aws_dynamodb_table.products.arn, aws_dynamodb_table.carts.arn, aws_dynamodb_table.orders.arn, "${aws_dynamodb_table.products.arn}/index/*", "${aws_dynamodb_table.carts.arn}/index/*", "${aws_dynamodb_table.orders.arn}/index/*"] },
      { Effect = "Allow", Action = "sqs:SendMessage", Resource = aws_sqs_queue.orders.arn },
      { Effect = "Allow", Action = "events:PutEvents", Resource = aws_cloudwatch_event_bus.orders.arn },
    ]
  })
}

resource "aws_iam_role" "orders" {
  name               = "${local.unique}-orders-role"
  assume_role_policy = data.aws_iam_policy_document.lambda_assume.json
}

resource "aws_iam_role_policy" "orders" {
  name = "orders-policy"
  role = aws_iam_role.orders.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      { Effect = "Allow", Action = "logs:CreateLogStream", Resource = aws_cloudwatch_log_group.orders.arn },
      { Effect = "Allow", Action = ["logs:PutLogEvents"], Resource = "${aws_cloudwatch_log_group.orders.arn}:*" },
      { Effect = "Allow", Action = ["dynamodb:GetItem", "dynamodb:UpdateItem"],
      Resource = [aws_dynamodb_table.orders.arn, "${aws_dynamodb_table.orders.arn}/index/*"] },
      { Effect = "Allow", Action = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:GetQueueAttributes"],
      Resource = aws_sqs_queue.orders.arn },
      { Effect = "Allow", Action = "states:StartExecution", Resource = aws_sfn_state_machine.orders.arn },
      { Effect = "Allow", Action = "events:PutEvents", Resource = aws_cloudwatch_event_bus.orders.arn },
      { Effect = "Allow", Action = ["s3:PutObject"], Resource = "${aws_s3_bucket.audit.arn}/*" },
    ]
  })
}

# ---------- Shipping API handler ----------

resource "aws_lambda_function" "api" {
  function_name    = "${local.unique}-api"
  role             = aws_iam_role.api.arn
  runtime          = "dotnet8"
  handler          = "ShoppingApi::ShoppingApi.ApiHandler::Handle"
  filename         = "${path.module}/dist/ShoppingApi.zip"
  source_code_hash = filebase64sha256("${path.module}/dist/ShoppingApi.zip")
  timeout          = 30
  memory_size      = 512

  environment {
    variables = {
      PRODUCTS_TABLE   = aws_dynamodb_table.products.name
      CARTS_TABLE      = aws_dynamodb_table.carts.name
      ORDERS_TABLE     = aws_dynamodb_table.orders.name
      ORDERS_QUEUE_URL = aws_sqs_queue.orders.id
      ORDER_EVENT_BUS  = aws_cloudwatch_event_bus.orders.name
    }
  }
}

# ---------- Order workflow handler (SQS processor + Step Functions tasks + audit) ----------

resource "aws_lambda_function" "orders_worker" {
  function_name    = "${local.unique}-orders-worker"
  role             = aws_iam_role.orders.arn
  runtime          = "dotnet8"
  handler          = "OrderWorkflow::OrderWorkflow.Functions::ProcessOrder"
  filename         = "${path.module}/dist/OrderWorkflow.zip"
  source_code_hash = filebase64sha256("${path.module}/dist/OrderWorkflow.zip")
  timeout          = 60
  memory_size      = 512

  environment {
    variables = {
      ORDERS_TABLE    = aws_dynamodb_table.orders.name
      STATE_MACHINE   = aws_sfn_state_machine.orders.arn
      ORDER_EVENT_BUS = aws_cloudwatch_event_bus.orders.name
    }
  }
}

resource "aws_lambda_function" "orders_complete" {
  function_name    = "${local.unique}-orders-complete"
  role             = aws_iam_role.orders.arn
  runtime          = "dotnet8"
  handler          = "OrderWorkflow::OrderWorkflow.Functions::CompleteOrder"
  filename         = "${path.module}/dist/OrderWorkflow.zip"
  source_code_hash = filebase64sha256("${path.module}/dist/OrderWorkflow.zip")
  timeout          = 60
  memory_size      = 512

  environment {
    variables = {
      ORDERS_TABLE    = aws_dynamodb_table.orders.name
      ORDER_EVENT_BUS = aws_cloudwatch_event_bus.orders.name
    }
  }
}

resource "aws_lambda_function" "orders_audit" {
  function_name    = "${local.unique}-orders-audit"
  role             = aws_iam_role.orders.arn
  runtime          = "dotnet8"
  handler          = "OrderWorkflow::OrderWorkflow.Functions::AuditOrder"
  filename         = "${path.module}/dist/OrderWorkflow.zip"
  source_code_hash = filebase64sha256("${path.module}/dist/OrderWorkflow.zip")
  timeout          = 60
  memory_size      = 256

  environment {
    variables = {
      AUDIT_BUCKET = aws_s3_bucket.audit.id
    }
  }
}

