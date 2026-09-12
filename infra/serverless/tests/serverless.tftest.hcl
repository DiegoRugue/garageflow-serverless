mock_provider "aws" {
  mock_data "aws_caller_identity" {
    defaults = {
      account_id = "123456789012"
      arn        = "arn:aws:iam::123456789012:user/terraform-test"
      user_id    = "AIDATEST"
    }
  }

  mock_data "aws_subnet" {
    defaults = {
      vpc_id = "vpc-0123456789abcdef0"
    }
  }

  mock_data "aws_security_group" {
    defaults = {
      vpc_id = "vpc-0123456789abcdef0"
    }
  }

  mock_data "aws_lb_listener" {
    defaults = {
      arn               = "arn:aws:elasticloadbalancing:us-east-1:123456789012:listener/app/garageflow/0123456789abcdef/0123456789abcdef"
      load_balancer_arn = "arn:aws:elasticloadbalancing:us-east-1:123456789012:loadbalancer/app/garageflow/0123456789abcdef"
    }
  }

  mock_data "aws_lb" {
    defaults = {
      arn      = "arn:aws:elasticloadbalancing:us-east-1:123456789012:loadbalancer/app/garageflow/0123456789abcdef"
      dns_name = "internal-garageflow-123.us-east-1.elb.amazonaws.com"
      internal = true
      vpc_id   = "vpc-0123456789abcdef0"
    }
  }
}

variables {
  environment = "homologation"
  owner       = "garageflow-team"
  expires_on  = "2026-12-31"
  platform_contract = {
    awsRegion                   = "us-east-1"
    vpcId                       = "vpc-0123456789abcdef0"
    publicSubnetIds             = ["subnet-0123456789abcdef0", "subnet-1123456789abcdef0"]
    privateApplicationSubnetIds = ["subnet-2123456789abcdef0", "subnet-3123456789abcdef0"]
    databaseSubnetIds           = ["subnet-4123456789abcdef0", "subnet-5123456789abcdef0"]
    clusterName                 = "garageflow-homologation"
    clusterSecurityGroupId      = "sg-0123456789abcdef0"
    ecrRepositoryUrl            = "123456789012.dkr.ecr.us-east-1.amazonaws.com/garageflow-homologation"
    apiGatewayId                = "a1b2c3d4e5"
    apiGatewayExecutionArn      = "arn:aws:execute-api:us-east-1:123456789012:a1b2c3d4e5"
    jwtSecretArn                = "arn:aws:secretsmanager:us-east-1:123456789012:secret:garageflow/jwt-AbCdEf"
    internalAuthSecretArn       = "arn:aws:secretsmanager:us-east-1:123456789012:secret:garageflow/internal-AbCdEf"
    bootstrapSecretArn          = "arn:aws:secretsmanager:us-east-1:123456789012:secret:garageflow/bootstrap-AbCdEf"
    webhookSecretArn            = "arn:aws:secretsmanager:us-east-1:123456789012:secret:garageflow/webhook-AbCdEf"
    snsTopicArn                 = "arn:aws:sns:us-east-1:123456789012:garageflow-homologation"
  }
  ingress_contract = {
    listenerArn                   = "arn:aws:elasticloadbalancing:us-east-1:123456789012:listener/app/garageflow/0123456789abcdef/0123456789abcdef"
    internalApiBaseUrl            = "http://internal-garageflow-123.us-east-1.elb.amazonaws.com"
    transport                     = "http"
    authenticationSecurityGroupId = "sg-1123456789abcdef0"
    vpcLinkSecurityGroupId        = "sg-2123456789abcdef0"
  }
  authentication_role_arn = "arn:aws:iam::123456789012:role/LabRole"
  authorizer_role_arn     = "arn:aws:iam::123456789012:role/LabRole"
  package_path            = "tests/fixtures/lambda-package.bin"
}

run "plans_exact_lambda_contract" {
  command = apply

  assert {
    condition = (
      aws_lambda_function.customer_authentication.runtime == "dotnet10" &&
      aws_lambda_function.customer_authentication.architectures[0] == "x86_64" &&
      aws_lambda_function.customer_authentication.handler == "GarageFlow.Serverless.Host::GarageFlow.Serverless.Host.CustomerAuthenticationFunction::FunctionHandler" &&
      aws_lambda_function.customer_authentication.timeout == 10 &&
      aws_lambda_function.customer_authentication.memory_size == 512 &&
      toset(aws_lambda_function.customer_authentication.vpc_config[0].subnet_ids) == toset(var.platform_contract.privateApplicationSubnetIds) &&
      length(aws_lambda_function.customer_authentication.vpc_config[0].security_group_ids) == 1 &&
      contains(aws_lambda_function.customer_authentication.vpc_config[0].security_group_ids, var.ingress_contract.authenticationSecurityGroupId)
    )
    error_message = "Customer authentication Lambda must use the approved runtime, handler, capacity, and private network."
  }

  assert {
    condition = tomap(aws_lambda_function.customer_authentication.environment[0].variables) == tomap({
      JWT_SECRET_ARN           = var.platform_contract.jwtSecretArn
      INTERNAL_AUTH_SECRET_ARN = var.platform_contract.internalAuthSecretArn
      INTERNAL_API_BASE_URL    = var.ingress_contract.internalApiBaseUrl
      INTERNAL_API_TRANSPORT   = var.ingress_contract.transport
      JWT_ISSUER               = "GarageFlow"
      JWT_AUDIENCE             = "GarageFlow.Adapters.Api"
    })
    error_message = "Customer authentication Lambda environment must contain only the approved ARN and endpoint settings."
  }

  assert {
    condition = (
      aws_lambda_function.request_authorizer.runtime == "dotnet10" &&
      aws_lambda_function.request_authorizer.architectures[0] == "x86_64" &&
      aws_lambda_function.request_authorizer.handler == "GarageFlow.Serverless.Host::GarageFlow.Serverless.Host.RequestAuthorizerFunction::FunctionHandler" &&
      aws_lambda_function.request_authorizer.timeout == 5 &&
      length(aws_lambda_function.request_authorizer.vpc_config) == 0 &&
      tomap(aws_lambda_function.request_authorizer.environment[0].variables) == tomap({
        JWT_SECRET_ARN = var.platform_contract.jwtSecretArn
        JWT_ISSUER     = "GarageFlow"
        JWT_AUDIENCE   = "GarageFlow.Adapters.Api"
      })
    )
    error_message = "Request authorizer must stay outside the VPC with only JWT settings."
  }

  assert {
    condition = (
      aws_lambda_alias.customer_authentication.name == "live" &&
      aws_lambda_alias.request_authorizer.name == "live" &&
      aws_lambda_permission.customer_authentication.qualifier == "live" &&
      aws_lambda_permission.customer_authentication.principal == "apigateway.amazonaws.com" &&
      aws_lambda_permission.customer_authentication.source_account == "123456789012" &&
      aws_lambda_permission.customer_authentication.source_arn == "arn:aws:execute-api:us-east-1:123456789012:a1b2c3d4e5/*/POST/auth/customers/token" &&
      aws_lambda_permission.request_authorizer.qualifier == "live" &&
      aws_lambda_permission.request_authorizer.source_arn == "arn:aws:execute-api:us-east-1:123456789012:a1b2c3d4e5/authorizers/*"
    )
    error_message = "API Gateway permissions must target the live aliases and exact source paths."
  }

  assert {
    condition = (
      aws_cloudwatch_log_group.customer_authentication.retention_in_days == 7 &&
      aws_cloudwatch_log_group.request_authorizer.retention_in_days == 7
    )
    error_message = "Both Lambda log groups must retain logs for seven days."
  }

  assert {
    condition = (
      output.customerAuthenticationAliasArn == aws_lambda_alias.customer_authentication.arn &&
      output.requestAuthorizerAliasArn == aws_lambda_alias.request_authorizer.arn
    )
    error_message = "The public Terraform outputs must expose only the live alias ARNs."
  }
}

run "plans_https_ingress_with_matching_tls_hostname" {
  command = plan

  variables {
    ingress_contract = {
      listenerArn                   = "arn:aws:elasticloadbalancing:us-east-1:123456789012:listener/app/garageflow/0123456789abcdef/0123456789abcdef"
      internalApiBaseUrl            = "https://api.internal.garageflow.example"
      transport                     = "https"
      authenticationSecurityGroupId = "sg-1123456789abcdef0"
      vpcLinkSecurityGroupId        = "sg-2123456789abcdef0"
      tlsServerName                 = "api.internal.garageflow.example"
    }
  }

  assert {
    condition     = aws_lambda_function.customer_authentication.environment[0].variables["INTERNAL_API_BASE_URL"] == "https://api.internal.garageflow.example"
    error_message = "HTTPS ingress must remain deployable when the URL host matches the validated TLS server name."
  }
}

run "rejects_cross_account_role" {
  command = plan

  variables {
    authentication_role_arn = "arn:aws:iam::999999999999:role/LabRole"
  }

  expect_failures = [terraform_data.deployment_guard]
}

run "plans_http_explicit_default_port" {
  command = plan
  variables {
    ingress_contract = {
      listenerArn                   = "arn:aws:elasticloadbalancing:us-east-1:123456789012:listener/app/garageflow/0123456789abcdef/0123456789abcdef"
      internalApiBaseUrl            = "http://internal-garageflow-123.us-east-1.elb.amazonaws.com:80/"
      transport                     = "http"
      authenticationSecurityGroupId = "sg-1123456789abcdef0"
      vpcLinkSecurityGroupId        = "sg-2123456789abcdef0"
    }
  }
  assert {
    condition     = local.ingress_hostname == "internal-garageflow-123.us-east-1.elb.amazonaws.com"
    error_message = "HTTP default port must not become part of the ALB hostname."
  }
}

run "plans_https_explicit_default_port" {
  command = plan
  variables {
    ingress_contract = {
      listenerArn                   = "arn:aws:elasticloadbalancing:us-east-1:123456789012:listener/app/garageflow/0123456789abcdef/0123456789abcdef"
      internalApiBaseUrl            = "https://api.internal.garageflow.example:443/"
      transport                     = "https"
      authenticationSecurityGroupId = "sg-1123456789abcdef0"
      vpcLinkSecurityGroupId        = "sg-2123456789abcdef0"
      tlsServerName                 = "api.internal.garageflow.example"
    }
  }
  assert {
    condition     = local.ingress_hostname == "api.internal.garageflow.example"
    error_message = "HTTPS default port must not become part of the TLS hostname."
  }
}

run "rejects_resources_outside_platform_vpc" {
  command = plan

  override_data {
    target = data.aws_security_group.authentication
    values = {
      vpc_id = "vpc-99999999999999999"
    }
  }

  expect_failures = [terraform_data.deployment_guard]
}

run "rejects_non_internal_or_wrong_hostname_alb" {
  command = plan

  override_data {
    target = data.aws_lb.ingress
    values = {
      dns_name = "unexpected.internal.example.com"
      internal = false
      vpc_id   = "vpc-0123456789abcdef0"
    }
  }

  expect_failures = [terraform_data.deployment_guard]
}
