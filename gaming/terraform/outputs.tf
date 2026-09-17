output "api_endpoint" {
  description = "HTTP API base URL."
  value       = aws_apigatewayv2_stage.http.invoke_url
}

output "ws_endpoint" {
  description = "WebSocket endpoint (wss://...)."
  value       = aws_apigatewayv2_stage.ws.invoke_url
}

output "telemetry_stream" {
  value = aws_kinesis_stream.telemetry.name
}

output "telemetry_bucket" {
  value = aws_s3_bucket.telemetry.id
}