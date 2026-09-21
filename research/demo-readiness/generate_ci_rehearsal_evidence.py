#!/usr/bin/env python3
"""Generate and validate XSP-017 demo-readiness evidence without inventing broker evidence."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
from pathlib import Path
from typing import Any

EVIDENCE_PACK_VERSION = "xsp017-evidence-v1"
BROKER_DEMO_CLASS = "BROKER_DEMO"
CI_REHEARSAL_CLASS = "CI_REHEARSAL_NOT_BROKER_DEMO"

REQUIRED_DEMO_FIELDS = (
    "datasetId",
    "datasetSha256",
    "replayRunId",
    "replayOutputSha256",
    "brokerSymbol",
    "dataSourceId",
    "demoExecutionId",
    "capturedAtUtc",
    "tickCount",
    "featureParityPassed",
    "replayDeterminismPassed",
    "primaryShadowPassed",
    "riskPassed",
    "restartReconnectPassed",
    "modelOutagePassed",
    "staleDataPassed",
)


def _read_json(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError("evidence manifest must be a JSON object")
    return value


def _required_text(value: Any, field: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{field} must be a non-empty string")
    return value.strip()


def _sha256(value: Any, field: str) -> str:
    text = _required_text(value, field).lower()
    if len(text) != 64 or any(char not in "0123456789abcdef" for char in text):
        raise ValueError(f"{field} must be a 64-character lowercase/uppercase SHA-256 hex string")
    return text


def validate_broker_demo_manifest(
    manifest: dict[str, Any],
) -> dict[str, Any]:
    missing = [
        field for field in REQUIRED_DEMO_FIELDS
        if field not in manifest
    ]
    if missing:
        raise ValueError(
            "broker demo manifest missing required fields: "
            + ", ".join(missing)
        )

    if manifest.get("evidenceClass") != BROKER_DEMO_CLASS:
        raise ValueError(
            f"evidenceClass must be {BROKER_DEMO_CLASS}"
        )

    if manifest.get("liveMoneyEnabled") is not False:
        raise ValueError(
            "broker demo evidence must explicitly state liveMoneyEnabled=false"
        )

    _required_text(manifest["datasetId"], "datasetId")
    _sha256(manifest["datasetSha256"], "datasetSha256")
    _required_text(manifest["replayRunId"], "replayRunId")
    _sha256(manifest["replayOutputSha256"], "replayOutputSha256")
    _required_text(manifest["brokerSymbol"], "brokerSymbol")
    _required_text(manifest["dataSourceId"], "dataSourceId")
    _required_text(manifest["demoExecutionId"], "demoExecutionId")

    captured = _required_text(manifest["capturedAtUtc"], "capturedAtUtc")
    timestamp = dt.datetime.fromisoformat(captured.replace("Z", "+00:00"))
    if timestamp.tzinfo is None:
        raise ValueError("capturedAtUtc must include UTC/timezone information")

    tick_count = manifest["tickCount"]
    if not isinstance(tick_count, int) or isinstance(tick_count, bool) or tick_count <= 0:
        raise ValueError("tickCount must be a positive integer")

    pass_fields = (
        "featureParityPassed",
        "replayDeterminismPassed",
        "primaryShadowPassed",
        "riskPassed",
        "restartReconnectPassed",
        "modelOutagePassed",
        "staleDataPassed",
    )
    failed = [
        field for field in pass_fields
        if manifest.get(field) is not True
    ]
    if failed:
        raise ValueError(
            "broker demo manifest contains failed/unproven gates: "
            + ", ".join(failed)
        )

    forbidden_names = (
        "password",
        "apikey",
        "api_key",
        "secret",
        "token",
        "credential",
    )

    def walk(value: Any, path: str = "") -> None:
        if isinstance(value, dict):
            for key, child in value.items():
                normalized = str(key).replace("-", "").replace("_", "").lower()
                if any(
                    forbidden.replace("_", "") in normalized
                    for forbidden in forbidden_names
                ):
                    raise ValueError(
                        f"credentials/secrets are forbidden in evidence manifests: {path}{key}"
                    )
                walk(child, f"{path}{key}.")
        elif isinstance(value, list):
            for index, child in enumerate(value):
                walk(child, f"{path}{index}.")

    walk(manifest)
    return manifest


def build_ci_rehearsal_evidence(
    external_demo_manifest: dict[str, Any] | None = None,
) -> dict[str, Any]:
    repository = os.environ.get(
        "GITHUB_REPOSITORY",
        "HoangHung997/XauScalp",
    )
    run_id = os.environ.get("GITHUB_RUN_ID")
    sha = os.environ.get("GITHUB_SHA", "unknown")
    run_url = (
        f"https://github.com/{repository}/actions/runs/{run_id}"
        if run_id
        else None
    )

    external: dict[str, Any]
    if external_demo_manifest is None:
        external = {
            "status": "missing",
            "requiredEvidenceClass": BROKER_DEMO_CLASS,
            "reason": (
                "No validated MT5 broker-demo evidence manifest was supplied. "
                "CI/fake-provider/fake-broker results are rehearsal only."
            ),
        }
        readiness = "blocked-external-broker-demo-evidence"
    else:
        validated = validate_broker_demo_manifest(
            external_demo_manifest
        )
        external = {
            "status": "validated",
            "evidenceClass": validated["evidenceClass"],
            "datasetId": validated["datasetId"],
            "datasetSha256": validated["datasetSha256"],
            "replayRunId": validated["replayRunId"],
            "replayOutputSha256": validated["replayOutputSha256"],
            "brokerSymbol": validated["brokerSymbol"],
            "dataSourceId": validated["dataSourceId"],
            "demoExecutionId": validated["demoExecutionId"],
            "capturedAtUtc": validated["capturedAtUtc"],
            "tickCount": validated["tickCount"],
        }
        readiness = "ready-for-human-demo-review"

    return {
        "evidencePackVersion": EVIDENCE_PACK_VERSION,
        "evidenceClass": CI_REHEARSAL_CLASS,
        "repository": repository,
        "codeCommit": sha,
        "ciRunId": run_id,
        "ciRunUrl": run_url,
        "softwareGate": {
            "status": "pass-if-ci-job-succeeds",
            "checks": [
                "ordered-market-event-record-and-replay-rehearsal",
                "deterministic-replay-and-feature-parity",
                "primary-shadow-authority-isolation",
                "jev-model-outage-stop-or-explicit-fallback",
                "hard-risk-healthy-and-stale-state-drills",
                "execution-submit-restart-reconnect-no-duplicate",
                "live-money-remains-disabled",
            ],
        },
        "liveMoneyAuthorized": False,
        "externalBrokerDemoEvidence": external,
        "demoReadiness": readiness,
        "promotionRule": (
            "Do not mark XSP-017 complete or authorize live money until "
            "externalBrokerDemoEvidence.status == validated and a human reviews "
            "the evidence pack."
        ),
    }


def write_json(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(value, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--external-demo-manifest", type=Path)
    parser.add_argument(
        "--require-external",
        action="store_true",
        help="fail when no validated external broker-demo manifest is supplied",
    )
    return parser.parse_args()


def main() -> int:
    args = _parse_args()
    external = (
        validate_broker_demo_manifest(
            _read_json(args.external_demo_manifest)
        )
        if args.external_demo_manifest
        else None
    )
    evidence = build_ci_rehearsal_evidence(external)
    write_json(args.output, evidence)

    print(
        json.dumps(
            {
                "output": str(args.output),
                "demoReadiness": evidence["demoReadiness"],
                "externalStatus": evidence[
                    "externalBrokerDemoEvidence"
                ]["status"],
            }
        )
    )

    if (
        args.require_external
        and evidence["externalBrokerDemoEvidence"]["status"]
        != "validated"
    ):
        return 2

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
