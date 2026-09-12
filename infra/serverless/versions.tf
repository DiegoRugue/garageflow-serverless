terraform {
  required_version = "= 1.15.7"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "= 6.49.0"
    }
  }

  backend "s3" {}
}

provider "aws" {
  region = "us-east-1"

  default_tags {
    tags = {
      Environment = var.environment
      Owner       = var.owner
      ExpiresOn   = var.expires_on
      ManagedBy   = "Terraform"
      Project     = "GarageFlow"
    }
  }
}
