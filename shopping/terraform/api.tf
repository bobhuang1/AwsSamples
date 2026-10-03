# HTTP API Gateway (v2). One catch-all default route hits the API handler,
# which dispatches by path so the route table stays tiny; a public
# GET /products route shows the mixed-security pattern with Cognito JWT auth
# applied to everything else.

# ---------- Cognito user pool for JWTs ----------

resource "aws_cognito_user_pool" "shoppers" {
  name = "${local.unique}-shoppers"

  password_policy {
    minimum_length    = 8
    require_lowercase = true
    require_uppercase = true
    require_numbers   = true
    require_symbols   = false
  }

  schema {
    name                = "email"
    attribute_data_type = "String"
    required            = true
    mutable             = true
  }

  auto_verified_attributes = ["email"]
}

resource "aws_cognito_user_pool_client" "web" {
  name            = "${local.unique}-web"
  user_pool_id    = aws_cognito_user_pool.shoppers.id
  generate_secret = false

  explicit_auth_flows = ["ALLOW_USER_SRP_AUTH", "ALLOW_REFRESH_TOKEN_AUTH", "ALLOW_USER_PASSWORD_AUTH"]

  allowed_oauth_flows           = ["code"]
  allowed_oauth_scopes          = ["openid", "email", "profile"]
  supported_identity_providers  = ["COGNITO"]
  callback_urls                 = ["https://localhost/oauth2/idpresponse"]
  logout_urls                   = ["https://localhost/signout"]
  prevent_user_existence_errors = "ENABLED"
}

# ---------- API Gateway ----------

resource "aws_apigatewayv2_api" "shop" {
  name          = "${local.unique}-api"
  protocol_type = "HTTP"

  cors_configuration {
    allow_origins = ["*"]
    allow_methods = ["GET", "POST", "DELETE"]
    allow_headers = ["content-type", "authorization", "idempotency-key"]
    max_age       = 300
  }
}

resource "aws_apigatewayv2_integration" "shop" {
  api_id                 = aws_apigatewayv2_api.shop.id
  integration_type       = "AWS_PROXY"
  integration_uri        = aws_lambda_function.api.invoke_arn
  payload_format_version = "2.0"
}

# Public catalog route (no auth) + catch-all default (JWT protected).
resource "aws_apigatewayv2_route" "products_public" {
  api_id    = aws_apigatewayv2_api.shop.id
  route_key = "GET /products"
  target    = "integrations/${aws_apigatewayv2_integration.shop.id}"
}

resource "aws_apigatewayv2_authorizer" "jwt" {
  api_id           = aws_apigatewayv2_api.shop.id
  authorizer_type  = "JWT"
  identity_sources = ["$request.header.Authorization"]
  name             = "shopper-jwt"

  jwt_configuration {
    audience = [aws_cognito_user_pool_client.web.id]
    issuer   = "https://cognito-idp.${var.region}.amazonaws.com/${aws_cognito_user_pool.shoppers.id}"
  }
}

resource "aws_apigatewayv2_route" "default" {
  api_id             = aws_apigatewayv2_api.shop.id
  route_key          = "$default"
  target             = "integrations/${aws_apigatewayv2_integration.shop.id}"
  authorization_type = "JWT"
  authorizer_id      = aws_apigatewayv2_authorizer.jwt.id
}

resource "aws_apigatewayv2_deployment" "shop" {
  api_id      = aws_apigatewayv2_api.shop.id
  description = "redeploy"
  triggers = {
    redeployment = sha1(join(",", [
      aws_apigatewayv2_integration.shop.id,
      aws_apigatewayv2_route.products_public.id,
      aws_apigatewayv2_route.default.id,
    ]))
  }
  depends_on = [
    aws_apigatewayv2_route.products_public,
    aws_apigatewayv2_route.default,
  ]
}

resource "aws_apigatewayv2_stage" "shop" {
  api_id        = aws_apigatewayv2_api.shop.id
  name          = local.api_stage
  deployment_id = aws_apigatewayv2_deployment.shop.id
}

resource "aws_lambda_permission" "api" {
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.api.function_name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.shop.execution_arn}/*/*"
}