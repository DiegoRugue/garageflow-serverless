#!/usr/bin/env python3
"""Validate deployment inputs and emit serverless Terraform variables."""

from __future__ import annotations

import argparse
import copy
import json
import os
import re
import sys
import tempfile
from datetime import date
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parent))
from infra_contract import ContractError, validate_contract  # noqa: E402


AWS_REGION = "us-east-1"
ENVIRONMENTS = ("homologation", "production")
_ACCOUNT_PATTERN = re.compile(r"^[0-9]{12}$")
_ROLE_ARN_PATTERN = re.compile(
    r"^arn:aws:iam::(?P<account>[0-9]{12}):role/(?P<name>[A-Za-z0-9+=,.@_/-]{1,512})$"
)
_CLEAN_TAG_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9 _.:/@+=,-]{0,127}$")
_ARN_PATTERN = re.compile(
    r"^arn:aws:(?P<service>[a-z0-9-]+):(?P<region>[a-z0-9-]*):(?P<account>[0-9]{12}):\S+$"
)


class TerraformVariablesError(ValueError):
    """Raised when protected deployment inputs cannot produce safe tfvars."""


def _fail(reason: str) -> None:
    raise TerraformVariablesError(reason)


def _trusted_path(path, *, must_exist: bool) -> Path:
    base = Path(os.environ.get("RUNNER_TEMP", tempfile.gettempdir()))
    try:
        trusted_base = base.resolve(strict=True)
        supplied = Path(path)
        candidate = supplied if supplied.is_absolute() else trusted_base / supplied
        resolved = candidate.resolve(strict=must_exist)
        resolved.relative_to(trusted_base)
    except (OSError, RuntimeError, TypeError, ValueError) as error:
        raise TerraformVariablesError("artifact path must stay within the trusted temporary directory") from error
    if resolved == trusted_base:
        _fail("artifact path must stay within the trusted temporary directory")
    return resolved


def _validate_account(value: object) -> str:
    if not isinstance(value, str) or _ACCOUNT_PATTERN.fullmatch(value) is None:
        _fail("AWS account ID must contain exactly 12 digits")
    return value


def _validate_role(value: object, account_id: str, label: str) -> str:
    if not isinstance(value, str):
        _fail(f"{label} must be an IAM role ARN")
    match = _ROLE_ARN_PATTERN.fullmatch(value)
    if match is None or match.group("account") != account_id:
        _fail(f"{label} must be an IAM role ARN in the protected AWS account")
    return value


def _validate_public_tag(value: object, label: str) -> str:
    if not isinstance(value, str) or _CLEAN_TAG_PATTERN.fullmatch(value) is None:
        _fail(f"{label} must be a clean AWS tag value")
    return value


def _validate_expiry(value: object) -> str:
    if not isinstance(value, str):
        _fail("expires_on must use YYYY-MM-DD")
    try:
        parsed = date.fromisoformat(value)
    except ValueError as error:
        raise TerraformVariablesError("expires_on must use YYYY-MM-DD") from error
    if parsed.isoformat() != value:
        _fail("expires_on must use YYYY-MM-DD")
    return value


def _walk_strings(value):
    if isinstance(value, str):
        yield value
    elif type(value) is list:
        for child in value:
            yield from _walk_strings(child)
    elif type(value) is dict:
        for child in value.values():
            yield from _walk_strings(child)


def _validate_contract_identity(document: dict, account_id: str) -> None:
    for value in _walk_strings(document):
        if not value.startswith("arn:"):
            continue
        match = _ARN_PATTERN.fullmatch(value)
        if match is None or match.group("account") != account_id:
            _fail("every contract ARN must belong to the protected AWS account")
        if match.group("region") and match.group("region") != AWS_REGION:
            _fail("every regional contract ARN must use us-east-1")


def build_tfvars(
    *,
    platform_document: dict,
    ingress_document: dict,
    environment: str,
    aws_account_id: str,
    owner: str,
    expires_on: str,
    authentication_role_arn: str,
    authorizer_role_arn: str,
    package_path,
) -> dict:
    """Return exact Terraform inputs after validating both contracts and protected values."""

    if environment not in ENVIRONMENTS:
        _fail("environment must be homologation or production")
    account_id = _validate_account(aws_account_id)
    try:
        platform = validate_contract(platform_document, "platform", environment, "1.0")
        ingress = validate_contract(ingress_document, "ingress", environment, "2.0")
    except ContractError as error:
        raise TerraformVariablesError(str(error)) from error

    if platform["outputs"]["awsRegion"] != AWS_REGION:
        _fail("platform contract must target us-east-1")
    expected_ecr_prefix = f"{account_id}.dkr.ecr.{AWS_REGION}.amazonaws.com/"
    if not platform["outputs"]["ecrRepositoryUrl"].startswith(expected_ecr_prefix):
        _fail("platform ECR repository must belong to the protected account and region")
    _validate_contract_identity(platform, account_id)
    _validate_contract_identity(ingress, account_id)

    resolved_package = _trusted_path(package_path, must_exist=True)
    if not resolved_package.is_file() or resolved_package.suffix.lower() != ".zip":
        _fail("package_path must identify an existing ZIP file")

    return {
        "environment": environment,
        "owner": _validate_public_tag(owner, "owner"),
        "expires_on": _validate_expiry(expires_on),
        "platform_contract": copy.deepcopy(platform["outputs"]),
        "ingress_contract": copy.deepcopy(ingress["outputs"]),
        "authentication_role_arn": _validate_role(
            authentication_role_arn, account_id, "authentication_role_arn"
        ),
        "authorizer_role_arn": _validate_role(authorizer_role_arn, account_id, "authorizer_role_arn"),
        "package_path": str(resolved_package),
    }


def _read_json(path, producer: str) -> dict:
    resolved = _trusted_path(path, must_exist=True)
    try:
        with resolved.open("r", encoding="utf-8") as stream:
            value = json.load(stream)
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise TerraformVariablesError(f"{producer} contract could not be read as JSON") from error
    if type(value) is not dict:
        _fail(f"{producer} contract must be an object")
    return value


def _write_json(path, value: dict) -> None:
    resolved = _trusted_path(path, must_exist=False)
    try:
        with resolved.open("x", encoding="utf-8", newline="\n") as stream:
            json.dump(value, stream, indent=2, sort_keys=True)
            stream.write("\n")
    except OSError as error:
        raise TerraformVariablesError("Terraform variables file could not be created") from error


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform-contract", required=True)
    parser.add_argument("--ingress-contract", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--environment", required=True)
    parser.add_argument("--aws-account-id", required=True)
    parser.add_argument("--owner", required=True)
    parser.add_argument("--expires-on", required=True)
    parser.add_argument("--authentication-role-arn", required=True)
    parser.add_argument("--authorizer-role-arn", required=True)
    parser.add_argument("--package-path", required=True)
    return parser


def main(arguments: list[str] | None = None) -> int:
    options = _parser().parse_args(arguments)
    try:
        variables = build_tfvars(
            platform_document=_read_json(options.platform_contract, "platform"),
            ingress_document=_read_json(options.ingress_contract, "ingress"),
            environment=options.environment,
            aws_account_id=options.aws_account_id,
            owner=options.owner,
            expires_on=options.expires_on,
            authentication_role_arn=options.authentication_role_arn,
            authorizer_role_arn=options.authorizer_role_arn,
            package_path=options.package_path,
        )
        _write_json(options.output, variables)
        return 0
    except TerraformVariablesError as error:
        print(f"serverless-tfvars: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
