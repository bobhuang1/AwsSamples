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
  description = "Optional custom domain for CloudFront (also set acm_certificate_arn and point a DNS CNAME/alias at the distribution); leave null to use the *.cloudfront.net URL."

  validation {
    condition     = var.domain_name == null || var.acm_certificate_arn != null
    error_message = "domain_name needs acm_certificate_arn: CloudFront rejects an alias without an ACM certificate that covers it."
  }
}

variable "acm_certificate_arn" {
  type        = string
  default     = null
  description = "ARN of an issued ACM certificate for domain_name. CloudFront only accepts certificates from us-east-1. Only used when domain_name is set."

  validation {
    condition     = var.acm_certificate_arn == null || can(regex("^arn:[a-z-]+:acm:us-east-1:[0-9]{12}:certificate/.+$", var.acm_certificate_arn))
    error_message = "acm_certificate_arn must be the ARN of an ACM certificate in us-east-1 (CloudFront cannot use certificates from other regions)."
  }
}

variable "frontend_origin_path" {
  type        = string
  default     = ""
  description = "S3 bucket folder the CloudFront origin mounts, e.g. \"/build\"."
}

variable "audit_retention_days" {
  type        = number
  default     = 365
  description = "Days to keep audit objects before S3 expires them."
}
