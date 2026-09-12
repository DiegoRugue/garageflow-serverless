# GarageFlow Serverless

## Scope and architecture

Own only the customer CPF authentication Lambda, HTTP API REQUEST authorizer Lambda, their runtime, tests, Lambda infrastructure, aliases and invocation permissions. Platform owns the API Gateway, VPC, ingress and common secrets. Database and application own persistence and business rules.

Use C#/.NET 10 with Nullable enabled and one public type per file. Projects: Domain for CPF input invariant; Application for use cases and ports; Adapters.Infrastructure for HTTP, JWT and Secrets Manager; Host for Lambda handlers/composition only. Dependencies point inward. SDK/framework types stay in adapters/Host. No database dependencies or project references to another checkout. Mirror GarageFlow vertical slices where applicable.

Admin/staff login remains in the application. CPF login is only for linked customers. The API owns password hashing, customer status, permissions and OS ownership; Lambda never recreates those decisions. Fail closed, return generic authentication errors and never log CPF, credentials, secrets or JWTs.

## Delivery

Use Terraform 1.15.7 and AWS provider 6.49.0. Consume only validated metadata manifests, never another root's Terraform state. Accept pre-existing execution-role ARNs for Academy; do not create IAM roles or claim unavailable IAM restrictions are applied. Environments: develop -> homologation, main -> production, us-east-1. Secrets are resolved at runtime through Secrets Manager; only secret ARNs enter Lambda configuration/manifests.

Run strict solution build, all .NET tests, package publish, shared Python contract tests with at least 80% branch-aware coverage, Terraform fmt/init-without-backend/validate/mock-provider tests, shell syntax and workflow validation before completion. Enforce at least 80% meaningful .NET line coverage with `coverlet.runsettings`. Use `C:/projects/GarageFlow-study/tools/locked-python/Scripts/python.exe`, `C:/Program Files/Git/bin/bash.exe`, and `C:/projects/GarageFlow-study/tools/actionlint-1.7.12/actionlint.exe` for local verification. Tests must not require AWS credentials. Relevant changed security behavior requires failing regression tests before implementation. Keep meaningful architecture, malformed-request, token compatibility and outbound HTTP tests.

Serverless deployment must remain ordered: validate protected ref, SHA, region, caller account and complete versioned contracts; verify the referenced AWS network and IAM identities; build and package in `RUNNER_TEMP`; apply the saved Terraform plan; wait for both Lambda functions; publish the immutable serverless v1 revision before its stable contract; then invoke the protected platform edge workflow. Never read secret values, create IAM resources, place generated tfvars/packages in the checkout, or run AWS commands during pull-request quality checks.

All plans, reports, test outputs, logs, generated manifests/tfvars and work evidence stay outside every repository under C:/projects/GarageFlow-study/sdd/2026-09-11-m2-serverless-edge. Repository documentation is limited to README, ADR/RFC and delivery artifacts. Do not publish real credentials, state, generated packages or test records.
