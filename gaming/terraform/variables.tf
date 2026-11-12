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

variable "leaderboard_shards" {
  type        = number
  default     = 8
  description = "Index keys the leaderboard is split over (1-32). Raising it later needs a backfill: a player moves to the new shard on their next scored match."

  validation {
    condition     = var.leaderboard_shards >= 1 && var.leaderboard_shards <= 32 && floor(var.leaderboard_shards) == var.leaderboard_shards
    error_message = "leaderboard_shards must be a whole number from 1 to 32."
  }
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
