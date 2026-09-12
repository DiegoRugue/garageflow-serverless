#!/usr/bin/env python3
"""Build and validate GarageFlow infrastructure metadata contracts.

CLI file paths must resolve under RUNNER_TEMP when it is configured, or under
the operating system temporary directory otherwise. Relative paths resolve
from that trusted temporary directory.
"""

from __future__ import annotations

import argparse
import copy
import json
import os
import re
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlsplit


SCHEMA_VERSION = "1.0"
INGRESS_SCHEMA_VERSION = "2.0"
PRODUCERS = ("platform", "database", "ingress", "serverless")
ENVIRONMENTS = ("homologation", "production")
_CONTRACT_PATH = "contract"
_OUTPUTS_PATH = f"{_CONTRACT_PATH}.outputs"
_PUBLISHER_OUTPUTS_PATH = "outputs"

OUTPUT_FIELDS = {
    "platform": (
        "awsRegion",
        "vpcId",
        "publicSubnetIds",
        "privateApplicationSubnetIds",
        "databaseSubnetIds",
        "clusterName",
        "clusterSecurityGroupId",
        "ecrRepositoryUrl",
        "apiGatewayId",
        "apiGatewayExecutionArn",
        "jwtSecretArn",
        "internalAuthSecretArn",
        "bootstrapSecretArn",
        "webhookSecretArn",
        "snsTopicArn",
    ),
    "database": (
        "databaseHost",
        "databasePort",
        "databaseName",
        "databaseSecretArn",
        "databaseSecurityGroupId",
    ),
    "ingress": ("listenerArn", "internalApiBaseUrl", "tlsServerName"),
    "serverless": ("customerAuthenticationAliasArn", "requestAuthorizerAliasArn"),
}
INGRESS_V2_FIELDS = (
    "listenerArn", "internalApiBaseUrl", "transport",
    "authenticationSecurityGroupId", "vpcLinkSecurityGroupId", "tlsServerName",
)
_TRUSTED_FIELD_NAMES = frozenset(
    {
        "schemaVersion",
        "environment",
        "producer",
        "sourceCommit",
        "publishedAt",
        "outputs",
    }.union(*(set(fields) for fields in OUTPUT_FIELDS.values()), INGRESS_V2_FIELDS)
)

_SECRET_REFERENCE_FIELDS = {
    "platform": {
        "jwtSecretArn",
        "internalAuthSecretArn",
        "bootstrapSecretArn",
        "webhookSecretArn",
    },
    "database": {"databaseSecretArn"},
    "ingress": set(),
    "serverless": set(),
}
_SECRET_NAME_PARTS = (
    "password",
    "passwd",
    "passphrase",
    "token",
    "privatekey",
    "credential",
    "clientsecret",
    "apikey",
    "accesskey",
    "secret",
    "hash",
    "privatepem",
    "pemprivate",
)
_SHA_PATTERN = re.compile(r"^[0-9a-f]{40}$")
_RFC3339_UTC_PATTERN = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z$")
_REGION_PATTERN = re.compile(r"^[a-z]{2}(?:-gov)?-[a-z]+-\d+$")
_VPC_PATTERN = re.compile(r"^vpc-[0-9a-f]{8}(?:[0-9a-f]{9})?$")
_SUBNET_PATTERN = re.compile(r"^subnet-[0-9a-f]{8}(?:[0-9a-f]{9})?$")
_SECURITY_GROUP_PATTERN = re.compile(r"^sg-[0-9a-f]{8}(?:[0-9a-f]{9})?$")
_API_GATEWAY_ID_PATTERN = re.compile(r"^[a-z0-9]{10}$")
_ECR_URL_PATTERN = re.compile(
    r"^\d{12}\.dkr\.ecr\.[a-z0-9-]+\.amazonaws\.com(?:\.cn)?/"
    r"[a-z0-9]+(?:[._/-][a-z0-9]+)*$"
)
_CLUSTER_NAME_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$")
_DATABASE_NAME_PATTERN = re.compile(r"^[A-Za-z]\w{0,62}$", re.ASCII)
_ARN_PATTERN = re.compile(
    r"^arn:(?P<partition>aws(?:-us-gov|-cn)?):(?P<service>[a-z0-9-]+):"
    r"(?P<region>[a-z0-9-]+):(?P<account>\d{12}):(?P<resource>\S+)$"
)
_LAMBDA_ALIAS_RESOURCE_PATTERN = re.compile(
    r"^function:[A-Za-z0-9_-]{1,64}:(?!\d+$)[A-Za-z0-9_-]{1,128}$"
)
_ARTIFACT_PATH_ERROR = "artifact path must stay within the trusted temporary directory"
_BASE_HOST_PATTERN = (
    r"(?=[^:/?#]{1,253}(?:[:/]|$))[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?"
    r"(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+"
)
_INGRESS_URL_PATTERNS = {
    "http": re.compile(r"[Hh][Tt][Tt][Pp]://" + _BASE_HOST_PATTERN + r"(?::80)?/?"),
    "https": re.compile(r"[Hh][Tt][Tt][Pp][Ss]://" + _BASE_HOST_PATTERN + r"(?::443)?/?"),
}


class ContractError(ValueError):
    """Raised when an infrastructure metadata contract is invalid."""


class _SafeArgumentParser(argparse.ArgumentParser):
    def error(self, message: str) -> None:
        self.print_usage(sys.stderr)
        self.exit(2, f"{self.prog}: error: invalid arguments\n")


def _fail(path: str, reason: str) -> None:
    raise ContractError(f"{path}: {reason}")


def _required_mapping(value: object, path: str) -> dict:
    if type(value) is not dict:
        _fail(path, "must be an object")
    return value


def _resolve_artifact_path(path) -> Path:
    configured_base = os.environ.get("RUNNER_TEMP")
    base_path = Path(configured_base) if configured_base else Path(tempfile.gettempdir())
    try:
        trusted_base = base_path.resolve(strict=True)
        supplied_path = Path(path)
        candidate = supplied_path if supplied_path.is_absolute() else trusted_base / supplied_path
        resolved_path = candidate.resolve(strict=False)
    except (OSError, RuntimeError, TypeError, ValueError) as error:
        raise ContractError(_ARTIFACT_PATH_ERROR) from error

    if not trusted_base.is_dir() or resolved_path == trusted_base:
        raise ContractError(_ARTIFACT_PATH_ERROR)
    try:
        resolved_path.relative_to(trusted_base)
    except ValueError as error:
        raise ContractError(_ARTIFACT_PATH_ERROR) from error
    return resolved_path


def _required_string(value: object, path: str) -> str:
    if not isinstance(value, str) or not value:
        _fail(path, "must be a non-empty string")
    if value != value.strip():
        _fail(path, "must not contain surrounding whitespace")
    if any(ord(character) < 32 or ord(character) == 127 for character in value):
        _fail(path, "must not contain control characters")
    return value


def _match(value: object, path: str, pattern: re.Pattern[str], description: str) -> str:
    text = _required_string(value, path)
    if pattern.fullmatch(text) is None:
        _fail(path, description)
    return text


def _required(document: dict, field: str, path: str) -> object:
    if field not in document:
        _fail(f"{path}.{field}", "is required")
    return document[field]


def _validate_arn(value: object, path: str, service: str, resource_prefix: str | None = None) -> str:
    arn = _required_string(value, path)
    match = _ARN_PATTERN.fullmatch(arn)
    if match is None or match.group("service") != service:
        _fail(path, f"must be a valid {service} ARN")
    resource = match.group("resource")
    if resource_prefix is not None and (
        not resource.startswith(resource_prefix) or resource == resource_prefix
    ):
        _fail(path, f"must be a valid {service} ARN")
    return arn


def _validate_hostname(value: object, path: str) -> str:
    hostname = _required_string(value, path)
    if len(hostname) > 253 or hostname.endswith("."):
        _fail(path, "must be a valid hostname")
    labels = hostname.split(".")
    if len(labels) < 2 or any(
        not label
        or len(label) > 63
        or re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?", label) is None
        for label in labels
    ):
        _fail(path, "must be a valid hostname")
    return hostname


def _validate_https_url(value: object, path: str) -> str:
    url = _required_string(value, path)
    try:
        parsed = urlsplit(url)
        port = parsed.port
    except ValueError:
        _fail(path, "must be a valid HTTPS URL")
    if (
        parsed.scheme != "https"
        or not parsed.hostname
        or parsed.username is not None
        or parsed.password is not None
        or parsed.query
        or parsed.fragment
        or parsed.path not in ("", "/")
        or port not in (None, 443)
    ):
        _fail(path, "must be a valid HTTPS base URL")
    _validate_hostname(parsed.hostname, path)
    return url


def _validate_subnets(value: object, path: str) -> list[str]:
    if type(value) is not list or not value:
        _fail(path, "must be a non-empty array")
    validated = []
    for index, subnet in enumerate(value):
        validated.append(_match(subnet, f"{path}[{index}]", _SUBNET_PATTERN, "must be a subnet ID"))
    if len(set(validated)) != len(validated):
        _fail(path, "must not contain duplicate subnet IDs")
    return validated


def _normalized_key(key: str) -> str:
    return re.sub(r"[^a-z0-9]", "", key.lower())


def _check_secret_field_name(
    key: object,
    allowed_secret_references: set[str],
    references_allowed: bool,
    path: str,
) -> str:
    if not isinstance(key, str):
        _fail(path, "field names must be strings")
    if references_allowed and key in allowed_secret_references:
        return key
    normalized = _normalized_key(key)
    if any(part in normalized for part in _SECRET_NAME_PARTS):
        _fail(path, "contains a forbidden secret-like field name")
    return key


def _check_secret_fields(
    value: object,
    allowed_secret_references: set[str],
    path: str = _CONTRACT_PATH,
) -> None:
    if type(value) is list:
        for index, child in enumerate(value):
            _check_secret_fields(child, allowed_secret_references, f"{path}[{index}]")
        return
    if type(value) is not dict:
        return

    references_allowed = path in (_OUTPUTS_PATH, _PUBLISHER_OUTPUTS_PATH)
    for raw_key, child in value.items():
        key = _check_secret_field_name(
            raw_key,
            allowed_secret_references,
            references_allowed,
            path,
        )
        child_path = f"{path}.{key if key in _TRUSTED_FIELD_NAMES else '<extension>'}"
        _check_secret_fields(child, allowed_secret_references, child_path)


def _validate_schema_version(schema_version: object, producer: str) -> None:
    if schema_version != SCHEMA_VERSION and not (
        producer == "ingress" and schema_version == INGRESS_SCHEMA_VERSION
    ):
        _fail("contract.schemaVersion", "unsupported schema version for producer")


def _validate_metadata(document: dict, producer: str, environment: str) -> dict:
    if producer not in PRODUCERS:
        _fail("producer", "unsupported expected producer")
    if environment not in ENVIRONMENTS:
        _fail("environment", "unsupported expected environment")

    schema_version = _required_string(_required(document, "schemaVersion", "contract"), "contract.schemaVersion")
    _validate_schema_version(schema_version, producer)

    declared_environment = _required_string(
        _required(document, "environment", "contract"), "contract.environment"
    )
    if declared_environment != environment:
        _fail("contract.environment", "does not match expected environment")

    declared_producer = _required_string(_required(document, "producer", "contract"), "contract.producer")
    if declared_producer not in PRODUCERS or declared_producer != producer:
        _fail("contract.producer", "does not match expected producer")

    _match(
        _required(document, "sourceCommit", "contract"),
        "contract.sourceCommit",
        _SHA_PATTERN,
        "must be a 40-character lowercase Git SHA",
    )
    published_at = _match(
        _required(document, "publishedAt", "contract"),
        "contract.publishedAt",
        _RFC3339_UTC_PATTERN,
        "must be a UTC RFC3339 timestamp ending in Z",
    )
    try:
        datetime.fromisoformat(published_at[:-1] + "+00:00")
    except ValueError:
        _fail("contract.publishedAt", "must be a valid UTC RFC3339 timestamp")

    return _required_mapping(_required(document, "outputs", _CONTRACT_PATH), _OUTPUTS_PATH)


def _validate_platform(outputs: dict) -> None:
    base = _OUTPUTS_PATH
    aws_region = _match(
        _required(outputs, "awsRegion", base),
        f"{base}.awsRegion",
        _REGION_PATTERN,
        "must be an AWS region",
    )
    _match(_required(outputs, "vpcId", base), f"{base}.vpcId", _VPC_PATTERN, "must be a VPC ID")
    for field in ("publicSubnetIds", "privateApplicationSubnetIds", "databaseSubnetIds"):
        _validate_subnets(_required(outputs, field, base), f"{base}.{field}")
    _match(
        _required(outputs, "clusterName", base),
        f"{base}.clusterName",
        _CLUSTER_NAME_PATTERN,
        "must be a valid EKS cluster name",
    )
    _match(
        _required(outputs, "clusterSecurityGroupId", base),
        f"{base}.clusterSecurityGroupId",
        _SECURITY_GROUP_PATTERN,
        "must be a security group ID",
    )
    _match(
        _required(outputs, "ecrRepositoryUrl", base),
        f"{base}.ecrRepositoryUrl",
        _ECR_URL_PATTERN,
        "must be an ECR repository URL",
    )
    api_gateway_id = _match(
        _required(outputs, "apiGatewayId", base),
        f"{base}.apiGatewayId",
        _API_GATEWAY_ID_PATTERN,
        "must be an API Gateway ID",
    )
    execution_arn = _validate_arn(
        _required(outputs, "apiGatewayExecutionArn", base),
        f"{base}.apiGatewayExecutionArn",
        "execute-api",
    )
    execution_arn_parts = _ARN_PATTERN.fullmatch(execution_arn)
    if (
        execution_arn_parts.group("region") != aws_region
        or execution_arn_parts.group("resource") != api_gateway_id
    ):
        _fail(f"{base}.apiGatewayExecutionArn", "must identify the declared API Gateway")
    for field in ("jwtSecretArn", "internalAuthSecretArn", "bootstrapSecretArn", "webhookSecretArn"):
        _validate_arn(_required(outputs, field, base), f"{base}.{field}", "secretsmanager", "secret:")
    _validate_arn(_required(outputs, "snsTopicArn", base), f"{base}.snsTopicArn", "sns")


def _validate_database(outputs: dict) -> None:
    base = _OUTPUTS_PATH
    _validate_hostname(_required(outputs, "databaseHost", base), f"{base}.databaseHost")
    port = _required(outputs, "databasePort", base)
    if type(port) is not int or not 1 <= port <= 65535:
        _fail(f"{base}.databasePort", "must be an integer between 1 and 65535")
    _match(
        _required(outputs, "databaseName", base),
        f"{base}.databaseName",
        _DATABASE_NAME_PATTERN,
        "must be a valid PostgreSQL database name",
    )
    _validate_arn(
        _required(outputs, "databaseSecretArn", base),
        f"{base}.databaseSecretArn",
        "secretsmanager",
        "secret:",
    )
    _match(
        _required(outputs, "databaseSecurityGroupId", base),
        f"{base}.databaseSecurityGroupId",
        _SECURITY_GROUP_PATTERN,
        "must be a security group ID",
    )


def _validate_ingress(outputs: dict) -> None:
    base = _OUTPUTS_PATH
    _validate_arn(
        _required(outputs, "listenerArn", base),
        f"{base}.listenerArn",
        "elasticloadbalancing",
        "listener/",
    )
    _validate_https_url(_required(outputs, "internalApiBaseUrl", base), f"{base}.internalApiBaseUrl")
    _validate_hostname(_required(outputs, "tlsServerName", base), f"{base}.tlsServerName")


def _validate_ingress_v2(outputs: dict) -> None:
    base = _OUTPUTS_PATH
    _validate_arn(
        _required(outputs, "listenerArn", base), f"{base}.listenerArn",
        "elasticloadbalancing", "listener/",
    )
    transport = _required_string(_required(outputs, "transport", base), f"{base}.transport")
    if transport not in _INGRESS_URL_PATTERNS:
        _fail(f"{base}.transport", "must be http or https")
    _match(
        _required(outputs, "internalApiBaseUrl", base), f"{base}.internalApiBaseUrl",
        _INGRESS_URL_PATTERNS[transport], "must be a base URL matching the declared transport and default port",
    )
    for field in ("authenticationSecurityGroupId", "vpcLinkSecurityGroupId"):
        _match(_required(outputs, field, base), f"{base}.{field}", _SECURITY_GROUP_PATTERN,
               "must be a security group ID")
    if transport == "https":
        _validate_hostname(_required(outputs, "tlsServerName", base), f"{base}.tlsServerName")
    elif "tlsServerName" in outputs:
        _fail(f"{base}.tlsServerName", "must be absent for HTTP transport")


def _validate_serverless(outputs: dict) -> None:
    base = _OUTPUTS_PATH
    for field in ("customerAuthenticationAliasArn", "requestAuthorizerAliasArn"):
        arn = _validate_arn(_required(outputs, field, base), f"{base}.{field}", "lambda", "function:")
        resource = _ARN_PATTERN.fullmatch(arn).group("resource")
        if _LAMBDA_ALIAS_RESOURCE_PATTERN.fullmatch(resource) is None:
            _fail(f"{base}.{field}", "must identify a Lambda alias")


_OUTPUT_VALIDATORS = {
    "platform": _validate_platform,
    "database": _validate_database,
    "ingress": _validate_ingress,
    "serverless": _validate_serverless,
}


def validate_contract(
    document: dict, producer: str, environment: str, schema_version: str | None = None,
) -> dict:
    """Validate and return an independent copy of a complete manifest."""

    contract = _required_mapping(document, _CONTRACT_PATH)
    if producer not in PRODUCERS:
        _fail("producer", "unsupported expected producer")
    _check_secret_fields(contract, _SECRET_REFERENCE_FIELDS[producer])
    outputs = _validate_metadata(contract, producer, environment)
    if schema_version is not None and contract["schemaVersion"] != schema_version:
        _fail("contract.schemaVersion", "does not match expected schema version")
    if contract["schemaVersion"] == INGRESS_SCHEMA_VERSION:
        _validate_ingress_v2(outputs)
    else:
        _OUTPUT_VALIDATORS[producer](outputs)
    return copy.deepcopy(contract)


def load_contract(path, producer: str, environment: str, schema_version: str | None = None) -> dict:
    """Read a JSON manifest from path and validate it for its consumer."""

    try:
        contract_path = _resolve_artifact_path(path)
        with contract_path.open("r", encoding="utf-8") as stream:
            document = json.load(stream)
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ContractError("contract file could not be read as JSON") from error
    return validate_contract(document, producer, environment, schema_version)


def build_contract(
    outputs: dict,
    producer: str,
    environment: str,
    source_commit: str,
    published_at: str | None = None,
    schema_version: str = SCHEMA_VERSION,
) -> dict:
    """Build a validated manifest; v1 remains the default for existing publishers."""

    flat_outputs = _required_mapping(outputs, _PUBLISHER_OUTPUTS_PATH)
    if producer not in PRODUCERS:
        _fail("producer", "unsupported producer")
    _validate_schema_version(schema_version, producer)
    _check_secret_fields(
        flat_outputs,
        _SECRET_REFERENCE_FIELDS[producer],
        _PUBLISHER_OUTPUTS_PATH,
    )
    selected_outputs = {
        field: copy.deepcopy(flat_outputs[field])
        for field in (INGRESS_V2_FIELDS if schema_version == INGRESS_SCHEMA_VERSION else OUTPUT_FIELDS[producer])
        if field in flat_outputs
    }
    timestamp = published_at
    if timestamp is None:
        timestamp = datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")
    document = {
        "schemaVersion": schema_version,
        "environment": environment,
        "producer": producer,
        "sourceCommit": source_commit,
        "publishedAt": timestamp,
        "outputs": selected_outputs,
    }
    return validate_contract(document, producer, environment)


def _read_outputs(path: str) -> dict:
    try:
        outputs_path = _resolve_artifact_path(path)
        with outputs_path.open("r", encoding="utf-8") as stream:
            return _required_mapping(json.load(stream), "outputs")
    except ContractError:
        raise
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ContractError("outputs file could not be read as JSON") from error


def _write_contract(path: str, document: dict) -> None:
    try:
        destination = _resolve_artifact_path(path)
        with destination.open("w", encoding="utf-8", newline="\n") as stream:
            json.dump(document, stream, indent=2, sort_keys=True)
            stream.write("\n")
    except OSError as error:
        raise ContractError("contract file could not be written") from error


def _parser() -> argparse.ArgumentParser:
    parser = _SafeArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    validate = subparsers.add_parser("validate", help="validate a metadata contract")
    validate.add_argument("--file", required=True, help="contract path under the trusted temporary directory")
    validate.add_argument("--producer", required=True)
    validate.add_argument("--environment", required=True)
    validate.add_argument("--schema-version", help="require this exact supported schema version")

    publish = subparsers.add_parser("publish", help="build and write a metadata contract")
    publish.add_argument("--input", required=True, help="outputs path under the trusted temporary directory")
    publish.add_argument("--output", required=True, help="contract path under the trusted temporary directory")
    publish.add_argument("--producer", required=True)
    publish.add_argument("--environment", required=True)
    publish.add_argument("--source-commit", required=True)
    publish.add_argument("--schema-version", default=SCHEMA_VERSION, help="manifest version; defaults to 1.0")
    return parser


def main(arguments: list[str] | None = None) -> int:
    parser = _parser()
    try:
        options = parser.parse_args(arguments)
        if options.command == "validate":
            document = load_contract(options.file, options.producer, options.environment, options.schema_version)
            json.dump(document, sys.stdout, separators=(",", ":"), sort_keys=True)
            sys.stdout.write("\n")
        else:
            outputs = _read_outputs(options.input)
            document = build_contract(
                outputs,
                options.producer,
                options.environment,
                options.source_commit,
                schema_version=options.schema_version,
            )
            _write_contract(options.output, document)
        return 0
    except ContractError as error:
        print(f"infra-contract: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
