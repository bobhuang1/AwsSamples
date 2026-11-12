# ---------- Shared pieces ----------

locals {
  # One log group, one role and one function per name. A function's log group
  # must be named exactly /aws/lambda/<function name>: that is the only group
  # Lambda writes to.
  lambda_names = ["api", "orders-outbox", "orders-worker", "orders-complete", "orders-audit"]

  # Only the calls each function makes (checked against the handler code).
  lambda_statements = {
    api = [
      { Effect = "Allow", Action = ["dynamodb:GetItem", "dynamodb:BatchGetItem", "dynamodb:Scan"], Resource = aws_dynamodb_table.products.arn },
      { Effect = "Allow", Action = "dynamodb:Query", Resource = "${aws_dynamodb_table.products.arn}/index/category-index" },
      # Checkout takes stock with a conditional update, so the API may update the stock
      # attribute of a product (and nothing else on the catalog).
      {
        Effect   = "Allow"
        Action   = "dynamodb:UpdateItem"
        Resource = aws_dynamodb_table.products.arn
        Condition = {
          "ForAllValues:StringEquals" = { "dynamodb:Attributes" = ["product_id", "stock"] }
        }
      },
      # Cart: GetItem (read), UpdateItem (atomic add / remove of a line), DeleteItem (empty the cart at checkout).
      { Effect = "Allow", Action = ["dynamodb:GetItem", "dynamodb:UpdateItem", "dynamodb:DeleteItem"], Resource = aws_dynamodb_table.carts.arn },
      { Effect = "Allow", Action = ["dynamodb:GetItem", "dynamodb:PutItem"], Resource = aws_dynamodb_table.orders.arn },
    ]
    orders-outbox = [
      { Effect = "Allow", Action = ["dynamodb:DescribeStream", "dynamodb:GetRecords", "dynamodb:GetShardIterator"], Resource = aws_dynamodb_table.orders.stream_arn },
      { Effect = "Allow", Action = "dynamodb:ListStreams", Resource = "*" },
      { Effect = "Allow", Action = "sqs:SendMessage", Resource = aws_sqs_queue.orders.arn },
      { Effect = "Allow", Action = "events:PutEvents", Resource = aws_cloudwatch_event_bus.orders.arn },
      # Where the stream mapping parks the metadata of a batch that kept failing.
      { Effect = "Allow", Action = "sqs:SendMessage", Resource = aws_sqs_queue.orders_outbox_failures.arn },
    ]
    orders-worker = [
      { Effect = "Allow", Action = "dynamodb:UpdateItem", Resource = aws_dynamodb_table.orders.arn },
      { Effect = "Allow", Action = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:GetQueueAttributes"], Resource = aws_sqs_queue.orders.arn },
      { Effect = "Allow", Action = "states:StartExecution", Resource = aws_sfn_state_machine.orders.arn },
      { Effect = "Allow", Action = "events:PutEvents", Resource = aws_cloudwatch_event_bus.orders.arn },
    ]
    orders-complete = [
      { Effect = "Allow", Action = "dynamodb:UpdateItem", Resource = aws_dynamodb_table.orders.arn },
      { Effect = "Allow", Action = "events:PutEvents", Resource = aws_cloudwatch_event_bus.orders.arn },
    ]
    orders-audit = [
      { Effect = "Allow", Action = "s3:PutObject", Resource = "${aws_s3_bucket.audit.arn}/*" },
    ]
  }
}

resource "aws_cloudwatch_log_group" "lambda" {
  for_each          = toset(local.lambda_names)
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

resource "aws_iam_role" "lambda" {
  for_each           = toset(local.lambda_names)
  name               = "${local.unique}-${each.key}-role"
  assume_role_policy = data.aws_iam_policy_document.lambda_assume.json
}

resource "aws_iam_role_policy" "lambda" {
  for_each = toset(local.lambda_names)
  name     = "${each.key}-policy"
  role     = aws_iam_role.lambda[each.key].id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = concat([
      { Effect = "Allow", Action = "logs:CreateLogStream", Resource = aws_cloudwatch_log_group.lambda[each.key].arn },
      { Effect = "Allow", Action = "logs:PutLogEvents", Resource = "${aws_cloudwatch_log_group.lambda[each.key].arn}:*" },
    ], local.lambda_statements[each.key])
  })
}

# ---------- Shipping API handler ----------

resource "aws_lambda_function" "api" {
  function_name    = "${local.unique}-api"
  role             = aws_iam_role.lambda["api"].arn
  runtime          = "dotnet10"
  handler          = "ShoppingApi::ShoppingApi.ApiHandler::Handle"
  filename         = "${path.module}/dist/ShoppingApi.zip"
  source_code_hash = filebase64sha256("${path.module}/dist/ShoppingApi.zip")
  timeout          = 30
  memory_size      = 512
  depends_on       = [aws_cloudwatch_log_group.lambda["api"]]

  environment {
    variables = {
      PRODUCTS_TABLE = aws_dynamodb_table.products.name
      CARTS_TABLE    = aws_dynamodb_table.carts.name
      ORDERS_TABLE   = aws_dynamodb_table.orders.name
    }
  }
}

# ---------- Order outbox: orders table stream -> SQS + EventBridge ----------
# The API only writes the order row (together with the cart delete, in one
# transaction). This function picks new rows up from the table stream and
# hands them to the pipeline, so an order can't be stored without being queued.

resource "aws_lambda_function" "orders_outbox" {
  function_name    = "${local.unique}-orders-outbox"
  role             = aws_iam_role.lambda["orders-outbox"].arn
  runtime          = "dotnet10"
  handler          = "OrderWorkflow::OrderWorkflow.Functions::PublishOrder"
  filename         = "${path.module}/dist/OrderWorkflow.zip"
  source_code_hash = filebase64sha256("${path.module}/dist/OrderWorkflow.zip")
  timeout          = 60
  memory_size      = 256
  depends_on       = [aws_cloudwatch_log_group.lambda["orders-outbox"]]

  environment {
    variables = {
      ORDERS_QUEUE_URL = aws_sqs_queue.orders.id
      ORDER_EVENT_BUS  = aws_cloudwatch_event_bus.orders.name
    }
  }
}

resource "aws_lambda_event_source_mapping" "orders_outbox" {
  event_source_arn        = aws_dynamodb_table.orders.stream_arn
  function_name           = aws_lambda_function.orders_outbox.arn
  starting_position       = "TRIM_HORIZON"
  batch_size              = 10
  function_response_types = ["ReportBatchItemFailures"]

  # A record that keeps failing must not hold up the orders behind it until the stream's
  # 24 h retention drops it: retry a few times, split the batch to isolate it, then park it.
  maximum_retry_attempts         = 5
  maximum_record_age_in_seconds  = 3600
  bisect_batch_on_function_error = true

  destination_config {
    on_failure {
      destination_arn = aws_sqs_queue.orders_outbox_failures.arn
    }
  }

  filter_criteria {
    filter {
      pattern = jsonencode({ eventName = ["INSERT"] })
    }
  }
}

# ---------- Order workflow handler (SQS processor + Step Functions tasks + audit) ----------

resource "aws_lambda_function" "orders_worker" {
  function_name    = "${local.unique}-orders-worker"
  role             = aws_iam_role.lambda["orders-worker"].arn
  runtime          = "dotnet10"
  handler          = "OrderWorkflow::OrderWorkflow.Functions::ProcessOrder"
  filename         = "${path.module}/dist/OrderWorkflow.zip"
  source_code_hash = filebase64sha256("${path.module}/dist/OrderWorkflow.zip")
  timeout          = 60
  memory_size      = 512
  depends_on       = [aws_cloudwatch_log_group.lambda["orders-worker"]]

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
  role             = aws_iam_role.lambda["orders-complete"].arn
  runtime          = "dotnet10"
  handler          = "OrderWorkflow::OrderWorkflow.Functions::CompleteOrder"
  filename         = "${path.module}/dist/OrderWorkflow.zip"
  source_code_hash = filebase64sha256("${path.module}/dist/OrderWorkflow.zip")
  timeout          = 60
  memory_size      = 512
  depends_on       = [aws_cloudwatch_log_group.lambda["orders-complete"]]

  environment {
    variables = {
      ORDERS_TABLE    = aws_dynamodb_table.orders.name
      ORDER_EVENT_BUS = aws_cloudwatch_event_bus.orders.name
    }
  }
}

resource "aws_lambda_function" "orders_audit" {
  function_name    = "${local.unique}-orders-audit"
  role             = aws_iam_role.lambda["orders-audit"].arn
  runtime          = "dotnet10"
  handler          = "OrderWorkflow::OrderWorkflow.Functions::AuditOrder"
  filename         = "${path.module}/dist/OrderWorkflow.zip"
  source_code_hash = filebase64sha256("${path.module}/dist/OrderWorkflow.zip")
  timeout          = 60
  memory_size      = 256
  depends_on       = [aws_cloudwatch_log_group.lambda["orders-audit"]]

  environment {
    variables = {
      AUDIT_BUCKET = aws_s3_bucket.audit.id
    }
  }
}

