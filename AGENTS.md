# GarageFlow Serverless

## Scope and architecture

Own only the customer CPF authentication Lambda, HTTP API REQUEST authorizer Lambda, their runtime, tests, Lambda infrastructure, aliases and invocation permissions. Platform owns the API Gateway, VPC, ingress and common secrets. Database and application own persistence and business rules.

Use C#/.NET 10 with Nullable enabled and one public type per file. Projects: Domain for CPF input invariant; Application for use cases and ports; Adapters.Infrastructure for HTTP, JWT and Secrets Manager; Host for Lambda handlers/composition only. Dependencies point inward. SDK/framework types stay in adapters/Host. No database dependencies or project references to another checkout. Mirror GarageFlow vertical slices where applicable.

Admin/staff login remains in the application. CPF login is only for linked customers. The API owns password hashing, customer status, permissions and OS ownership; Lambda never recreates those decisions. Fail closed, return generic authentication errors and never log CPF, credentials, secrets or JWTs.

## Delivery

Use Terraform 1.15.7 and AWS provider 6.49.0. Consume only validated metadata manifests, never another root's Terraform state. Accept pre-existing execution-role ARNs for Academy; do not create IAM roles or claim unavailable IAM restrictions are applied. Environments: develop -> homologation, main -> production, us-east-1. Secrets are resolved at runtime through Secrets Manager; only secret ARNs enter Lambda configuration/manifests.

Run strict solution build, all .NET tests, package publish, shared Python contract tests, Terraform fmt/validate/mock-provider tests, shell syntax and workflow validation before completion. Tests must not require AWS credentials. Relevant changed security behavior requires failing regression tests before implementation. Keep meaningful architecture, malformed-request, token compatibility and outbound HTTP tests.

All plans, reports, test outputs, logs, generated manifests/tfvars and work evidence stay outside every repository under C:/projects/GarageFlow-study/sdd/2026-09-11-m2-serverless-edge. Repository documentation is limited to README, ADR/RFC and delivery artifacts. Do not publish real credentials, state, generated packages or test records.
