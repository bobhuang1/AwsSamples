output "api_endpoint" {
  description = "HTTP API base URL."
  value       = aws_apigatewayv2_stage.http.invoke_url
}

output "ws_endpoint" {
  description = "WebSocket endpoint (wss://...)."
  value       = aws_apigatewayv2_stage.ws.invoke_url
}

output "report_results_policy_arn" {
  description = "IAM policy to attach to the game server role that reports match results (POST /matches/{id}/result)."
  value       = aws_iam_policy.report_results.arn
}

output "telemetry_stream" {
  value = aws_kinesis_stream.telemetry.name
}

output "telemetry_bucket" {
  value = aws_s3_bucket.telemetry.id
}

output "cognito_pool_id" {
  value = aws_cognito_user_pool.users.id
}

output "cognito_client_id" {
  value = aws_cognito_user_pool_client.web.id
}
