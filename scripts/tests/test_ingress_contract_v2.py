import copy
import io
import json
import unittest
from contextlib import redirect_stderr, redirect_stdout

from jsonschema import Draft202012Validator, FormatChecker

from test_infra_contract import ROOT, load_module, manifest, producer_outputs, trusted_temp_directory


def ingress_v2(transport="http"):
    outputs = copy.deepcopy(producer_outputs()["ingress"])
    outputs.update(
        transport=transport,
        internalApiBaseUrl=f"{transport}://internal-garageflow-123.us-east-1.elb.amazonaws.com",
        authenticationSecurityGroupId="sg-0123456789abcdef0",
        vpcLinkSecurityGroupId="sg-1123456789abcdef0",
    )
    if transport == "http":
        outputs.pop("tlsServerName")
    document = manifest(producer="ingress", outputs=outputs)
    document["schemaVersion"] = "2.0"
    return document


class IngressV2Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.contract = load_module()

    def assert_valid(self, document):
        self.assertEqual(document, self.contract.validate_contract(document, "ingress", "homologation"))

    def test_explicit_http_and_https_v2_validate(self):
        for transport in ("http", "https"):
            with self.subTest(transport=transport):
                self.assert_valid(ingress_v2(transport))

    def test_v1_is_preserved_and_does_not_accept_http(self):
        document = manifest("ingress", outputs=producer_outputs()["ingress"])
        self.assert_valid(document)
        document["outputs"]["internalApiBaseUrl"] = "http://internal.example.com"
        with self.assertRaises(self.contract.ContractError):
            self.assert_valid(document)

    def test_version_is_only_supported_for_ingress_and_expected_version_is_enforced(self):
        with self.assertRaises(self.contract.ContractError):
            self.contract.validate_contract(ingress_v2(), "ingress", "homologation", schema_version="1.0")
        for producer in ("platform", "database", "serverless"):
            document = manifest(producer, outputs=producer_outputs()[producer])
            document["schemaVersion"] = "2.0"
            with self.subTest(producer=producer), self.assertRaises(self.contract.ContractError):
                self.contract.validate_contract(document, producer, "homologation")

    def test_tls_and_security_group_requirements(self):
        cases = []
        for field in ("transport", "authenticationSecurityGroupId", "vpcLinkSecurityGroupId"):
            document = ingress_v2()
            del document["outputs"][field]
            cases.append(document)
        missing_tls = ingress_v2("https")
        del missing_tls["outputs"]["tlsServerName"]
        cases.append(missing_tls)
        for field, value in (("tlsServerName", "unneeded.example.com"), ("transport", "HTTP"),
                             ("authenticationSecurityGroupId", "sg-invalid"), ("vpcLinkSecurityGroupId", None)):
            document = ingress_v2()
            document["outputs"][field] = value
            cases.append(document)
        for document in cases:
            with self.subTest(outputs=document["outputs"]), self.assertRaises(self.contract.ContractError):
                self.assert_valid(document)

    def test_schema_and_validator_agree_on_transports_urls_and_required_fields(self):
        schema_path = ROOT / "contracts" / "infra-contract-v2.schema.json"
        self.assertTrue(schema_path.is_file(), "v2 schema must be available to consumers")
        schema = json.loads(schema_path.read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
        validator = Draft202012Validator(schema, format_checker=FormatChecker())
        cases = [(ingress_v2(), True), (ingress_v2("https"), True)]
        for transport, url, valid in (
            ("http", "HTTP://internal.example.com:80/", True),
            ("https", "https://internal.example.com:443/", True),
            ("http", "http://internal.example.com:", False),
            ("http", "http://internal.example.com:443", False),
            ("https", "http://internal.example.com", False),
            ("http", "http://name:password@internal.example.com", False),
            ("http", "http://internal.example.com/path", False),
            ("http", "http://internal.example.com?", False),
            ("http", "http://internal.example.com#", False),
            ("http", "http://internal.example.com?x=y", False),
            ("http", "http://127.0.0.1:8080", False),
            ("http", "http://" + "a" * 64 + ".example.com", False),
        ):
            document = ingress_v2(transport)
            document["outputs"]["internalApiBaseUrl"] = url
            cases.append((document, valid))
        for field, value in (("transport", "ftp"), ("tlsServerName", "not-needed.example.com"),
                             ("authenticationSecurityGroupId", "invalid"),
                             ("vpcLinkSecurityGroupId", "sg-0123456789abcdef0\n")):
            document = ingress_v2()
            document["outputs"][field] = value
            cases.append((document, False))
        for field in ("authenticationSecurityGroupId", "vpcLinkSecurityGroupId", "transport"):
            document = ingress_v2()
            del document["outputs"][field]
            cases.append((document, False))
        document = ingress_v2("https")
        del document["outputs"]["tlsServerName"]
        cases.append((document, False))
        for document, expected in cases:
            with self.subTest(document=document):
                self.assertEqual(expected, validator.is_valid(document))
                try:
                    self.assert_valid(document)
                    actual = True
                except self.contract.ContractError:
                    actual = False
                self.assertEqual(expected, actual)

    def test_publisher_requires_explicit_version_and_preserves_only_known_fields(self):
        document = ingress_v2()
        outputs = copy.deepcopy(document["outputs"])
        outputs["futureMetadata"] = "not published"
        built = self.contract.build_contract(
            outputs, "ingress", "homologation", document["sourceCommit"], document["publishedAt"], schema_version="2.0")
        self.assertEqual(document, built)
        with self.assertRaises(self.contract.ContractError):
            self.contract.build_contract(outputs, "ingress", "homologation", document["sourceCommit"])
        for transport in ("http", "https"):
            document = ingress_v2(transport)
            built = self.contract.build_contract(document["outputs"], "ingress", "homologation",
                document["sourceCommit"], document["publishedAt"], schema_version="2.0")
            self.assertEqual(document, built)

    def test_publisher_rejects_unknown_version_and_sensitive_outputs(self):
        document = ingress_v2()
        for version in ("3.0", "2", None):
            with self.subTest(version=version), self.assertRaises(self.contract.ContractError):
                self.contract.build_contract(document["outputs"], "ingress", "homologation",
                    document["sourceCommit"], schema_version=version)
        document["outputs"]["password"] = "must-never-appear"
        with self.assertRaises(self.contract.ContractError) as caught:
            self.contract.build_contract(document["outputs"], "ingress", "homologation",
                document["sourceCommit"], schema_version="2.0")
        self.assertNotIn("must-never-appear", str(caught.exception))

    def test_cli_can_publish_and_load_explicit_v2(self):
        document = ingress_v2()
        with trusted_temp_directory() as directory:
            source, target = directory / "outputs.json", directory / "ingress.json"
            source.write_text(json.dumps(document["outputs"]), encoding="utf-8")
            self.assertEqual(0, self.contract.main(["publish", "--input", str(source), "--output", str(target),
                "--producer", "ingress", "--environment", "homologation", "--source-commit", document["sourceCommit"],
                "--schema-version", "2.0"]))
            loaded = self.contract.load_contract(target, "ingress", "homologation", schema_version="2.0")
            self.assertEqual("2.0", loaded["schemaVersion"])
            with redirect_stdout(io.StringIO()) as output:
                self.assertEqual(0, self.contract.main(["validate", "--file", str(target), "--producer", "ingress",
                    "--environment", "homologation", "--schema-version", "2.0"]))
            self.assertEqual(loaded, json.loads(output.getvalue()))
            with redirect_stderr(io.StringIO()) as error:
                self.assertEqual(2, self.contract.main(["validate", "--file", str(target), "--producer", "ingress",
                    "--environment", "homologation", "--schema-version", "1.0"]))
            self.assertIn("does not match expected schema version", error.getvalue())


if __name__ == "__main__":
    unittest.main()
