#!/usr/bin/env bash
set -Eeuo pipefail

fail() {
  printf 'deploy-serverless: %s\n' "$1" >&2
  exit 2
}

require_environment_value() {
  local name="$1"
  [[ -n "${!name:-}" ]] || fail "required environment variable ${name} is missing"
}

[[ "$#" -eq 2 && "$1" == "--environment" ]] || fail "usage: deploy-serverless.sh --environment <homologation|production>"
deployment_environment="$2"

for required_name in \
  RUNNER_TEMP GITHUB_REF GITHUB_REF_NAME GITHUB_SHA GITHUB_RUN_ID GITHUB_RUN_ATTEMPT \
  AWS_REGION AWS_ACCOUNT_ID \
  AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY AWS_SESSION_TOKEN TF_STATE_BUCKET \
  TF_OWNER TF_EXPIRES_ON AUTHENTICATION_ROLE_ARN AUTHORIZER_ROLE_ARN; do
  require_environment_value "$required_name"
done

python_bin="${PYTHON_BIN:-python}"
git_bin="${GIT_BIN:-git}"
aws_bin="${AWS_BIN:-aws}"
terraform_bin="${TERRAFORM_BIN:-terraform}"
dotnet_bin="${DOTNET_BIN:-dotnet}"
zip_bin="${ZIP_BIN:-zip}"

case "$GITHUB_REF_NAME" in
  develop)
    expected_environment="homologation"
    ;;
  main)
    expected_environment="production"
    ;;
  *)
    fail "deployment requires the protected branch main or develop"
    ;;
esac

[[ "$deployment_environment" == "$expected_environment" ]] || fail "environment does not match the protected branch"
[[ "$GITHUB_REF" == "refs/heads/${GITHUB_REF_NAME}" ]] || fail "ref does not identify the protected branch"
[[ "$AWS_REGION" == "us-east-1" ]] || fail "AWS_REGION must be us-east-1"
[[ "$AWS_ACCOUNT_ID" =~ ^[0-9]{12}$ ]] || fail "AWS_ACCOUNT_ID must contain exactly 12 digits"
[[ "$GITHUB_SHA" =~ ^[0-9a-f]{40}$ ]] || fail "GITHUB_SHA must be a full lowercase Git SHA"
[[ "$GITHUB_RUN_ID" =~ ^[0-9]+$ ]] || fail "GITHUB_RUN_ID must be numeric"
[[ "$GITHUB_RUN_ATTEMPT" =~ ^[0-9]+$ ]] || fail "GITHUB_RUN_ATTEMPT must be numeric"

actual_branch="$("$git_bin" branch --show-current)"
actual_commit="$("$git_bin" rev-parse HEAD)"
[[ "$actual_branch" == "$GITHUB_REF_NAME" ]] || fail "checked-out branch does not match the protected ref"
[[ "$actual_commit" == "$GITHUB_SHA" ]] || fail "checked-out commit does not match GITHUB_SHA"

caller_account="$("$aws_bin" sts get-caller-identity --query Account --output text)"
[[ "$caller_account" == "$AWS_ACCOUNT_ID" ]] || fail "AWS caller does not match the protected account"

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
terraform_root="${repository_root}/infra/serverless"
work_directory="$(mktemp -d "${RUNNER_TEMP%/}/garageflow-serverless.XXXXXX")"

cleanup() {
  "$python_bin" - "$RUNNER_TEMP" "$work_directory" <<'PY' >/dev/null 2>&1 || true
import shutil
import sys
from pathlib import Path

trusted = Path(sys.argv[1]).resolve(strict=True)
target = Path(sys.argv[2]).resolve(strict=True)
target.relative_to(trusted)
if target.parent != trusted or not target.name.startswith("garageflow-serverless."):
    raise SystemExit(1)
shutil.rmtree(target)
PY
}
trap cleanup EXIT

platform_contract="${work_directory}/platform.json"
ingress_contract="${work_directory}/ingress.json"
publish_directory="${work_directory}/publish"
package_path="${work_directory}/garageflow-serverless.zip"
tfvars_path="${work_directory}/serverless.auto.tfvars.json"
plan_path="${work_directory}/serverless.tfplan"
contract_values_path="${work_directory}/deployment-values.txt"
terraform_raw_outputs="${work_directory}/terraform-outputs.raw.json"
terraform_outputs="${work_directory}/terraform-outputs.json"
serverless_contract="${work_directory}/serverless.json"

"$aws_bin" s3 cp \
  "s3://${TF_STATE_BUCKET}/contracts/v1/${deployment_environment}/platform.json" \
  "$platform_contract" --only-show-errors
"$aws_bin" s3 cp \
  "s3://${TF_STATE_BUCKET}/contracts/v2/${deployment_environment}/ingress.json" \
  "$ingress_contract" --only-show-errors

"$python_bin" "${repository_root}/scripts/infra_contract.py" validate \
  --file "$platform_contract" --producer platform --environment "$deployment_environment" \
  --schema-version 1.0 >/dev/null
"$python_bin" "${repository_root}/scripts/infra_contract.py" validate \
  --file "$ingress_contract" --producer ingress --environment "$deployment_environment" \
  --schema-version 2.0 >/dev/null

mkdir -p "$publish_directory"
"$dotnet_bin" publish "${repository_root}/Host/GarageFlow.Serverless.Host.csproj" \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained false \
  --output "$publish_directory" \
  -p:ContinuousIntegrationBuild=true
(
  cd "$publish_directory"
  "$zip_bin" -q -r "$package_path" .
)

"$python_bin" "${repository_root}/scripts/serverless_tfvars.py" \
  --platform-contract "$platform_contract" \
  --ingress-contract "$ingress_contract" \
  --output "$tfvars_path" \
  --environment "$deployment_environment" \
  --aws-account-id "$AWS_ACCOUNT_ID" \
  --owner "$TF_OWNER" \
  --expires-on "$TF_EXPIRES_ON" \
  --authentication-role-arn "$AUTHENTICATION_ROLE_ARN" \
  --authorizer-role-arn "$AUTHORIZER_ROLE_ARN" \
  --package-path "$package_path"

"$python_bin" - "$tfvars_path" > "$contract_values_path" <<'PY'
import json
import sys
from pathlib import Path
from urllib.parse import urlsplit

sys.stdout.reconfigure(newline="\n")
document = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
platform = document["platform_contract"]
ingress = document["ingress_contract"]
print(platform["vpcId"])
print(" ".join(platform["privateApplicationSubnetIds"]))
print(ingress["authenticationSecurityGroupId"])
print(ingress["vpcLinkSecurityGroupId"])
print(ingress["listenerArn"])
print(urlsplit(ingress["internalApiBaseUrl"]).hostname)
print(ingress["transport"])
print(ingress.get("tlsServerName") or "")
PY
mapfile -t contract_values < "$contract_values_path"
[[ "${#contract_values[@]}" -eq 8 ]] || fail "validated contracts did not yield required deployment values"
vpc_id="${contract_values[0]}"
read -r -a private_subnet_ids <<< "${contract_values[1]}"
authentication_security_group_id="${contract_values[2]}"
vpc_link_security_group_id="${contract_values[3]}"
listener_arn="${contract_values[4]}"
internal_api_hostname="${contract_values[5]}"
ingress_transport="${contract_values[6]}"
tls_server_name="${contract_values[7]}"

for role_arn in "$AUTHENTICATION_ROLE_ARN" "$AUTHORIZER_ROLE_ARN"; do
  role_name="${role_arn##*/}"
  actual_role_arn="$("$aws_bin" iam get-role --role-name "$role_name" --query Role.Arn --output text)"
  [[ "$actual_role_arn" == "$role_arn" ]] || fail "configured execution role does not match the protected account role"
done

read -r -a actual_subnet_vpcs <<< "$("$aws_bin" ec2 describe-subnets \
  --subnet-ids "${private_subnet_ids[@]}" --query 'Subnets[].VpcId' --output text)"
[[ "${#actual_subnet_vpcs[@]}" -eq "${#private_subnet_ids[@]}" ]] || fail "not all private application subnets were resolved"
for subnet_vpc in "${actual_subnet_vpcs[@]}"; do
  [[ "$subnet_vpc" == "$vpc_id" ]] || fail "private application subnet is outside the platform VPC"
done

read -r -a actual_security_group_vpcs <<< "$("$aws_bin" ec2 describe-security-groups \
  --group-ids "$authentication_security_group_id" "$vpc_link_security_group_id" \
  --query 'SecurityGroups[].VpcId' --output text)"
[[ "${#actual_security_group_vpcs[@]}" -eq 2 ]] || fail "ingress security groups were not resolved"
for security_group_vpc in "${actual_security_group_vpcs[@]}"; do
  [[ "$security_group_vpc" == "$vpc_id" ]] || fail "ingress security group is outside the platform VPC"
done

load_balancer_arn="$("$aws_bin" elbv2 describe-listeners --listener-arns "$listener_arn" \
  --query 'Listeners[0].LoadBalancerArn' --output text)"
load_balancer_vpc="$("$aws_bin" elbv2 describe-load-balancers --load-balancer-arns "$load_balancer_arn" \
  --query 'LoadBalancers[0].VpcId' --output text)"
load_balancer_scheme="$("$aws_bin" elbv2 describe-load-balancers --load-balancer-arns "$load_balancer_arn" \
  --query 'LoadBalancers[0].Scheme' --output text)"
load_balancer_hostname="$("$aws_bin" elbv2 describe-load-balancers --load-balancer-arns "$load_balancer_arn" \
  --query 'LoadBalancers[0].DNSName' --output text)"
[[ "$load_balancer_vpc" == "$vpc_id" ]] || fail "ingress listener load balancer is outside the platform VPC"
[[ "$load_balancer_scheme" == "internal" ]] || fail "ingress listener must belong to an internal load balancer"
if [[ "$ingress_transport" == "http" ]]; then
  [[ "${load_balancer_hostname,,}" == "${internal_api_hostname,,}" ]] || fail "HTTP internal API hostname does not match the ingress load balancer"
else
  [[ "${tls_server_name,,}" == "${internal_api_hostname,,}" ]] || fail "HTTPS internal API hostname does not match the ingress TLS server name"
fi

"$terraform_bin" -chdir="$terraform_root" init -input=false -reconfigure \
  -backend-config="bucket=${TF_STATE_BUCKET}" \
  -backend-config="key=phase3/${deployment_environment}/serverless.tfstate" \
  -backend-config="region=${AWS_REGION}" \
  -backend-config="encrypt=true" \
  -backend-config="use_lockfile=true"
"$terraform_bin" -chdir="$terraform_root" validate
"$terraform_bin" -chdir="$terraform_root" plan -input=false -lock-timeout=5m \
  -var-file="$tfvars_path" -out="$plan_path"
"$terraform_bin" -chdir="$terraform_root" apply -input=false -auto-approve -lock-timeout=5m "$plan_path"

"$terraform_bin" -chdir="$terraform_root" output -json > "$terraform_raw_outputs"
"$python_bin" - "$terraform_raw_outputs" "$terraform_outputs" <<'PY'
import json
import sys
from pathlib import Path

source = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
expected = {"customerAuthenticationAliasArn", "requestAuthorizerAliasArn"}
if set(source) != expected or any(set(source[name]) < {"value"} for name in expected):
    raise SystemExit("unexpected Terraform serverless outputs")
values = {name: source[name]["value"] for name in sorted(expected)}
Path(sys.argv[2]).write_text(json.dumps(values, sort_keys=True) + "\n", encoding="utf-8")
PY

mapfile -t alias_arns < <("$python_bin" - "$terraform_outputs" <<'PY'
import json
import sys
from pathlib import Path

sys.stdout.reconfigure(newline="\n")
outputs = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
print(outputs["customerAuthenticationAliasArn"])
print(outputs["requestAuthorizerAliasArn"])
PY
)
for alias_arn in "${alias_arns[@]}"; do
  "$aws_bin" lambda wait function-active-v2 --function-name "${alias_arn%:live}"
done

"$python_bin" "${repository_root}/scripts/infra_contract.py" publish \
  --input "$terraform_outputs" \
  --output "$serverless_contract" \
  --producer serverless \
  --environment "$deployment_environment" \
  --source-commit "$GITHUB_SHA" \
  --schema-version 1.0

"$aws_bin" s3api put-object \
  --bucket "$TF_STATE_BUCKET" \
  --key "contracts/v1/${deployment_environment}/serverless/revisions/${GITHUB_SHA}/${GITHUB_RUN_ID}-${GITHUB_RUN_ATTEMPT}.json" \
  --body "$serverless_contract" \
  --content-type application/json \
  --if-none-match '*' >/dev/null
"$aws_bin" s3 cp "$serverless_contract" \
  "s3://${TF_STATE_BUCKET}/contracts/v1/${deployment_environment}/serverless.json" \
  --content-type application/json --only-show-errors

printf 'Serverless deployment completed for %s at %s.\n' "$deployment_environment" "$GITHUB_SHA"
