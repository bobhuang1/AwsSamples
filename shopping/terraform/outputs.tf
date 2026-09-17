output "website_url" {
  description = "Front door (CloudFront distribution)."
  value       = "https://${aws_cloudfront_distribution.frontend.domain_name}"
}

output "api_endpoint" {
  description = "HTTP API endpoint for the shopping API."
  value       = aws_apigatewayv2_stage.shop.invoke_url
}

output "api_stage" {
  description = "API stage name."
  value       = aws_apigatewayv2_stage.shop.name
}

output "cognito_pool_id" {
  value = aws_cognito_user_pool.shoppers.id
}

output "cognito_client_id" {
  value = aws_cognito_user_pool_client.web.id
}

output "state_machine_arn" {
  value = aws_sfn_state_machine.orders.arn
}

output "event_bus_name" {
  value = aws_cloudwatch_event_bus.orders.name
}