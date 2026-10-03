# Sign-in for the sample: a Cognito user pool issues the ID tokens that the
# APIs check. The WebSocket $connect route uses a Lambda REQUEST authorizer
# (ConnectAuthorizer) because browsers cannot send an Authorization header on
# a WebSocket handshake; clients pass the ID token as ?token=.

resource "aws_cognito_user_pool" "users" {
  name = "${local.unique}-users"

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
  user_pool_id    = aws_cognito_user_pool.users.id
  generate_secret = false

  explicit_auth_flows           = ["ALLOW_USER_SRP_AUTH", "ALLOW_REFRESH_TOKEN_AUTH", "ALLOW_USER_PASSWORD_AUTH"]
  prevent_user_existence_errors = "ENABLED"
}

locals {
  jwt_issuer = "https://cognito-idp.${var.region}.amazonaws.com/${aws_cognito_user_pool.users.id}"
}

resource "aws_iam_role" "authorizer" {
  name               = "${local.unique}-authorizer-role"
  assume_role_policy = data.aws_iam_policy_document.lambda_assume.json
}

resource "aws_iam_role_policy" "authorizer" {
  name = "authorizer-policy"
  role = aws_iam_role.authorizer.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      { Effect = "Allow", Action = "logs:CreateLogStream", Resource = aws_cloudwatch_log_group.game.arn },
      { Effect = "Allow", Action = ["logs:PutLogEvents"], Resource = "${aws_cloudwatch_log_group.game.arn}:*" },
    ]
  })
}

resource "aws_lambda_function" "authorizer" {
  function_name    = "${local.unique}-authorizer"
  role             = aws_iam_role.authorizer.arn
  runtime          = "dotnet8"
  handler          = "GameBackend::GameBackend.ConnectAuthorizer::Handle"
  filename         = "${path.module}/dist/GameBackend.zip"
  source_code_hash = filebase64sha256("${path.module}/dist/GameBackend.zip")
  timeout          = 10
  memory_size      = 512

  environment {
    variables = {
      JWT_ISSUER   = local.jwt_issuer
      JWT_AUDIENCE = aws_cognito_user_pool_client.web.id
    }
  }
}

resource "aws_apigatewayv2_authorizer" "connect" {
  api_id           = aws_apigatewayv2_api.ws.id
  authorizer_type  = "REQUEST"
  authorizer_uri   = aws_lambda_function.authorizer.invoke_arn
  identity_sources = ["route.request.querystring.token"]
  name             = "connect-token"
}

resource "aws_lambda_permission" "authorizer" {
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.authorizer.function_name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.ws.execution_arn}/authorizers/${aws_apigatewayv2_authorizer.connect.id}"
}
