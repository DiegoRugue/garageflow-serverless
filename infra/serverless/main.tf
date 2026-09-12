locals {
  customer_authentication_name = "garageflow-${var.environment}-customer-authentication"
  request_authorizer_name      = "garageflow-${var.environment}-request-authorizer"
  ingress_hostname = split(":", trimsuffix(
    replace(lower(var.ingress_contract.internalApiBaseUrl), "/^https?:\\/\\//", ""), "/"
  ))[0]
  protected_arns = [
    var.platform_contract.apiGatewayExecutionArn,
    var.platform_contract.jwtSecretArn,
    var.platform_contract.internalAuthSecretArn,
    var.platform_contract.bootstrapSecretArn,
    var.platform_contract.webhookSecretArn,
    var.platform_contract.snsTopicArn,
    var.ingress_contract.listenerArn,
    var.authentication_role_arn,
    var.authorizer_role_arn,
  ]
}

data "aws_caller_identity" "current" {}

data "aws_subnet" "private_application" {
  for_each = toset(var.platform_contract.privateApplicationSubnetIds)
  id       = each.value
}

data "aws_security_group" "authentication" {
  id = var.ingress_contract.authenticationSecurityGroupId
}

data "aws_security_group" "vpc_link" {
  id = var.ingress_contract.vpcLinkSecurityGroupId
}

data "aws_lb_listener" "ingress" {
  arn = var.ingress_contract.listenerArn
}

data "aws_lb" "ingress" {
  arn = data.aws_lb_listener.ingress.load_balancer_arn
}

resource "terraform_data" "deployment_guard" {
  input = {
    environment = var.environment
    vpc_id      = var.platform_contract.vpcId
  }

  lifecycle {
    precondition {
      condition = (
        alltrue([for arn in local.protected_arns : split(":", arn)[4] == data.aws_caller_identity.current.account_id]) &&
        startswith(var.platform_contract.ecrRepositoryUrl, "${data.aws_caller_identity.current.account_id}.dkr.ecr.us-east-1.amazonaws.com/")
      )
      error_message = "Every contract ARN, role ARN, and the ECR repository must belong to the caller account."
    }

    precondition {
      condition = (
        alltrue([for subnet in data.aws_subnet.private_application : subnet.vpc_id == var.platform_contract.vpcId]) &&
        data.aws_security_group.authentication.vpc_id == var.platform_contract.vpcId &&
        data.aws_security_group.vpc_link.vpc_id == var.platform_contract.vpcId
      )
      error_message = "Private application subnets and ingress security groups must belong to the platform VPC."
    }

    precondition {
      condition = (
        data.aws_lb.ingress.internal &&
        data.aws_lb.ingress.vpc_id == var.platform_contract.vpcId &&
        (
          (var.ingress_contract.transport == "http" && lower(data.aws_lb.ingress.dns_name) == local.ingress_hostname) ||
          (var.ingress_contract.transport == "https" && lower(var.ingress_contract.tlsServerName) == local.ingress_hostname)
        )
      )
      error_message = "The ingress listener must identify an internal ALB in the platform VPC and the URL host must match its HTTP DNS name or HTTPS TLS server name."
    }
  }
}

resource "aws_cloudwatch_log_group" "customer_authentication" {
  name              = "/aws/lambda/${local.customer_authentication_name}"
  retention_in_days = 7
}

resource "aws_cloudwatch_log_group" "request_authorizer" {
  name              = "/aws/lambda/${local.request_authorizer_name}"
  retention_in_days = 7
}

resource "aws_lambda_function" "customer_authentication" {
  function_name    = local.customer_authentication_name
  description      = "Authenticates linked GarageFlow customers by CPF."
  filename         = var.package_path
  source_code_hash = filebase64sha256(var.package_path)
  role             = var.authentication_role_arn
  handler          = "GarageFlow.Serverless.Host::GarageFlow.Serverless.Host.CustomerAuthenticationFunction::FunctionHandler"
  runtime          = "dotnet10"
  architectures    = ["x86_64"]
  memory_size      = 512
  timeout          = 10
  publish          = true

  vpc_config {
    subnet_ids         = var.platform_contract.privateApplicationSubnetIds
    security_group_ids = [var.ingress_contract.authenticationSecurityGroupId]
  }

  environment {
    variables = {
      JWT_SECRET_ARN           = var.platform_contract.jwtSecretArn
      INTERNAL_AUTH_SECRET_ARN = var.platform_contract.internalAuthSecretArn
      INTERNAL_API_BASE_URL    = var.ingress_contract.internalApiBaseUrl
      INTERNAL_API_TRANSPORT   = var.ingress_contract.transport
      JWT_ISSUER               = "GarageFlow"
      JWT_AUDIENCE             = "GarageFlow.Adapters.Api"
    }
  }

  depends_on = [aws_cloudwatch_log_group.customer_authentication, terraform_data.deployment_guard]
}

resource "aws_lambda_function" "request_authorizer" {
  function_name    = local.request_authorizer_name
  description      = "Authorizes GarageFlow customer JWTs for HTTP API routes."
  filename         = var.package_path
  source_code_hash = filebase64sha256(var.package_path)
  role             = var.authorizer_role_arn
  handler          = "GarageFlow.Serverless.Host::GarageFlow.Serverless.Host.RequestAuthorizerFunction::FunctionHandler"
  runtime          = "dotnet10"
  architectures    = ["x86_64"]
  memory_size      = 256
  timeout          = 5
  publish          = true

  environment {
    variables = {
      JWT_SECRET_ARN = var.platform_contract.jwtSecretArn
      JWT_ISSUER     = "GarageFlow"
      JWT_AUDIENCE   = "GarageFlow.Adapters.Api"
    }
  }

  depends_on = [aws_cloudwatch_log_group.request_authorizer, terraform_data.deployment_guard]
}

resource "aws_lambda_alias" "customer_authentication" {
  name             = "live"
  description      = "Stable customer authentication deployment target."
  function_name    = aws_lambda_function.customer_authentication.function_name
  function_version = aws_lambda_function.customer_authentication.version
}

resource "aws_lambda_alias" "request_authorizer" {
  name             = "live"
  description      = "Stable request authorizer deployment target."
  function_name    = aws_lambda_function.request_authorizer.function_name
  function_version = aws_lambda_function.request_authorizer.version
}

resource "aws_lambda_permission" "customer_authentication" {
  statement_id   = "AllowApiGatewayCustomerAuthentication"
  action         = "lambda:InvokeFunction"
  function_name  = aws_lambda_function.customer_authentication.function_name
  qualifier      = aws_lambda_alias.customer_authentication.name
  principal      = "apigateway.amazonaws.com"
  source_account = data.aws_caller_identity.current.account_id
  source_arn     = "${var.platform_contract.apiGatewayExecutionArn}/*/POST/auth/customers/token"
}

resource "aws_lambda_permission" "request_authorizer" {
  statement_id   = "AllowApiGatewayRequestAuthorizer"
  action         = "lambda:InvokeFunction"
  function_name  = aws_lambda_function.request_authorizer.function_name
  qualifier      = aws_lambda_alias.request_authorizer.name
  principal      = "apigateway.amazonaws.com"
  source_account = data.aws_caller_identity.current.account_id
  source_arn     = "${var.platform_contract.apiGatewayExecutionArn}/authorizers/*"
}
