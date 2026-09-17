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