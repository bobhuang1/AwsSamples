# Checkout pipeline:
#   API handler -> SQS orders queue (durable buffer + DLQ) -> worker Lambda
#   -> Step Functions (timed fulfillment) -> EventBridge (audit + downstream)

resource "aws_sqs_queue" "orders" {
  name                       = "${local.unique}-orders"
  visibility_timeout_seconds = 90
  message_retention_seconds  = 86400
  delay_seconds              = 0
  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.orders_dlq.arn
    maxReceiveCount     = 5
  })
}

resource "aws_sqs_queue" "orders_dlq" {
  name                      = "${local.unique}-orders-dlq"
  message_retention_seconds = 1209600
}

# Process orders from the queue in batches; report batch-item failures so the
# good messages move on while bad ones redrive to the DLQ.
resource "aws_lambda_event_source_mapping" "orders" {
  event_source_arn                   = aws_sqs_queue.orders.arn
  function_name                      = aws_lambda_function.orders_worker.arn
  enabled                            = true
  batch_size                         = 5
  maximum_batching_window_in_seconds = 5
  function_response_types            = ["ReportBatchItemFailures"]
}

# ---------- Step Functions: timed fulfillment ----------

resource "aws_iam_role" "sfn" {
  name = "${local.unique}-sfn-role"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Action    = "sts:AssumeRole"
      Principal = { Service = ["states.amazonaws.com", "events.amazonaws.com"] }
    }]
  })
}

data "aws_iam_policy_document" "sfn" {
  statement {
    effect    = "Allow"
    actions   = ["lambda:InvokeFunction"]
    resources = [aws_lambda_function.orders_complete.arn]
  }
  statement {
    effect    = "Allow"
    actions   = ["logs:CreateLogGroup", "logs:CreateLogStream", "logs:PutLogEvents"]
    resources = ["*"]
  }
}

resource "aws_iam_policy" "sfn" {
  name   = "${local.unique}-sfn-policy"
  policy = data.aws_iam_policy_document.sfn.json
}

resource "aws_iam_role_policy_attachment" "sfn" {
  role       = aws_iam_role.sfn.name
  policy_arn = aws_iam_policy.sfn.arn
}

locals {
  # ASL definition, rendered from plain JSON so escaping is a non-issue.
  order_definition = jsonencode({
    Comment = "Order fulfillment pipeline"
    StartAt = "HoldForFulfillment"
    States = {
      HoldForFulfillment = {
        Type    = "Wait"
        Seconds = 15
        Next    = "CompleteOrder"
      }
      CompleteOrder = {
        Type     = "Task"
        Resource = aws_lambda_function.orders_complete.arn
        End      = true
      }
    }
  })
}

resource "aws_sfn_state_machine" "orders" {
  name       = "${local.unique}-orders"
  role_arn   = aws_iam_role.sfn.arn
  type       = "STANDARD"
  definition = local.order_definition

  logging_configuration {
    log_destination        = "${aws_cloudwatch_log_group.orders.arn}:*"
    include_execution_data = false
    level                  = "ERROR"
  }
}

# ---------- EventBridge: order lifecycle events for downstream consumers ----------

resource "aws_cloudwatch_event_bus" "orders" {
  name = "${local.unique}-orders"
}

resource "aws_cloudwatch_event_rule" "order_lifecycle" {
  name           = "${local.unique}-order-lifecycle"
  event_bus_name = aws_cloudwatch_event_bus.orders.name
  event_pattern = jsonencode({
    source      = ["com.sample.shop"]
    detail-type = ["order.placed", "order.processing", "order.completed"]
  })
}

resource "aws_cloudwatch_event_target" "audit" {
  rule           = aws_cloudwatch_event_rule.order_lifecycle.name
  event_bus_name = aws_cloudwatch_event_bus.orders.name
  target_id      = "audit"
  arn            = aws_lambda_function.orders_audit.arn
}

resource "aws_lambda_permission" "audit" {
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.orders_audit.function_name
  principal     = "events.amazonaws.com"
  source_arn    = aws_cloudwatch_event_rule.order_lifecycle.arn
}

# ---------- Audit trail bucket ----------

resource "aws_s3_bucket" "audit" {
  bucket        = "${local.unique}-audit"
  force_destroy = true
}

resource "aws_s3_bucket_versioning" "audit" {
  bucket = aws_s3_bucket.audit.id
  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_public_access_block" "audit" {
  bucket                  = aws_s3_bucket.audit.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}