import importlib.util
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from contextlib import chdir, contextmanager, redirect_stderr, redirect_stdout
from datetime import datetime
from pathlib import Path
from unittest.mock import patch

from jsonschema import Draft202012Validator, FormatChecker


ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "scripts" / "infra_contract.py"
SCHEMA_PATH = ROOT / "contracts" / "infra-contract-v1.schema.json"


@contextmanager
def trusted_temp_directory():
    with tempfile.TemporaryDirectory() as directory:
        with patch.dict(os.environ, {"RUNNER_TEMP": directory}):
            yield Path(directory)


def create_directory_link(link: Path, target: Path):
    if os.name == "nt":
        subprocess.run(
            ["cmd", "/c", "mklink", "/J", str(link), str(target)],
            capture_output=True,
            check=True,
            text=True,
        )
    else:
        link.symlink_to(target, target_is_directory=True)


def load_module():
    if not MODULE_PATH.is_file():
        raise AssertionError("infrastructure contract tooling is missing")

    spec = importlib.util.spec_from_file_location("infra_contract", MODULE_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def platform_outputs():
    return {
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
        "ecrRepositoryUrl": "123456789012.dkr.ecr.us-east-1.amazonaws.com/garageflow-homologation",
        "apiGatewayId": "a1b2c3d4e5",
        "apiGatewayExecutionArn": "arn:aws:execute-api:us-east-1:123456789012:a1b2c3d4e5",
        "jwtSecretArn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:garageflow/jwt-AbCdEf",
        "internalAuthSecretArn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:garageflow/internal-AbCdEf",
        "bootstrapSecretArn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:garageflow/bootstrap-AbCdEf",
        "webhookSecretArn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:garageflow/webhook-AbCdEf",
        "snsTopicArn": "arn:aws:sns:us-east-1:123456789012:garageflow-homologation",
    }


def producer_outputs():
    return {
        "platform": platform_outputs(),
        "database": {
            "databaseHost": "garageflow.cluster-c123456789ab.us-east-1.rds.amazonaws.com",
            "databasePort": 5432,
            "databaseName": "garageflow",
            "databaseSecretArn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:garageflow/database-AbCdEf",
            "databaseSecurityGroupId": "sg-1123456789abcdef0",
        },
        "ingress": {
            "listenerArn": "arn:aws:elasticloadbalancing:us-east-1:123456789012:listener/app/garageflow/0123456789abcdef/0123456789abcdef",
            "internalApiBaseUrl": "https://api.internal.garageflow.example",
            "tlsServerName": "api.internal.garageflow.example",
        },
        "serverless": {
            "customerAuthenticationAliasArn": "arn:aws:lambda:us-east-1:123456789012:function:garageflow-auth:live",
            "requestAuthorizerAliasArn": "arn:aws:lambda:us-east-1:123456789012:function:garageflow-authorizer:live",
        },
    }


def manifest(producer="platform", environment="homologation", outputs=None):
    return {
        "schemaVersion": "1.0",
        "environment": environment,
        "producer": producer,
        "sourceCommit": "0123456789abcdef0123456789abcdef01234567",
        "publishedAt": "2026-09-11T14:30:00Z",
        "outputs": platform_outputs() if outputs is None else outputs,
    }


class ContractValidationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.contract = load_module()

    def assert_contract_error(self, document, producer="platform", environment="homologation"):
        with self.assertRaises(self.contract.ContractError):
            self.contract.validate_contract(document, producer, environment)

    def test_valid_platform_manifest_round_trips_with_additive_metadata(self):
        document = manifest()
        document["buildId"] = "20260911.1"
        document["outputs"]["futureEndpoint"] = "https://future.example.com"

        validated = self.contract.validate_contract(document, "platform", "homologation")

        self.assertEqual(document, validated)
        self.assertIsNot(document, validated)
        self.assertIsNot(document["outputs"], validated["outputs"])

    def test_all_producer_manifests_validate_with_complete_outputs(self):
        for producer, outputs in producer_outputs().items():
            with self.subTest(producer=producer):
                document = manifest(producer=producer, outputs=outputs)
                self.assertEqual(
                    document,
                    self.contract.validate_contract(document, producer, "homologation"),
                )

    def test_rejects_unknown_expected_or_declared_producer(self):
        for expected, declared in (("worker", "platform"), ("platform", "worker"), ("database", "platform")):
            with self.subTest(expected=expected, declared=declared):
                self.assert_contract_error(manifest(producer=declared), expected)

    def test_rejects_wrong_environment_and_schema_version(self):
        wrong_environment = manifest(environment="production")
        wrong_schema = manifest()
        wrong_schema["schemaVersion"] = "2.0"

        self.assert_contract_error(wrong_environment)
        self.assert_contract_error(wrong_schema)

    def test_rejects_missing_required_metadata_or_output(self):
        for path in ("sourceCommit", "outputs"):
            with self.subTest(path=path):
                document = manifest()
                del document[path]
                self.assert_contract_error(document)

        document = manifest()
        del document["outputs"]["vpcId"]
        self.assert_contract_error(document)

    def test_rejects_wrong_array_types_empty_arrays_and_duplicates(self):
        invalid_values = (
            "subnet-0123456789abcdef0",
            [],
            ["subnet-0123456789abcdef0", "subnet-0123456789abcdef0"],
            ["subnet-0123456789abcdef0", 7],
        )
        for value in invalid_values:
            with self.subTest(value_type=type(value).__name__, value_length=len(value)):
                document = manifest()
                document["outputs"]["publicSubnetIds"] = value
                self.assert_contract_error(document)

    def test_rejects_bool_and_other_wrong_database_port_types(self):
        outputs = {
            "databaseHost": "garageflow.example.com",
            "databasePort": 5432,
            "databaseName": "garageflow",
            "databaseSecretArn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:garageflow/database-AbCdEf",
            "databaseSecurityGroupId": "sg-1123456789abcdef0",
        }
        for value in (True, "5432", 0, 65536):
            with self.subTest(value_type=type(value).__name__):
                document = manifest(producer="database", outputs={**outputs, "databasePort": value})
                self.assert_contract_error(document, producer="database")

    def test_rejects_malformed_arns_urls_hostnames_and_resource_ids(self):
        mutations = {
            "vpcId": "not-a-vpc",
            "clusterSecurityGroupId": "sg-nothex",
            "apiGatewayExecutionArn": "not-an-arn",
            "jwtSecretArn": "arn:aws:sns:us-east-1:123456789012:not-a-secret",
            "ecrRepositoryUrl": "https://123456789012.dkr.ecr.us-east-1.amazonaws.com/repo",
        }
        for field, value in mutations.items():
            with self.subTest(field=field):
                document = manifest()
                document["outputs"][field] = value
                self.assert_contract_error(document)

        ingress = {
            "listenerArn": "arn:aws:elasticloadbalancing:us-east-1:123456789012:listener/app/name/id/id",
            "internalApiBaseUrl": "http://api.internal.example",
            "tlsServerName": "https://api.internal.example",
        }
        self.assert_contract_error(manifest(producer="ingress", outputs=ingress), producer="ingress")

        database = {
            "databaseHost": "bad_host.example.com",
            "databasePort": 5432,
            "databaseName": "garageflow",
            "databaseSecretArn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:garageflow/database-AbCdEf",
            "databaseSecurityGroupId": "sg-1123456789abcdef0",
        }
        self.assert_contract_error(manifest(producer="database", outputs=database), producer="database")

    def test_rejects_incomplete_or_wrong_service_resource_arns(self):
        platform_mutations = {
            "jwtSecretArn": "arn:aws:secretsmanager::123456789012:secret:",
            "apiGatewayExecutionArn": "arn:aws:execute-api:us-east-1:123456789012:wrong-resource",
        }
        for field, value in platform_mutations.items():
            with self.subTest(field=field):
                document = manifest()
                document["outputs"][field] = value
                self.assert_contract_error(document)

        valid_aliases = {
            "customerAuthenticationAliasArn": "arn:aws:lambda:us-east-1:123456789012:function:garageflow-auth:live",
            "requestAuthorizerAliasArn": "arn:aws:lambda:us-east-1:123456789012:function:garageflow-authorizer:live",
        }
        incomplete_alias = "arn:aws:lambda:us-east-1:123456789012:function:garageflow:"
        for field in valid_aliases:
            with self.subTest(field=field):
                outputs = {**valid_aliases, field: incomplete_alias}
                document = manifest(producer="serverless", outputs=outputs)
                self.assert_contract_error(document, producer="serverless")

    def test_rejects_invalid_timestamp_and_source_commit(self):
        for field, value in (
            ("sourceCommit", "ABCDEF0123456789abcdef0123456789abcdef01"),
            ("sourceCommit", "01234567"),
            ("publishedAt", "2026-09-11T14:30:00+00:00"),
            ("publishedAt", "2026-09-11 14:30:00Z"),
            ("publishedAt", "2026-02-30T14:30:00Z"),
        ):
            with self.subTest(field=field, value=value):
                document = manifest()
                document[field] = value
                self.assert_contract_error(document)

    def test_rejects_surrounding_whitespace_and_control_characters(self):
        for field, value in (("clusterName", " garageflow"), ("databaseName", "garage\nflow")):
            with self.subTest(field=field):
                if field == "clusterName":
                    document = manifest()
                    producer = "platform"
                else:
                    outputs = {
                        "databaseHost": "garageflow.example.com",
                        "databasePort": 5432,
                        "databaseName": "garageflow",
                        "databaseSecretArn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:garageflow/database-AbCdEf",
                        "databaseSecurityGroupId": "sg-1123456789abcdef0",
                    }
                    document = manifest(producer="database", outputs=outputs)
                    producer = "database"
                document["outputs"][field] = value
                self.assert_contract_error(document, producer=producer)

    def test_rejects_secret_like_field_names_recursively_without_echoing_values(self):
        sensitive_value = "DO-NOT-DISCLOSE-9b5d6a"
        for extra in (
            {"password": sensitive_value},
            {"metadata": {"apiToken": sensitive_value}},
            {"metadata": [{"privateKey": sensitive_value}]},
            {"metadata": {"passwordHash": sensitive_value}},
            {"metadata": {"privatePem": sensitive_value}},
            {"metadata": {"databaseSecretArn": sensitive_value}},
            {"databaseSecretArn": sensitive_value},
        ):
            with self.subTest(extra=next(iter(extra))):
                document = manifest()
                document["outputs"]["future"] = extra
                with self.assertRaises(self.contract.ContractError) as captured:
                    self.contract.validate_contract(document, "platform", "homologation")
                self.assertNotIn(sensitive_value, str(captured.exception))

    def test_secret_validation_does_not_echo_sensitive_field_name_content(self):
        sensitive_value = "DO-NOT-DISCLOSE-in-key-18aa"
        document = manifest()
        document["outputs"][f"password_{sensitive_value}"] = "redacted"

        with self.assertRaises(self.contract.ContractError) as captured:
            self.contract.validate_contract(document, "platform", "homologation")

        self.assertNotIn(sensitive_value, str(captured.exception))

    def test_secret_validation_redacts_arbitrary_ancestor_keys_through_dicts_and_lists(self):
        sensitive_value = "DO-NOT-DISCLOSE-parent-18aa"
        for extension in (
            {sensitive_value: {"password": "synthetic"}},
            {sensitive_value: [{"metadata": {"privateKey": "synthetic"}}]},
        ):
            with self.subTest(shape=type(next(iter(extension.values()))).__name__):
                document = manifest()
                document["outputs"].update(extension)

                with self.assertRaises(self.contract.ContractError) as captured:
                    self.contract.validate_contract(document, "platform", "homologation")

                self.assertNotIn(sensitive_value, str(captured.exception))
                self.assertNotIn("\n", str(captured.exception))

    def test_rejects_secret_reference_field_owned_by_another_producer(self):
        sensitive_value = "DO-NOT-DISCLOSE-cross-producer-b27f"
        document = manifest()
        document["outputs"]["databaseSecretArn"] = sensitive_value

        with self.assertRaises(self.contract.ContractError) as captured:
            self.contract.validate_contract(document, "platform", "homologation")

        self.assertNotIn(sensitive_value, str(captured.exception))


class ContractBuilderAndLoaderTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.contract = load_module()

    def test_build_contract_is_deterministic_with_explicit_published_at(self):
        outputs = platform_outputs()

        built = self.contract.build_contract(
            outputs,
            "platform",
            "homologation",
            "0123456789abcdef0123456789abcdef01234567",
            "2026-09-11T14:30:00Z",
        )

        self.assertEqual(manifest(), built)

    def test_build_contract_omits_additive_publisher_outputs(self):
        outputs = platform_outputs()
        outputs["futurePublicMetadata"] = "safe-but-not-v1"

        built = self.contract.build_contract(
            outputs,
            "platform",
            "homologation",
            "0123456789abcdef0123456789abcdef01234567",
            "2026-09-11T14:30:00Z",
        )

        self.assertNotIn("futurePublicMetadata", built["outputs"])
        self.assertEqual(set(platform_outputs()), set(built["outputs"]))

    def test_build_contract_rejects_secret_like_unknown_output_before_filtering(self):
        outputs = platform_outputs()
        outputs["accessToken"] = "DO-NOT-DISCLOSE-44c1"

        with self.assertRaises(self.contract.ContractError) as captured:
            self.contract.build_contract(
                outputs,
                "platform",
                "homologation",
                "0123456789abcdef0123456789abcdef01234567",
                "2026-09-11T14:30:00Z",
            )

        self.assertNotIn("DO-NOT-DISCLOSE-44c1", str(captured.exception))

    def test_load_contract_reads_and_validates_json(self):
        with trusted_temp_directory() as directory:
            path = directory / "contract.json"
            path.write_text(json.dumps(manifest()), encoding="utf-8")

            loaded = self.contract.load_contract(path, "platform", "homologation")

        self.assertEqual(manifest(), loaded)

    def test_load_contract_wraps_invalid_json_without_echoing_contents(self):
        sensitive_value = "DO-NOT-DISCLOSE-7703"
        with trusted_temp_directory() as directory:
            path = directory / "contract.json"
            path.write_text('{"password":"' + sensitive_value, encoding="utf-8")

            with self.assertRaises(self.contract.ContractError) as captured:
                self.contract.load_contract(path, "platform", "homologation")

        self.assertNotIn(sensitive_value, str(captured.exception))

    def test_public_interfaces_reject_non_object_documents_and_outputs(self):
        with self.assertRaises(self.contract.ContractError):
            self.contract.validate_contract([], "platform", "homologation")
        with self.assertRaises(self.contract.ContractError):
            self.contract.build_contract(
                [],
                "platform",
                "homologation",
                "0123456789abcdef0123456789abcdef01234567",
            )


class ContractArtifactPathTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.contract = load_module()

    def run_main(self, arguments):
        stdout = io.StringIO()
        stderr = io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            status = self.contract.main(arguments)
        return status, stdout.getvalue(), stderr.getvalue()

    def test_relative_paths_read_and_write_under_runner_temp(self):
        with trusted_temp_directory() as directory:
            (directory / "outputs.json").write_text(
                json.dumps(platform_outputs()),
                encoding="utf-8",
            )

            status, stdout, stderr = self.run_main(
                [
                    "publish",
                    "--input",
                    "outputs.json",
                    "--output",
                    "contract.json",
                    "--producer",
                    "platform",
                    "--environment",
                    "homologation",
                    "--source-commit",
                    "0123456789abcdef0123456789abcdef01234567",
                ]
            )
            loaded = self.contract.load_contract(
                "contract.json",
                "platform",
                "homologation",
            )

        self.assertEqual(0, status)
        self.assertEqual("", stdout)
        self.assertEqual("", stderr)
        self.assertEqual(platform_outputs(), loaded["outputs"])

    def test_load_uses_operating_system_temp_when_runner_temp_is_unset(self):
        with tempfile.TemporaryDirectory() as directory:
            contract_path = Path(directory) / "contract.json"
            contract_path.write_text(json.dumps(manifest()), encoding="utf-8")
            with patch.dict(os.environ):
                os.environ.pop("RUNNER_TEMP", None)
                loaded = self.contract.load_contract(
                    contract_path,
                    "platform",
                    "homologation",
                )

        self.assertEqual(manifest(), loaded)

    def test_load_rejects_parent_traversal_and_absolute_outside_path_without_disclosure(self):
        sensitive_name = "DO-NOT-DISCLOSE-outside-contract-713d.json"
        with tempfile.TemporaryDirectory() as parent_directory:
            parent = Path(parent_directory)
            trusted = parent / "trusted"
            trusted.mkdir()
            outside = parent / sensitive_name
            outside.write_text(json.dumps(manifest()), encoding="utf-8")

            with patch.dict(os.environ, {"RUNNER_TEMP": str(trusted)}), chdir(trusted):
                for supplied_path in (f"../{sensitive_name}", outside):
                    with self.subTest(path_kind=type(supplied_path).__name__):
                        with self.assertRaises(self.contract.ContractError) as captured:
                            self.contract.load_contract(
                                supplied_path,
                                "platform",
                                "homologation",
                            )
                        self.assertNotIn(sensitive_name, str(captured.exception))

    def test_publish_rejects_outside_input_and_output_paths_without_disclosure(self):
        sensitive_name = "DO-NOT-DISCLOSE-outside-output-50ac.json"
        with tempfile.TemporaryDirectory() as parent_directory:
            parent = Path(parent_directory)
            trusted = parent / "trusted"
            trusted.mkdir()
            inside_input = trusted / "outputs.json"
            outside_input = parent / sensitive_name
            inside_input.write_text(json.dumps(platform_outputs()), encoding="utf-8")
            outside_input.write_text(json.dumps(platform_outputs()), encoding="utf-8")

            with patch.dict(os.environ, {"RUNNER_TEMP": str(trusted)}):
                cases = (
                    (outside_input, trusted / "contract.json"),
                    (inside_input, parent / sensitive_name),
                )
                for supplied_input, supplied_output in cases:
                    with self.subTest(outside="input" if supplied_input == outside_input else "output"):
                        status, stdout, stderr = self.run_main(
                            [
                                "publish",
                                "--input",
                                str(supplied_input),
                                "--output",
                                str(supplied_output),
                                "--producer",
                                "platform",
                                "--environment",
                                "homologation",
                                "--source-commit",
                                "0123456789abcdef0123456789abcdef01234567",
                            ]
                        )
                        self.assertEqual(2, status)
                        self.assertEqual("", stdout)
                        self.assertNotIn(sensitive_name, stderr)

    def test_read_and_write_reject_symlink_escapes(self):
        with tempfile.TemporaryDirectory() as parent_directory:
            parent = Path(parent_directory)
            trusted = parent / "trusted"
            outside = parent / "outside"
            trusted.mkdir()
            outside.mkdir()
            outside_contract = outside / "contract.json"
            outside_contract.write_text(json.dumps(manifest()), encoding="utf-8")
            outside_link = trusted / "outside-link"
            create_directory_link(outside_link, outside)
            (trusted / "outputs.json").write_text(
                json.dumps(platform_outputs()),
                encoding="utf-8",
            )

            with patch.dict(os.environ, {"RUNNER_TEMP": str(trusted)}):
                with self.assertRaises(self.contract.ContractError):
                    self.contract.load_contract(
                        outside_link / "contract.json",
                        "platform",
                        "homologation",
                    )
                status, _, _ = self.run_main(
                    [
                        "publish",
                        "--input",
                        str(trusted / "outputs.json"),
                        "--output",
                        str(outside_link / "published.json"),
                        "--producer",
                        "platform",
                        "--environment",
                        "homologation",
                        "--source-commit",
                        "0123456789abcdef0123456789abcdef01234567",
                    ]
                )
                outside_file_created = (outside / "published.json").exists()

        self.assertEqual(2, status)
        self.assertFalse(outside_file_created)


class ContractSchemaConformanceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.contract = load_module()
        cls.schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(cls.schema)
        cls.validator = Draft202012Validator(cls.schema, format_checker=FormatChecker())

    def test_schema_and_python_agree_on_normalized_secret_like_extension_keys(self):
        forbidden_field_names = (
            "api_key",
            "access-key",
            "client_secret",
            "credentialHash",
            "p-a-s-s-w-o-r-d",
            "pass_phrase",
            "passwd",
            "pem_private",
            "private_key",
            "private-pem",
            "refresh_token",
        )
        for field_name in forbidden_field_names:
            with self.subTest(field_name=field_name):
                document = manifest()
                document["outputs"]["futureMetadata"] = {field_name: "synthetic"}

                with self.assertRaises(self.contract.ContractError):
                    self.contract.validate_contract(document, "platform", "homologation")
                self.assertFalse(self.validator.is_valid(document))

        for field_name in ("apiVersion", "bypassMode", "compassHeading"):
            with self.subTest(field_name=field_name):
                compatible = manifest()
                compatible["outputs"]["futureMetadata"] = {field_name: True}
                self.assertEqual(
                    compatible,
                    self.contract.validate_contract(compatible, "platform", "homologation"),
                )
                self.validator.validate(compatible)

    def test_schema_accepts_complete_manifests_for_every_producer(self):
        for producer, outputs in producer_outputs().items():
            with self.subTest(producer=producer):
                self.validator.validate(manifest(producer=producer, outputs=outputs))

    def test_schema_and_python_accept_https_base_urls(self):
        longest_hostname = ".".join(["a" * 63] * 3 + ["b" * 61])
        valid_urls = (
            "https://api.internal.example",
            "https://api.internal.example/",
            "https://api.internal.example:443",
            "https://api.internal.example:443/",
            "HTTPS://API.Internal.Example/",
            "hTtPs://api.internal.example/",
            "https://api-v1.internal.example/",
            "https://a.b",
            "https://192.0.2.1/",
            f"https://{longest_hostname}:443/",
            # urlsplit treats these ports and empty components as an HTTPS origin.
            "https://api.internal.example:",
            "https://api.internal.example:0443/",
            "https://api.internal.example?",
            "https://api.internal.example#",
            "https://api.internal.example/?#",
        )
        for url in valid_urls:
            with self.subTest(url=url):
                outputs = producer_outputs()["ingress"]
                outputs["internalApiBaseUrl"] = url
                document = manifest(producer="ingress", outputs=outputs)

                self.assertEqual(
                    document,
                    self.contract.validate_contract(document, "ingress", "homologation"),
                )
                self.validator.validate(document)

    def test_schema_and_python_reject_invalid_https_base_urls(self):
        oversized_hostname = ".".join(["a" * 63] * 3 + ["b" * 62])
        invalid_urls = (
            "https://api.internal.example/v1",
            "https://api.internal.example//",
            "https://user@api.internal.example",
            "https://user:password@api.internal.example",
            "https://@api.internal.example",
            "https://api.internal.example?version=1",
            "https://api.internal.example/#section",
            "https://api.internal.example:8443",
            "https://api.internal.example:80/",
            "https://api.internal.example:0",
            "https://api.internal.example:65536",
            "https://api.internal.example:invalid",
            "http://api.internal.example",
            "https:///api.internal.example",
            "https://localhost",
            "https://api.internal.example.",
            "https://api..example",
            "https://-api.internal.example",
            "https://api-.internal.example",
            "https://api_internal.example",
            "https://[2001:db8::1]/",
            f"https://{'a' * 64}.example",
            f"https://{oversized_hostname}",
            " https://api.internal.example",
            "https://api.internal.example\n",
            "https://api.\tinternal.example",
            "https://api.internal.example/\u007f",
        )
        validator_without_formats = Draft202012Validator(self.schema)
        for url in invalid_urls:
            with self.subTest(url=url):
                outputs = producer_outputs()["ingress"]
                outputs["internalApiBaseUrl"] = url
                document = manifest(producer="ingress", outputs=outputs)

                with self.assertRaises(self.contract.ContractError):
                    self.contract.validate_contract(document, "ingress", "homologation")
                self.assertFalse(self.validator.is_valid(document))
                self.assertFalse(validator_without_formats.is_valid(document))

    def test_schema_rejects_incomplete_or_wrong_service_resource_arns(self):
        invalid_documents = []
        for field, value in (
            ("jwtSecretArn", "arn:aws:secretsmanager::123456789012:secret:"),
            ("apiGatewayExecutionArn", "arn:aws:execute-api:us-east-1:123456789012:wrong-resource"),
        ):
            document = manifest()
            document["outputs"][field] = value
            invalid_documents.append((field, document))

        for field in ("customerAuthenticationAliasArn", "requestAuthorizerAliasArn"):
            outputs = producer_outputs()["serverless"]
            outputs[field] = "arn:aws:lambda:us-east-1:123456789012:function:garageflow:"
            invalid_documents.append((field, manifest(producer="serverless", outputs=outputs)))

        for field, document in invalid_documents:
            with self.subTest(field=field):
                self.assertFalse(self.validator.is_valid(document))


class ContractCliTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.contract = load_module()

    def run_cli(self, *arguments):
        return subprocess.run(
            [sys.executable, str(MODULE_PATH), *arguments],
            cwd=ROOT,
            capture_output=True,
            text=True,
            check=False,
        )

    def test_help_describes_the_trusted_temporary_path_boundary(self):
        result = self.run_cli("--help")

        self.assertEqual(0, result.returncode)
        self.assertIn("RUNNER_TEMP", result.stdout)
        self.assertIn("operating system temporary directory", result.stdout)

    def test_publish_then_validate_round_trip(self):
        with trusted_temp_directory() as directory:
            outputs_path = directory / "outputs.json"
            contract_path = directory / "contract.json"
            outputs_path.write_text(json.dumps(platform_outputs()), encoding="utf-8")

            published = self.run_cli(
                "publish",
                "--input",
                str(outputs_path),
                "--output",
                str(contract_path),
                "--producer",
                "platform",
                "--environment",
                "homologation",
                "--source-commit",
                "0123456789abcdef0123456789abcdef01234567",
            )
            validated = self.run_cli(
                "validate",
                "--file",
                str(contract_path),
                "--producer",
                "platform",
                "--environment",
                "homologation",
            )

            self.assertEqual(0, published.returncode, published.stderr)
            self.assertEqual(0, validated.returncode, validated.stderr)
            written = json.loads(contract_path.read_text(encoding="utf-8"))
            emitted = json.loads(validated.stdout)
            datetime.fromisoformat(written["publishedAt"].replace("Z", "+00:00"))
            self.assertEqual(written, emitted)
            self.assertEqual(manifest()["outputs"], written["outputs"])
            self.assertEqual("1.0", written["schemaVersion"])
            self.assertEqual("homologation", written["environment"])
            self.assertEqual("platform", written["producer"])
            self.assertEqual(
                "0123456789abcdef0123456789abcdef01234567",
                written["sourceCommit"],
            )

    def test_main_publishes_then_validates_contract_in_process(self):
        with trusted_temp_directory() as directory:
            outputs_path = directory / "outputs.json"
            contract_path = directory / "contract.json"
            outputs_path.write_text(json.dumps(platform_outputs()), encoding="utf-8")
            publish_stdout = io.StringIO()
            publish_stderr = io.StringIO()

            with redirect_stdout(publish_stdout), redirect_stderr(publish_stderr):
                publish_status = self.contract.main(
                    [
                        "publish",
                        "--input",
                        str(outputs_path),
                        "--output",
                        str(contract_path),
                        "--producer",
                        "platform",
                        "--environment",
                        "homologation",
                        "--source-commit",
                        "0123456789abcdef0123456789abcdef01234567",
                    ]
                )

            validate_stdout = io.StringIO()
            validate_stderr = io.StringIO()
            with redirect_stdout(validate_stdout), redirect_stderr(validate_stderr):
                validate_status = self.contract.main(
                    [
                        "validate",
                        "--file",
                        str(contract_path),
                        "--producer",
                        "platform",
                        "--environment",
                        "homologation",
                    ]
                )
            written = json.loads(contract_path.read_text(encoding="utf-8"))

        self.assertEqual(0, publish_status)
        self.assertEqual("", publish_stdout.getvalue())
        self.assertEqual("", publish_stderr.getvalue())
        self.assertEqual(0, validate_status)
        self.assertEqual("", validate_stderr.getvalue())
        self.assertEqual(written, json.loads(validate_stdout.getvalue()))

    def test_main_returns_two_for_unreadable_publish_input_without_echoing_path(self):
        sensitive_value = "DO-NOT-DISCLOSE-path-971e"
        with trusted_temp_directory() as directory:
            missing_path = directory / sensitive_value
            output_path = directory / "contract.json"
            stderr = io.StringIO()
            with redirect_stderr(stderr):
                status = self.contract.main(
                    [
                        "publish",
                        "--input",
                        str(missing_path),
                        "--output",
                        str(output_path),
                        "--producer",
                        "platform",
                        "--environment",
                        "homologation",
                        "--source-commit",
                        "0123456789abcdef0123456789abcdef01234567",
                    ]
                )

        self.assertEqual(2, status)
        self.assertIn("could not be read as JSON", stderr.getvalue())
        self.assertNotIn(sensitive_value, stderr.getvalue())

    def test_cli_returns_nonzero_without_disclosing_invalid_secret_value(self):
        sensitive_value = "DO-NOT-DISCLOSE-f42e"
        with trusted_temp_directory() as directory:
            contract_path = directory / "contract.json"
            document = manifest()
            document["outputs"]["password"] = sensitive_value
            contract_path.write_text(json.dumps(document), encoding="utf-8")

            result = self.run_cli(
                "validate",
                "--file",
                str(contract_path),
                "--producer",
                "platform",
                "--environment",
                "homologation",
            )

        self.assertNotEqual(0, result.returncode)
        self.assertNotIn(sensitive_value, result.stdout)
        self.assertNotIn(sensitive_value, result.stderr)

    def test_cli_redacts_arbitrary_ancestor_keys_for_nested_secret_fields(self):
        sensitive_value = "DO-NOT-DISCLOSE-parent-cli-13bf\nforged-log"
        with trusted_temp_directory() as directory:
            contract_path = directory / "contract.json"
            document = manifest()
            document["outputs"][sensitive_value] = [{"password": "synthetic"}]
            contract_path.write_text(json.dumps(document), encoding="utf-8")

            result = self.run_cli(
                "validate",
                "--file",
                str(contract_path),
                "--producer",
                "platform",
                "--environment",
                "homologation",
            )

        self.assertNotEqual(0, result.returncode)
        self.assertNotIn(sensitive_value, result.stdout)
        self.assertNotIn(sensitive_value, result.stderr)
        self.assertNotIn("forged-log", result.stderr)

    def test_cli_does_not_echo_invalid_expected_producer(self):
        sensitive_value = "DO-NOT-DISCLOSE-producer-a6e9"
        with trusted_temp_directory() as directory:
            contract_path = directory / "contract.json"
            contract_path.write_text(json.dumps(manifest()), encoding="utf-8")

            result = self.run_cli(
                "validate",
                "--file",
                str(contract_path),
                "--producer",
                sensitive_value,
                "--environment",
                "homologation",
            )

        self.assertNotEqual(0, result.returncode)
        self.assertNotIn(sensitive_value, result.stdout)
        self.assertNotIn(sensitive_value, result.stderr)

    def test_cli_argument_errors_do_not_echo_unrecognized_values(self):
        sensitive_value = "DO-NOT-DISCLOSE-argument-4c91"

        with trusted_temp_directory() as directory:
            contract_path = directory / "contract.json"
            contract_path.write_text(json.dumps(manifest()), encoding="utf-8")
            result = self.run_cli(
                "validate",
                "--file",
                str(contract_path),
                "--producer",
                "platform",
                "--environment",
                "homologation",
                "--unexpected",
                sensitive_value,
            )

        self.assertNotEqual(0, result.returncode)
        self.assertNotIn(sensitive_value, result.stdout)
        self.assertNotIn(sensitive_value, result.stderr)


if __name__ == "__main__":
    unittest.main()
