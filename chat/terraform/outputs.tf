output "ws_endpoint" {
  description = "WebSocket endpoint for the chat client (wss://...)."
  value       = aws_apigatewayv2_stage.chat.invoke_url
}

output "api_id" {
  value = aws_apigatewayv2_api.chat.id
}

output "fanout_queue" {
  value = aws_sqs_queue.fanout.id
}

output "cognito_pool_id" {
  value = aws_cognito_user_pool.users.id
}

output "cognito_client_id" {
  value = aws_cognito_user_pool_client.web.id
}
