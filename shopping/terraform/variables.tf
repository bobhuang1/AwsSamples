variable "region" {
  type        = string
  default     = "us-east-1"
  description = "Primary AWS region."
}

variable "environment" {
  type        = string
  default     = "dev"
  description = "Environment label (dev/staging/prod); part of the resource names."
}

variable "name_prefix" {
  type        = string
  default     = "shop"
  description = "Short prefix for resource names (a random suffix keeps them unique)."
}

variable "domain_name" {
  type        = string
  default     = null
  description = "Optional custom domain for CloudFront; leave null to use the *.cloudfront.net URL."
}

variable "frontend_origin_path" {
  type        = string
  default     = ""
  description = "S3 bucket folder the CloudFront origin mounts, e.g. \"/build\"."
}