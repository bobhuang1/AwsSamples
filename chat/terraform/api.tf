# WebSocket API: clients connect, send chat messages, and get them fanned out
# to everyone in the same room. The write path ($default) is a quick DynamoDB
# put + SQS enqueue; the heavy fan-out happens in the consumer lambda, so a
# delivery storm is decoupled from the socket writer.

resource "aws_apigatewayv2_api" "chat" {
  name          = "${local.unique}-ws"
  protocol_type = "WEBSOCKET"
}

resource "aws_apigatewayv2_integration" "chat" {
  api_id                 = aws_apigatewayv2_api.chat.id
  integration_type       = "AWS_PROXY"
  integration_uri        = aws_lambda_function.message.invoke_arn
  integration_method     = "POST"
  payload_format_version = "1.0"
}

resource "aws_apigatewayv2_route" "connect" {
  api_id             = aws_apigatewayv2_api.chat.id
  route_key          = "$connect"
  target             = "integrations/${aws_apigatewayv2_integration.chat.id}"
  authorization_type = "CUSTOM"
  authorizer_id      = aws_apigatewayv2_authorizer.connect.id
}

resource "aws_apigatewayv2_route" "disconnect" {
  api_id    = aws_apigatewayv2_api.chat.id
  route_key = "$disconnect"
  target    = "integrations/${aws_apigatewayv2_integration.chat.id}"
}

resource "aws_apigatewayv2_route" "default" {
  api_id    = aws_apigatewayv2_api.chat.id
  route_key = "$default"
  target    = "integrations/${aws_apigatewayv2_integration.chat.id}"
}

resource "aws_apigatewayv2_deployment" "chat" {
  api_id      = aws_apigatewayv2_api.chat.id
  description = "redeploy"
  triggers = {
    redeployment = sha1(join(",", [
      aws_apigatewayv2_integration.chat.id,
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

resource "aws_apigatewayv2_stage" "chat" {
  api_id        = aws_apigatewayv2_api.chat.id
  name          = local.api_stage
  deployment_id = aws_apigatewayv2_deployment.chat.id
}

resource "aws_lambda_permission" "message" {
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.message.function_name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.chat.execution_arn}/*/*"
}