import json
import os
import stat
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from scripts.tests.test_serverless_tfvars import ACCOUNT_ID, ingress_manifest, platform_manifest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "deploy-serverless.sh"
GIT_BASH = Path(
    os.environ.get(
        "GIT_BASH",
        r"C:\Program Files\Git\bin\bash.exe" if os.name == "nt" else "/usr/bin/bash",
    )
)
LOCKED_PYTHON = Path(os.environ.get("LOCKED_PYTHON", sys.executable))
SOURCE_COMMIT = "0123456789abcdef0123456789abcdef01234567"


class DeployServerlessTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.temp = Path(self.temporary.name)
        self.command_log = self.temp / "commands.log"
        self.platform_path = self.temp / "platform-fixture.json"
        self.ingress_path = self.temp / "ingress-fixture.json"
        self.platform_path.write_text(json.dumps(platform_manifest()), encoding="utf-8")
        self.ingress_path.write_text(json.dumps(ingress_manifest()), encoding="utf-8")
        self.fake_bin = self.temp / "fake-bin"
        self.fake_bin.mkdir()
        self.git = self.write_fake("git", self.git_fake())
        self.aws = self.write_fake("aws", self.aws_fake())
        self.terraform = self.write_fake("terraform", self.terraform_fake())
        self.dotnet = self.write_fake("dotnet", self.dotnet_fake())
        self.zip = self.write_fake("zip", self.zip_fake())

    def tearDown(self):
        self.temporary.cleanup()

    def write_fake(self, name, body):
        path = self.fake_bin / name
        path.write_text("#!/usr/bin/env bash\nset -euo pipefail\n" + body, encoding="utf-8", newline="\n")
        path.chmod(path.stat().st_mode | stat.S_IEXEC)
        return path

    def git_fake(self):
        return r'''
if [[ "$*" == "branch --show-current" ]]; then
  printf '%s\n' "${FAKE_BRANCH:-develop}"
elif [[ "$*" == "rev-parse HEAD" ]]; then
  printf '%s\n' "${FAKE_SHA}"
else
  printf 'unexpected git arguments: %s\n' "$*" >&2
  exit 91
fi
'''

    def aws_fake(self):
        return r'''
printf 'aws %s\n' "$*" >> "$COMMAND_LOG"
case "$1 $2" in
  "sts get-caller-identity")
    printf '%s\n' "${FAKE_CALLER_ACCOUNT:-123456789012}"
    ;;
  "s3 cp")
    if [[ "$3" == *"/contracts/v1/homologation/platform.json" ]]; then
      cp "$PLATFORM_FIXTURE" "$4"
    elif [[ "$3" == *"/contracts/v2/homologation/ingress.json" ]]; then
      cp "$INGRESS_FIXTURE" "$4"
    fi
    ;;
  "iam get-role")
    printf '%s\n' "arn:aws:iam::123456789012:role/LabRole"
    ;;
  "ec2 describe-subnets")
    if [[ "${FAIL_NETWORK_MEMBERSHIP:-0}" == "1" ]]; then
      printf '%s\n' "vpc-99999999999999999 vpc-99999999999999999"
    else
      printf '%s\n' "vpc-0123456789abcdef0 vpc-0123456789abcdef0"
    fi
    ;;
  "ec2 describe-security-groups")
    printf '%s\n' "vpc-0123456789abcdef0 vpc-0123456789abcdef0"
    ;;
  "elbv2 describe-listeners")
    printf '%s\n' "arn:aws:elasticloadbalancing:us-east-1:123456789012:loadbalancer/app/garageflow/0123456789abcdef"
    ;;
  "elbv2 describe-load-balancers")
    if [[ "$*" == *"VpcId"* ]]; then
      printf '%s\n' "vpc-0123456789abcdef0"
    elif [[ "$*" == *"Scheme"* ]]; then
      printf '%s\n' "internal"
    elif [[ "$*" == *"DNSName"* ]]; then
      printf '%s\n' "internal-garageflow-123.us-east-1.elb.amazonaws.com"
    else
      exit 92
    fi
    ;;
  "lambda wait")
    ;;
  "s3api put-object")
    ;;
  *)
    printf 'unexpected aws arguments: %s\n' "$*" >&2
    exit 93
    ;;
esac
'''

    def terraform_fake(self):
        return r'''
printf 'terraform %s\n' "$*" >> "$COMMAND_LOG"
if [[ "$*" == *" plan "* || "$*" == *" plan" ]]; then
  [[ "${FAIL_TERRAFORM_PLAN:-0}" != "1" ]] || exit 41
  for argument in "$@"; do
    if [[ "$argument" == -out=* ]]; then
      : > "${argument#-out=}"
    fi
  done
elif [[ "$*" == *" apply "* || "$*" == *" apply" ]]; then
  [[ "${FAIL_TERRAFORM_APPLY:-0}" != "1" ]] || exit 42
elif [[ "$*" == *" output -json"* ]]; then
  printf '%s\n' '{"customerAuthenticationAliasArn":{"sensitive":false,"type":"string","value":"arn:aws:lambda:us-east-1:123456789012:function:garageflow-homologation-customer-authentication:live"},"requestAuthorizerAliasArn":{"sensitive":false,"type":"string","value":"arn:aws:lambda:us-east-1:123456789012:function:garageflow-homologation-request-authorizer:live"}}'
fi
'''

    def dotnet_fake(self):
        return r'''
printf 'dotnet %s\n' "$*" >> "$COMMAND_LOG"
output=""
previous=""
for argument in "$@"; do
  if [[ "$previous" == "--output" ]]; then output="$argument"; fi
  previous="$argument"
done
mkdir -p "$output"
printf 'synthetic assembly' > "$output/GarageFlow.Serverless.Host.dll"
'''

    def zip_fake(self):
        return r'''
printf 'zip %s\n' "$*" >> "$COMMAND_LOG"
for argument in "$@"; do
  if [[ "$argument" == *.zip ]]; then
    printf 'synthetic zip' > "$argument"
    exit 0
  fi
done
exit 94
'''

    def environment(self, **overrides):
        environment = os.environ.copy()
        environment.update(
            {
                "RUNNER_TEMP": self.temp.as_posix(),
                "COMMAND_LOG": self.command_log.as_posix(),
                "PLATFORM_FIXTURE": self.platform_path.as_posix(),
                "INGRESS_FIXTURE": self.ingress_path.as_posix(),
                "FAKE_SHA": SOURCE_COMMIT,
                "GITHUB_REF": "refs/heads/develop",
                "GITHUB_REF_NAME": "develop",
                "GITHUB_SHA": SOURCE_COMMIT,
                "GITHUB_RUN_ID": "3141592653",
                "GITHUB_RUN_ATTEMPT": "2",
                "AWS_REGION": "us-east-1",
                "AWS_ACCOUNT_ID": ACCOUNT_ID,
                "AWS_ACCESS_KEY_ID": "synthetic-access-key",
                "AWS_SECRET_ACCESS_KEY": "synthetic-secret-key",
                "AWS_SESSION_TOKEN": "synthetic-session-token",
                "TF_STATE_BUCKET": "garageflow-terraform-state",
                "TF_OWNER": "garageflow-team",
                "TF_EXPIRES_ON": "2026-12-31",
                "AUTHENTICATION_ROLE_ARN": f"arn:aws:iam::{ACCOUNT_ID}:role/LabRole",
                "AUTHORIZER_ROLE_ARN": f"arn:aws:iam::{ACCOUNT_ID}:role/LabRole",
                "PYTHON_BIN": LOCKED_PYTHON.as_posix(),
                "GIT_BIN": self.git.as_posix(),
                "AWS_BIN": self.aws.as_posix(),
                "TERRAFORM_BIN": self.terraform.as_posix(),
                "DOTNET_BIN": self.dotnet.as_posix(),
                "ZIP_BIN": self.zip.as_posix(),
                "PYTHONDONTWRITEBYTECODE": "1",
            }
        )
        environment.update(overrides)
        return environment

    def run_script(self, **overrides):
        return subprocess.run(
            [str(GIT_BASH), SCRIPT.as_posix(), "--environment", "homologation"],
            cwd=ROOT,
            env=self.environment(**overrides),
            capture_output=True,
            text=True,
            timeout=30,
        )

    def logged_commands(self):
        if not self.command_log.exists():
            return []
        return self.command_log.read_text(encoding="utf-8").splitlines()

    def test_success_publishes_immutable_revision_before_stable_contract(self):
        result = self.run_script()
        commands = self.logged_commands()

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue(any("contracts/v1/homologation/platform.json" in line for line in commands))
        self.assertTrue(any("contracts/v2/homologation/ingress.json" in line for line in commands))
        plan_index = next(i for i, line in enumerate(commands) if "terraform " in line and " plan " in line)
        apply_index = next(i for i, line in enumerate(commands) if "terraform " in line and " apply " in line)
        wait_index = next(i for i, line in enumerate(commands) if "aws lambda wait function-active-v2" in line)
        revision_index = next(i for i, line in enumerate(commands) if "aws s3api put-object" in line)
        stable_index = next(
            i
            for i, line in enumerate(commands)
            if "aws s3 cp" in line and "/contracts/v1/homologation/serverless.json" in line
        )
        self.assertLess(plan_index, apply_index)
        self.assertLess(apply_index, wait_index)
        self.assertLess(wait_index, revision_index)
        self.assertLess(revision_index, stable_index)
        self.assertIn(
            f"serverless/revisions/{SOURCE_COMMIT}/3141592653-2.json",
            commands[revision_index],
        )
        self.assertIn("--if-none-match *", commands[revision_index])
        self.assertFalse(any(self.temp.glob("garageflow-serverless.*")))

    def test_apply_failure_never_waits_or_publishes_metadata(self):
        result = self.run_script(FAIL_TERRAFORM_APPLY="1")
        commands = self.logged_commands()

        self.assertNotEqual(0, result.returncode)
        self.assertTrue(any("terraform " in line and " apply " in line for line in commands))
        self.assertFalse(any("aws lambda wait" in line for line in commands))
        self.assertFalse(any("aws s3api put-object" in line for line in commands))
        self.assertFalse(any("serverless.json" in line and "aws s3 cp" in line for line in commands))

    def test_https_ingress_accepts_url_host_matching_tls_server_name(self):
        ingress = ingress_manifest("https")
        ingress["outputs"]["internalApiBaseUrl"] = "https://api.internal.garageflow.example"
        self.ingress_path.write_text(json.dumps(ingress), encoding="utf-8")

        result = self.run_script()

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue(any("aws s3api put-object" in line for line in self.logged_commands()))

    def test_plan_or_discovery_failure_never_applies_or_publishes(self):
        for failure in ({"FAIL_TERRAFORM_PLAN": "1"}, {"FAIL_NETWORK_MEMBERSHIP": "1"}):
            with self.subTest(failure=next(iter(failure))):
                self.command_log.unlink(missing_ok=True)
                result = self.run_script(**failure)
                commands = self.logged_commands()

                self.assertNotEqual(0, result.returncode)
                self.assertFalse(any("terraform " in line and " apply " in line for line in commands))
                self.assertFalse(any("aws s3api put-object" in line for line in commands))
                self.assertFalse(any("serverless.json" in line and "aws s3 cp" in line for line in commands))

    def test_unprotected_ref_fails_before_any_aws_command(self):
        result = self.run_script(
            GITHUB_REF="refs/heads/feature/unsafe",
            GITHUB_REF_NAME="feature/unsafe",
            FAKE_BRANCH="feature/unsafe",
        )

        self.assertNotEqual(0, result.returncode)
        self.assertEqual([], self.logged_commands())
        self.assertIn("protected branch", result.stderr)

    def test_non_numeric_run_identity_fails_before_any_aws_command(self):
        for overrides, error_fragment in (
            ({"GITHUB_RUN_ID": ""}, "required"),
            ({"GITHUB_RUN_ID": "run-3141592653"}, "numeric"),
            ({"GITHUB_RUN_ATTEMPT": "attempt-2"}, "numeric"),
        ):
            with self.subTest(field=next(iter(overrides))):
                self.command_log.unlink(missing_ok=True)
                result = self.run_script(**overrides)

                self.assertNotEqual(0, result.returncode)
                self.assertEqual([], self.logged_commands())
                self.assertIn(error_fragment, result.stderr)


if __name__ == "__main__":
    unittest.main()
