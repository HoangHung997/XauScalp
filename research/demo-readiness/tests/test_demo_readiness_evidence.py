import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

import generate_ci_rehearsal_evidence as evidence  # noqa: E402


class DemoReadinessEvidenceTests(unittest.TestCase):
    def test_missing_external_evidence_fails_closed_without_fake_demo_claim(self):
        pack = evidence.build_ci_rehearsal_evidence()

        self.assertEqual(
            evidence.CI_REHEARSAL_CLASS,
            pack["evidenceClass"],
        )
        self.assertFalse(pack["liveMoneyAuthorized"])
        self.assertEqual(
            "missing",
            pack["externalBrokerDemoEvidence"]["status"],
        )
        self.assertEqual(
            "blocked-external-broker-demo-evidence",
            pack["demoReadiness"],
        )
        self.assertNotEqual(
            evidence.BROKER_DEMO_CLASS,
            pack["evidenceClass"],
        )

    def test_valid_broker_demo_manifest_is_accepted_without_credentials(self):
        manifest = self._manifest()

        validated = evidence.validate_broker_demo_manifest(manifest)
        pack = evidence.build_ci_rehearsal_evidence(validated)

        self.assertEqual(
            "validated",
            pack["externalBrokerDemoEvidence"]["status"],
        )
        self.assertEqual(
            "ready-for-human-demo-review",
            pack["demoReadiness"],
        )
        self.assertFalse(pack["liveMoneyAuthorized"])

    def test_failed_drill_is_rejected(self):
        manifest = self._manifest()
        manifest["staleDataPassed"] = False

        with self.assertRaisesRegex(
            ValueError,
            "failed/unproven gates",
        ):
            evidence.validate_broker_demo_manifest(manifest)

    def test_secrets_are_rejected_from_evidence(self):
        manifest = self._manifest()
        manifest["accountPassword"] = "must-not-be-here"

        with self.assertRaisesRegex(
            ValueError,
            "credentials/secrets",
        ):
            evidence.validate_broker_demo_manifest(manifest)

    def test_dataset_identity_must_match_hash(self):
        manifest = self._manifest()
        manifest["datasetId"] = "sha256:" + ("3" * 64)

        with self.assertRaisesRegex(
            ValueError,
            "datasetId must equal",
        ):
            evidence.validate_broker_demo_manifest(manifest)

    def test_missing_compiled_bridge_proof_is_rejected(self):
        manifest = self._manifest()
        manifest["mt5BridgesCompiledPassed"] = False

        with self.assertRaisesRegex(
            ValueError,
            "mt5BridgesCompiledPassed",
        ):
            evidence.validate_broker_demo_manifest(manifest)

    def test_non_utc_capture_time_is_rejected(self):
        manifest = self._manifest()
        manifest["capturedAtUtc"] = "2026-09-21T07:00:00+07:00"

        with self.assertRaisesRegex(ValueError, "explicitly UTC"):
            evidence.validate_broker_demo_manifest(manifest)

    def test_unresolved_p0_p1_issue_is_rejected(self):
        manifest = self._manifest()
        manifest["unresolvedP0P1CorrectnessIssues"] = [
            "example correctness blocker"
        ]

        with self.assertRaisesRegex(
            ValueError,
            "unresolved P0/P1",
        ):
            evidence.validate_broker_demo_manifest(manifest)

    def test_require_external_returns_nonzero_when_manifest_missing(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "evidence.json"
            result = subprocess.run(
                [
                    sys.executable,
                    str(ROOT / "generate_ci_rehearsal_evidence.py"),
                    "--output",
                    str(output),
                    "--require-external",
                ],
                check=False,
                capture_output=True,
                text=True,
                env={
                    **os.environ,
                    "GITHUB_REPOSITORY": "HoangHung997/XauScalp",
                    "GITHUB_SHA": "abc123",
                },
            )

            self.assertEqual(2, result.returncode)
            pack = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(
                "blocked-external-broker-demo-evidence",
                pack["demoReadiness"],
            )

    @staticmethod
    def _manifest():
        return {
            "evidenceClass": "BROKER_DEMO",
            "liveMoneyEnabled": False,
            "codeCommit": "a" * 40,
            "ciRunUrl": (
                "https://github.com/HoangHung997/XauScalp/actions/runs/123"
            ),
            "canonicalSymbol": "XAUUSD",
            "datasetId": "sha256:" + ("1" * 64),
            "datasetSha256": "1" * 64,
            "replayRunId": "replay-demo-001",
            "replayOutputSha256": "2" * 64,
            "brokerSymbol": "XAUUSD.G",
            "dataSourceId": "mt5-demo",
            "demoExecutionId": "demo-exec-001",
            "mt5MarketBridgeEx5Sha256": "4" * 64,
            "mt5DemoExecutionBridgeEx5Sha256": "5" * 64,
            "mt5BridgesCompiledPassed": True,
            "capturedAtUtc": "2026-09-21T00:00:00Z",
            "tickCount": 1000,
            "jevLatencyP95Ms": 45.0,
            "xauNativeLatencyP95Ms": 2.5,
            "costAssumptions": {
                "slippagePoints": 3.0,
                "commissionPerLot": 7.5,
                "latencyMs": 12.0,
            },
            "knownLimitations": [
                "Demo broker evidence only; live money remains disabled."
            ],
            "unresolvedP0P1CorrectnessIssues": [],
            "featureParityPassed": True,
            "replayDeterminismPassed": True,
            "primaryShadowPassed": True,
            "riskPassed": True,
            "restartReconnectPassed": True,
            "modelOutagePassed": True,
            "staleDataPassed": True,
        }


if __name__ == "__main__":
    unittest.main()
