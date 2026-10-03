# ---------- HTTP API: matchmaking + leaderboard ----------

resource "aws_apigatewayv2_api" "http" {
  name          = "${local.unique}-api"
  protocol_type = "HTTP"

  cors_configuration {
    allow_origins = ["*"]
    allow_methods = ["GET", "POST"]
    allow_headers = ["content-type", "authorization"]
    max_age       = 300
  }
}

resource "aws_apigatewayv2_integration" "http" {
  api_id                 = aws_apigatewayv2_api.http.id
  integration_type       = "AWS_PROXY"
  integration_uri        = aws_lambda_function.http.invoke_arn
  payload_format_version = "2.0"
}

# Public leaderboard; every other route needs a Cognito ID token, and the
# handler takes the player id from its "sub" claim.
resource "aws_apigatewayv2_route" "leaderboard_public" {
  api_id    = aws_apigatewayv2_api.http.id
  route_key = "GET /leaderboard/top"
  target    = "integrations/${aws_apigatewayv2_integration.http.id}"
}

resource "aws_apigatewayv2_authorizer" "jwt" {
  api_id           = aws_apigatewayv2_api.http.id
  authorizer_type  = "JWT"
  identity_sources = ["$request.header.Authorization"]
  name             = "player-jwt"

  jwt_configuration {
    audience = [aws_cognito_user_pool_client.web.id]
    issuer   = local.jwt_issuer
  }
}

resource "aws_apigatewayv2_route" "http" {
  api_id             = aws_apigatewayv2_api.http.id
  route_key          = "$default"
  target             = "integrations/${aws_apigatewayv2_integration.http.id}"
  authorization_type = "JWT"
  authorizer_id      = aws_apigatewayv2_authorizer.jwt.id
}

resource "aws_apigatewayv2_deployment" "http" {
  api_id      = aws_apigatewayv2_api.http.id
  description = "redeploy"
  triggers = {
    redeployment = sha1(join(",", [
      aws_apigatewayv2_integration.http.id,
      aws_apigatewayv2_route.leaderboard_public.id,
      aws_apigatewayv2_route.http.id,
    ]))
  }
  depends_on = [aws_apigatewayv2_route.http, aws_apigatewayv2_route.leaderboard_public]
}

resource "aws_apigatewayv2_stage" "http" {
  api_id        = aws_apigatewayv2_api.http.id
  name          = local.api_stage
  deployment_id = aws_apigatewayv2_deployment.http.id
}

resource "aws_lambda_permission" "http" {
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.http.function_name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.http.execution_arn}/*/*"
}

# ---------- WebSocket API: live game events ----------

resource "aws_apigatewayv2_api" "ws" {
  name                       = "${local.unique}-ws"
  protocol_type              = "WEBSOCKET"
  route_selection_expression = "$request.body.action"
}

resource "aws_apigatewayv2_integration" "ws" {
  api_id                 = aws_apigatewayv2_api.ws.id
  integration_type       = "AWS_PROXY"
  integration_uri        = aws_lambda_function.ws.invoke_arn
  integration_method     = "POST"
  payload_format_version = "1.0"
}

resource "aws_apigatewayv2_route" "connect" {
  api_id             = aws_apigatewayv2_api.ws.id
  route_key          = "$connect"
  target             = "integrations/${aws_apigatewayv2_integration.ws.id}"
  authorization_type = "CUSTOM"
  authorizer_id      = aws_apigatewayv2_authorizer.connect.id
}

resource "aws_apigatewayv2_route" "disconnect" {
  api_id    = aws_apigatewayv2_api.ws.id
  route_key = "$disconnect"
  target    = "integrations/${aws_apigatewayv2_integration.ws.id}"
}

resource "aws_apigatewayv2_route" "default" {
  api_id    = aws_apigatewayv2_api.ws.id
  route_key = "$default"
  target    = "integrations/${aws_apigatewayv2_integration.ws.id}"
}

resource "aws_apigatewayv2_deployment" "ws" {
  api_id      = aws_apigatewayv2_api.ws.id
  description = "redeploy"
  triggers = {
    redeployment = sha1(join(",", [
      aws_apigatewayv2_integration.ws.id,
      aws_apigatewayv2_authorizer.connect.id,
      aws_apigatewayv2_route.connect.id,
      aws_apigatewayv2_route.disconnect.id,
      aws_apigatewayv2_route.default.id,
    ]))
  }
  depends_on = [
    aws_apigatewayv2_route.connect,
    aws_apigatewayv2_route.disconnect,
    aws_apigatewayv2_route.default,
  ]
}

resource "aws_apigatewayv2_stage" "ws" {
  api_id        = aws_apigatewayv2_api.ws.id
  name          = local.api_stage
  deployment_id = aws_apigatewayv2_deployment.ws.id
}

resource "aws_lambda_permission" "ws" {
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.ws.function_name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.ws.execution_arn}/*/*"
}