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