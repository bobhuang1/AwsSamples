variable "region" {
  type        = string
  default     = "us-east-1"
  description = "Primary AWS region."
}

variable "environment" {
  type        = string
  default     = "dev"
  description = "Environment label (dev/staging/prod)."
}

variable "name_prefix" {
  type        = string
  default     = "game"
  description = "Short prefix for resource names."
}

variable "telemetry_shard_count" {
  type        = number
  default     = 1
  description = "Provisioned Kinesis shards for telemetry (each is billed hourly, idle or not)."
}

variable "telemetry_retention_days" {
  type        = number
  default     = 365
  description = "Days to keep telemetry objects before S3 expires them."
}
