# Static frontend: S3 website bucket served through CloudFront (WAF-filtered).
# The API is hit from the browser directly, so CORS is allowed on the API.

resource "aws_s3_bucket" "frontend" {
  bucket        = "${local.unique}-frontend"
  force_destroy = true
}

resource "aws_s3_bucket_policy" "frontend" {
  bucket = aws_s3_bucket.frontend.id
  # The website endpoint serves objects publicly; no permissions needed
  # on the S3 API side. Kept explicit so the serving behaviour is obvious.
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid       = "PublicReadGetObject"
        Effect    = "Allow"
        Principal = "*"
        Action    = ["s3:GetObject"]
        Resource  = "${aws_s3_bucket.frontend.arn}/*"
      }
    ]
  })
}

resource "aws_cloudfront_distribution" "frontend" {
  enabled             = true
  default_root_object = "index.html"
  price_class         = "PriceClass_100"
  web_acl_id          = aws_wafv2_web_acl.cloudfront.id
  aliases             = var.domain_name == null ? [] : [var.domain_name]

  origin {
    domain_name = "${aws_s3_bucket.frontend.bucket}.s3-website-${var.region}.amazonaws.com"
    origin_id   = "s3-frontend"
    origin_path = var.frontend_origin_path

    custom_origin_config {
      http_port              = 80
      https_port             = 443
      origin_protocol_policy = "http-only"
      origin_ssl_protocols   = ["TLSv1.2"]
    }
  }

  default_cache_behavior {
    target_origin_id       = "s3-frontend"
    viewer_protocol_policy = "redirect-to-https"
    compress               = true

    allowed_methods = ["GET", "HEAD", "OPTIONS"]
    cached_methods  = ["GET", "HEAD"]

    forwarded_values {
      query_string = false
      cookies {
        forward = "none"
      }
    }

    min_ttl     = 0
    default_ttl = 300
    max_ttl     = 86400
  }

  restrictions {
    geo_restriction {
      restriction_type = "none"
    }
  }

  viewer_certificate {
    cloudfront_default_certificate = true
  }
}