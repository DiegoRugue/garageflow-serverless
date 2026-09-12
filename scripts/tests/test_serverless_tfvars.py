import importlib.util
import json
import os
import tempfile
import unittest
from contextlib import contextmanager
from pathlib import Path
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "scripts" / "serverless_tfvars.py"
SOURCE_COMMIT = "0123456789abcdef0123456789abcdef01234567"
ACCOUNT_ID = "123456789012"


@contextmanager
def trusted_temp_directory():
    with tempfile.TemporaryDirectory() as directory:
        with patch.dict(os.environ, {"RUNNER_TEMP": directory}):
            yield Path(directory)


def load_module():
    if not MODULE_PATH.is_file():
        raise AssertionError("serverless Terraform input tooling is missing")
    spec = importlib.util.spec_from_file_location("serverless_tfvars", MODULE_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def platform_manifest():
    return {
        "schemaVersion": "1.0",
        "environment": "homologation",
        "producer": "platform",
        "sourceCommit": SOURCE_COMMIT,
        "publishedAt": "2026-09-11T14:30:00Z",
        "outputs": {
            "awsRegion": "us-east-1",
            "vpcId": "vpc-0123456789abcdef0",
            "publicSubnetIds": ["subnet-0123456789abcdef0", "subnet-1123456789abcdef0"],
            "privateApplicationSubnetIds": [
                "subnet-2123456789abcdef0",
                "subnet-3123456789abcdef0",
            ],
            "databaseSubnetIds": ["subnet-4123456789abcdef0", "subnet-5123456789abcdef0"],
            "clusterName": "garageflow-homologation",
            "clusterSecurityGroupId": "sg-0123456789abcdef0",
            "ecrRepositoryUrl": f"{ACCOUNT_ID}.dkr.ecr.us-east-1.amazonaws.com/garageflow-homologation",
            "apiGatewayId": "a1b2c3d4e5",
            "apiGatewayExecutionArn": f"arn:aws:execute-api:us-east-1:{ACCOUNT_ID}:a1b2c3d4e5",
            "jwtSecretArn": f"arn:aws:secretsmanager:us-east-1:{ACCOUNT_ID}:secret:garageflow/jwt-AbCdEf",
            "internalAuthSecretArn": f"arn:aws:secretsmanager:us-east-1:{ACCOUNT_ID}:secret:garageflow/internal-AbCdEf",
            "bootstrapSecretArn": f"arn:aws:secretsmanager:us-east-1:{ACCOUNT_ID}:secret:garageflow/bootstrap-AbCdEf",
            "webhookSecretArn": f"arn:aws:secretsmanager:us-east-1:{ACCOUNT_ID}:secret:garageflow/webhook-AbCdEf",
            "snsTopicArn": f"arn:aws:sns:us-east-1:{ACCOUNT_ID}:garageflow-homologation",
        },
    }


def ingress_manifest(transport="http"):
    outputs = {
        "listenerArn": (
            f"arn:aws:elasticloadbalancing:us-east-1:{ACCOUNT_ID}:"
            "listener/app/garageflow/0123456789abcdef/0123456789abcdef"
        ),
        "internalApiBaseUrl": f"{transport}://internal-garageflow-123.us-east-1.elb.amazonaws.com",
        "transport": transport,
        "authenticationSecurityGroupId": "sg-1123456789abcdef0",
        "vpcLinkSecurityGroupId": "sg-2123456789abcdef0",
    }
    if transport == "https":
        outputs["tlsServerName"] = "api.internal.garageflow.example"
    return {
        "schemaVersion": "2.0",
        "environment": "homologation",
        "producer": "ingress",
        "sourceCommit": SOURCE_COMMIT,
        "publishedAt": "2026-09-11T14:31:00Z",
        "outputs": outputs,
    }


class ServerlessTerraformVariablesTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.module = load_module()

    def build(self, directory, **overrides):
        package = directory / "customer-authentication.zip"
        package.write_bytes(b"synthetic zip")
        arguments = {
            "platform_document": platform_manifest(),
            "ingress_document": ingress_manifest(),
            "environment": "homologation",
            "aws_account_id": ACCOUNT_ID,
            "owner": "garageflow-team",
            "expires_on": "2026-12-31",
            "authentication_role_arn": f"arn:aws:iam::{ACCOUNT_ID}:role/LabRole",
            "authorizer_role_arn": f"arn:aws:iam::{ACCOUNT_ID}:role/LabRole",
            "package_path": package,
        }
        arguments.update(overrides)
        return self.module.build_tfvars(**arguments)

    def test_builds_exact_inputs_from_complete_http_contracts(self):
        with trusted_temp_directory() as directory:
            result = self.build(directory)

        self.assertEqual(
            {
                "environment",
                "owner",
                "expires_on",
                "platform_contract",
                "ingress_contract",
                "authentication_role_arn",
                "authorizer_role_arn",
                "package_path",
            },
            set(result),
        )
        self.assertEqual(platform_manifest()["outputs"], result["platform_contract"])
        self.assertEqual(ingress_manifest()["outputs"], result["ingress_contract"])
        self.assertNotIn("tlsServerName", result["ingress_contract"])
        self.assertEqual("homologation", result["environment"])
        self.assertEqual(f"arn:aws:iam::{ACCOUNT_ID}:role/LabRole", result["authentication_role_arn"])
        self.assertTrue(Path(result["package_path"]).is_absolute())

    def test_preserves_required_https_tls_server_name(self):
        with trusted_temp_directory() as directory:
            result = self.build(directory, ingress_document=ingress_manifest("https"))

        self.assertEqual("https", result["ingress_contract"]["transport"])
        self.assertEqual(
            "api.internal.garageflow.example",
            result["ingress_contract"]["tlsServerName"],
        )

    def test_rejects_any_contract_or_role_arn_from_another_account(self):
        cases = []
        wrong_secret = platform_manifest()
        wrong_secret["outputs"]["jwtSecretArn"] = (
            "arn:aws:secretsmanager:us-east-1:999999999999:secret:garageflow/jwt-AbCdEf"
        )
        cases.append({"platform_document": wrong_secret})
        wrong_listener = ingress_manifest()
        wrong_listener["outputs"]["listenerArn"] = wrong_listener["outputs"]["listenerArn"].replace(
            ACCOUNT_ID, "999999999999"
        )
        cases.append({"ingress_document": wrong_listener})
        cases.append({"authentication_role_arn": "arn:aws:iam::999999999999:role/LabRole"})
        cases.append({"authorizer_role_arn": "arn:aws:iam::999999999999:role/LabRole"})

        with trusted_temp_directory() as directory:
            for overrides in cases:
                with self.subTest(field=next(iter(overrides))):
                    with self.assertRaises(self.module.TerraformVariablesError):
                        self.build(directory, **overrides)

    def test_rejects_wrong_region_environment_expiry_and_malformed_role(self):
        wrong_region = platform_manifest()
        wrong_region["outputs"]["awsRegion"] = "us-west-2"
        wrong_environment = platform_manifest()
        wrong_environment["environment"] = "production"
        cases = (
            {"platform_document": wrong_region},
            {"platform_document": wrong_environment},
            {"expires_on": "31-12-2026"},
            {"owner": " garageflow-team"},
            {"authentication_role_arn": f"arn:aws:iam::{ACCOUNT_ID}:user/LabRole"},
        )

        with trusted_temp_directory() as directory:
            for overrides in cases:
                with self.subTest(field=next(iter(overrides))):
                    with self.assertRaises(self.module.TerraformVariablesError):
                        self.build(directory, **overrides)

    def test_rejects_package_outside_runner_temp_or_missing_zip(self):
        with trusted_temp_directory() as directory:
            outside = Path(tempfile.gettempdir()) / "outside-serverless-package.zip"
            with self.assertRaises(self.module.TerraformVariablesError):
                self.build(directory, package_path=outside)
            with self.assertRaises(self.module.TerraformVariablesError):
                self.build(directory, package_path=directory / "missing.zip")
            non_zip = directory / "package.txt"
            non_zip.write_text("not a zip", encoding="utf-8")
            with self.assertRaises(self.module.TerraformVariablesError):
                self.build(directory, package_path=non_zip)

    def test_cli_validates_manifests_and_writes_tfvars_only_in_runner_temp(self):
        with trusted_temp_directory() as directory:
            platform_path = directory / "platform.json"
            ingress_path = directory / "ingress.json"
            package_path = directory / "lambda.zip"
            platform_path.write_text(json.dumps(platform_manifest()), encoding="utf-8")
            ingress_path.write_text(json.dumps(ingress_manifest()), encoding="utf-8")
            package_path.write_bytes(b"synthetic zip")

            status = self.module.main(
                [
                    "--platform-contract", str(platform_path),
                    "--ingress-contract", str(ingress_path),
                    "--output", "serverless.auto.tfvars.json",
                    "--environment", "homologation",
                    "--aws-account-id", ACCOUNT_ID,
                    "--owner", "garageflow-team",
                    "--expires-on", "2026-12-31",
                    "--authentication-role-arn", f"arn:aws:iam::{ACCOUNT_ID}:role/LabRole",
                    "--authorizer-role-arn", f"arn:aws:iam::{ACCOUNT_ID}:role/LabRole",
                    "--package-path", str(package_path),
                ]
            )
            written = json.loads((directory / "serverless.auto.tfvars.json").read_text(encoding="utf-8"))

        self.assertEqual(0, status)
        self.assertEqual(platform_manifest()["outputs"], written["platform_contract"])


if __name__ == "__main__":
    unittest.main()
