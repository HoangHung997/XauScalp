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
    "codeCommit",
    "ciRunUrl",
    "canonicalSymbol",
    "datasetId",
    "datasetSha256",
    "replayRunId",
    "replayOutputSha256",
    "brokerSymbol",
    "dataSourceId",
    "demoExecutionId",
    "mt5MarketBridgeEx5Sha256",
    "mt5DemoExecutionBridgeEx5Sha256",
    "mt5BridgesCompiledPassed",
    "capturedAtUtc",
    "tickCount",
    "jevLatencyP95Ms",
    "xauNativeLatencyP95Ms",
    "costAssumptions",
    "knownLimitations",
    "unresolvedP0P1CorrectnessIssues",
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

    code_commit = _required_text(manifest["codeCommit"], "codeCommit").lower()
    if len(code_commit) != 40 or any(
        char not in "0123456789abcdef" for char in code_commit
    ):
        raise ValueError("codeCommit must be a 40-character git SHA hex string")

    ci_run_url = _required_text(manifest["ciRunUrl"], "ciRunUrl")
    if not ci_run_url.startswith("https://github.com/"):
        raise ValueError("ciRunUrl must be a GitHub Actions URL")

    canonical_symbol = _required_text(
        manifest["canonicalSymbol"],
        "canonicalSymbol",
    )
    if canonical_symbol != "XAUUSD":
        raise ValueError("canonicalSymbol must be XAUUSD for XSP-017")

    dataset_sha = _sha256(manifest["datasetSha256"], "datasetSha256")
    dataset_id = _required_text(manifest["datasetId"], "datasetId")
    if dataset_id.lower() != f"sha256:{dataset_sha}":
        raise ValueError("datasetId must equal sha256:<datasetSha256>")

    _required_text(manifest["replayRunId"], "replayRunId")
    _sha256(manifest["replayOutputSha256"], "replayOutputSha256")
    _required_text(manifest["brokerSymbol"], "brokerSymbol")
    _required_text(manifest["dataSourceId"], "dataSourceId")
    _required_text(manifest["demoExecutionId"], "demoExecutionId")
    _sha256(
        manifest["mt5MarketBridgeEx5Sha256"],
        "mt5MarketBridgeEx5Sha256",
    )
    _sha256(
        manifest["mt5DemoExecutionBridgeEx5Sha256"],
        "mt5DemoExecutionBridgeEx5Sha256",
    )
    if manifest["mt5BridgesCompiledPassed"] is not True:
        raise ValueError(
            "mt5BridgesCompiledPassed must be true for broker-demo evidence"
        )

    captured = _required_text(manifest["capturedAtUtc"], "capturedAtUtc")
    timestamp = dt.datetime.fromisoformat(captured.replace("Z", "+00:00"))
    if timestamp.tzinfo is None or timestamp.utcoffset() != dt.timedelta(0):
        raise ValueError("capturedAtUtc must be explicitly UTC")

    tick_count = manifest["tickCount"]
    if not isinstance(tick_count, int) or isinstance(tick_count, bool) or tick_count <= 0:
        raise ValueError("tickCount must be a positive integer")

    for field in ("jevLatencyP95Ms", "xauNativeLatencyP95Ms"):
        value = manifest[field]
        if (
            isinstance(value, bool)
            or not isinstance(value, (int, float))
            or value < 0
        ):
            raise ValueError(f"{field} must be a non-negative number")

    cost = manifest["costAssumptions"]
    if not isinstance(cost, dict):
        raise ValueError("costAssumptions must be an object")
    for field in ("slippagePoints", "commissionPerLot", "latencyMs"):
        value = cost.get(field)
        if (
            isinstance(value, bool)
            or not isinstance(value, (int, float))
            or value < 0
        ):
            raise ValueError(
                f"costAssumptions.{field} must be a non-negative number"
            )

    known_limitations = manifest["knownLimitations"]
    if not isinstance(known_limitations, list) or any(
        not isinstance(item, str) or not item.strip()
        for item in known_limitations
    ):
        raise ValueError("knownLimitations must be a list of non-empty strings")

    unresolved = manifest["unresolvedP0P1CorrectnessIssues"]
    if not isinstance(unresolved, list):
        raise ValueError(
            "unresolvedP0P1CorrectnessIssues must be a list"
        )
    if unresolved:
        raise ValueError(
            "unresolved P0/P1 correctness issues must be empty before demo readiness"
        )

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
            "codeCommit": validated["codeCommit"],
            "ciRunUrl": validated["ciRunUrl"],
            "canonicalSymbol": validated["canonicalSymbol"],
            "datasetId": validated["datasetId"],
            "datasetSha256": validated["datasetSha256"],
            "replayRunId": validated["replayRunId"],
            "replayOutputSha256": validated["replayOutputSha256"],
            "brokerSymbol": validated["brokerSymbol"],
            "dataSourceId": validated["dataSourceId"],
            "demoExecutionId": validated["demoExecutionId"],
            "mt5MarketBridgeEx5Sha256":
                validated["mt5MarketBridgeEx5Sha256"],
            "mt5DemoExecutionBridgeEx5Sha256":
                validated["mt5DemoExecutionBridgeEx5Sha256"],
            "mt5BridgesCompiledPassed":
                validated["mt5BridgesCompiledPassed"],
            "capturedAtUtc": validated["capturedAtUtc"],
            "tickCount": validated["tickCount"],
            "jevLatencyP95Ms": validated["jevLatencyP95Ms"],
            "xauNativeLatencyP95Ms": validated["xauNativeLatencyP95Ms"],
            "costAssumptions": validated["costAssumptions"],
            "knownLimitations": validated["knownLimitations"],
            "unresolvedP0P1CorrectnessIssues":
                validated["unresolvedP0P1CorrectnessIssues"],
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
