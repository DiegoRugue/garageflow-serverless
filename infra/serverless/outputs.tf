output "customerAuthenticationAliasArn" {
  description = "Live customer authentication Lambda alias ARN."
  value       = aws_lambda_alias.customer_authentication.arn
}

output "requestAuthorizerAliasArn" {
  description = "Live request authorizer Lambda alias ARN."
  value       = aws_lambda_alias.request_authorizer.arn
}
