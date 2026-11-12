# Checkout pipeline:
#   API handler -> SQS orders queue (durable buffer + DLQ) -> worker Lambda
#   -> Step Functions (timed fulfillment) -> EventBridge (audit + downstream)

resource "aws_sqs_queue" "orders" {
  sqs_managed_sse_enabled    = true
  name                       = "${local.unique}-orders"
  visibility_timeout_seconds = 360 # at least 6x the 60 s worker timeout (AWS guidance)
  message_retention_seconds  = 86400
  delay_seconds              = 0
  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.orders_dlq.arn
    maxReceiveCount     = 5
  })
}

resource "aws_sqs_queue" "orders_dlq" {
  sqs_managed_sse_enabled   = true
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
      Principal = { Service = "states.amazonaws.com" }
    }]
  })
}

data "aws_iam_policy_document" "sfn" {
  statement {
    effect    = "Allow"
    actions   = ["lambda:InvokeFunction"]
    resources = [aws_lambda_function.orders_complete.arn]
  }
  # Step Functions logging goes through CloudWatch Logs log delivery, whose
  # actions do not support resource-level scoping.
  statement {
    effect = "Allow"
    actions = [
      "logs:CreateLogDelivery",
      "logs:GetLogDelivery",
      "logs:UpdateLogDelivery",
      "logs:DeleteLogDelivery",
      "logs:ListLogDeliveries",
      "logs:PutResourcePolicy",
      "logs:DescribeResourcePolicies",
      "logs:DescribeLogGroups",
    ]
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

# Step Functions needs its own log group; it is not a Lambda log group.
resource "aws_cloudwatch_log_group" "sfn" {
  name              = "/aws/vendedlogs/states/${local.unique}-orders"
  retention_in_days = 14
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
        Retry = [{
          ErrorEquals     = ["Lambda.ServiceException", "Lambda.AWSLambdaException", "Lambda.SdkClientException", "Lambda.TooManyRequestsException"]
          IntervalSeconds = 2
          MaxAttempts     = 4
          BackoffRate     = 2
        }]
        Catch = [{
          ErrorEquals = ["States.ALL"]
          Next        = "OrderFailed"
        }]
        End = true
      }
      OrderFailed = {
        Type  = "Fail"
        Error = "OrderCompletionFailed"
        Cause = "CompleteOrder failed after retries; the order stays in PROCESSING."
      }
    }
  })
}

resource "aws_sfn_state_machine" "orders" {
  # checkov:skip=CKV_AWS_284: No X-Ray tracing: a Wait plus one Lambda task; execution history and CloudWatch metrics are enough for the sample
  # checkov:skip=CKV_AWS_285: Log level ERROR without execution data on purpose: order contents stay out of the logs and the log cost stays low
  name       = "${local.unique}-orders"
  role_arn   = aws_iam_role.sfn.arn
  type       = "STANDARD"
  definition = local.order_definition

  logging_configuration {
    log_destination        = "${aws_cloudwatch_log_group.sfn.arn}:*"
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
  # checkov:skip=CKV_AWS_21: No versioning on purpose: objects are written once under random keys and expire by lifecycle rule, so old versions would only add cost
  bucket        = "${local.unique}-audit"
  force_destroy = true
}

# Audit objects are written once under random keys, so versioning would only
# multiply storage. Expire them instead of letting the bucket grow forever.
resource "aws_s3_bucket_lifecycle_configuration" "audit" {
  bucket = aws_s3_bucket.audit.id

  rule {
    id     = "expire-audit"
    status = "Enabled"
    filter {}

    expiration {
      days = var.audit_retention_days
    }

    abort_incomplete_multipart_upload {
      days_after_initiation = 7
    }
  }
}

resource "aws_s3_bucket_public_access_block" "audit" {
  bucket                  = aws_s3_bucket.audit.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}