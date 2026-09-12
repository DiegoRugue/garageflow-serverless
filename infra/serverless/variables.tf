variable "environment" {
  description = "GarageFlow deployment environment."
  type        = string

  validation {
    condition     = contains(["homologation", "production"], var.environment)
    error_message = "environment must be homologation or production."
  }
}

variable "owner" {
  description = "Owner tag applied to managed resources."
  type        = string

  validation {
    condition     = can(regex("^[A-Za-z0-9][A-Za-z0-9 _.:/@+=,-]{0,127}$", var.owner))
    error_message = "owner must be a clean non-empty AWS tag value."
  }
}

variable "expires_on" {
  description = "Expiry tag in YYYY-MM-DD form."
  type        = string

  validation {
    condition     = can(regex("^[0-9]{4}-(0[1-9]|1[0-2])-([0-2][0-9]|3[01])$", var.expires_on))
    error_message = "expires_on must use YYYY-MM-DD."
  }
}

variable "platform_contract" {
  description = "Complete validated platform contract v1 outputs."
  type = object({
    awsRegion                   = string
    vpcId                       = string
    publicSubnetIds             = list(string)
    privateApplicationSubnetIds = list(string)
    databaseSubnetIds           = list(string)
    clusterName                 = string
    clusterSecurityGroupId      = string
    ecrRepositoryUrl            = string
    apiGatewayId                = string
    apiGatewayExecutionArn      = string
    jwtSecretArn                = string
    internalAuthSecretArn       = string
    bootstrapSecretArn          = string
    webhookSecretArn            = string
    snsTopicArn                 = string
  })

  validation {
    condition = (
      var.platform_contract.awsRegion == "us-east-1" &&
      can(regex("^vpc-[0-9a-f]{8}([0-9a-f]{9})?$", var.platform_contract.vpcId)) &&
      length(var.platform_contract.privateApplicationSubnetIds) > 0 &&
      length(distinct(var.platform_contract.privateApplicationSubnetIds)) == length(var.platform_contract.privateApplicationSubnetIds)
    )
    error_message = "platform_contract must target us-east-1 and provide a VPC with unique private application subnets."
  }
}

variable "ingress_contract" {
  description = "Complete validated ingress contract v2 outputs."
  type = object({
    listenerArn                   = string
    internalApiBaseUrl            = string
    transport                     = string
    authenticationSecurityGroupId = string
    vpcLinkSecurityGroupId        = string
    tlsServerName                 = optional(string)
  })

  validation {
    condition = (
      contains(["http", "https"], var.ingress_contract.transport) &&
      startswith(lower(var.ingress_contract.internalApiBaseUrl), "${var.ingress_contract.transport}://") &&
      can(regex("^sg-[0-9a-f]{8}([0-9a-f]{9})?$", var.ingress_contract.authenticationSecurityGroupId)) &&
      can(regex("^sg-[0-9a-f]{8}([0-9a-f]{9})?$", var.ingress_contract.vpcLinkSecurityGroupId)) &&
      (
        (var.ingress_contract.transport == "http" && var.ingress_contract.tlsServerName == null) ||
        (var.ingress_contract.transport == "https" && var.ingress_contract.tlsServerName != null)
      )
    )
    error_message = "ingress_contract must use a matching HTTP/HTTPS URL, valid security groups, and TLS server name only for HTTPS."
  }
}

variable "authentication_role_arn" {
  description = "Pre-existing IAM execution role ARN for customer authentication."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:iam::[0-9]{12}:role/[A-Za-z0-9+=,.@_/-]{1,512}$", var.authentication_role_arn))
    error_message = "authentication_role_arn must identify a pre-existing IAM role."
  }
}

variable "authorizer_role_arn" {
  description = "Pre-existing IAM execution role ARN for the request authorizer."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:iam::[0-9]{12}:role/[A-Za-z0-9+=,.@_/-]{1,512}$", var.authorizer_role_arn))
    error_message = "authorizer_role_arn must identify a pre-existing IAM role."
  }
}

variable "package_path" {
  description = "External framework-dependent linux-x64 Lambda ZIP package."
  type        = string

  validation {
    condition     = fileexists(var.package_path)
    error_message = "package_path must identify an existing package file."
  }
}
