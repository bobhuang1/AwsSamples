# WAF at the edge (CloudFront). Managed rules kill the common attack classes;
# the rate-based rule protects against credential-stuffing / scraper bursts.
# The API Gateway stage lives behind CloudFront in production; a regional ACL
# can be attached per-stage with aws_wafv2_web_acl_association.

resource "aws_wafv2_web_acl" "cloudfront" {
  name        = "${local.unique}-cloudfront-waf"
  description = "Edge WAF for the shopping frontend"
  scope       = "CLOUDFRONT"

  default_action {
    allow {}
  }

  rule {
    name     = "aws-managed-common"
    priority = 1

    override_action {
      none {}
    }

    statement {
      managed_rule_group_statement {
        vendor_name = "AWS"
        name        = "AWSManagedRulesCommonRuleSet"
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "shop-common"
      sampled_requests_enabled   = true
    }
  }

  rule {
    name     = "rate-limit"
    priority = 2

    action {
      block {}
    }

    statement {
      rate_based_statement {
        limit              = 5000
        aggregate_key_type = "IP"
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "shop-rate"
      sampled_requests_enabled   = true
    }
  }

  visibility_config {
    cloudwatch_metrics_enabled = true
    metric_name                = "shop-waf"
    sampled_requests_enabled   = true
  }
}