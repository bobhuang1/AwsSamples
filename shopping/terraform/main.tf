resource "random_id" "suffix" {
  byte_length = 3
}

locals {
  suffix = lower(random_id.suffix.hex)
  name   = "${var.name_prefix}-${var.environment}"
  unique = "${local.name}-${local.suffix}"

  tags = {
    Environment = var.environment
    ManagedBy   = "terraform"
    Sample      = "shopping"
  }

  api_stage = "v1"
}