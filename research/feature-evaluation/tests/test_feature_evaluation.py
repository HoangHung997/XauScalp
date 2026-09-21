import datetime as dt
import json
import math
import sys
import tempfile
import unittest
from pathlib import Path

EVAL_DIR = Path(__file__).resolve().parents[1]
if str(EVAL_DIR) not in sys.path:
    sys.path.insert(0, str(EVAL_DIR))

import evaluate_features as evaluation  # noqa: E402


class FeatureEvaluationTests(unittest.TestCase):
    def test_temporal_split_never_splits_equal_timestamp(self):
        start = dt.datetime(2026, 1, 1, tzinfo=dt.timezone.utc)
        rows = [
            self._row(
                index,
                start + dt.timedelta(seconds=index // 2),
            )
            for index in range(80)
        ]

        train, validation, oos = evaluation.temporal_split(rows)

        self.assertLess(train[-1]["timestampUtc"], validation[0]["timestampUtc"])
        self.assertLess(validation[-1]["timestampUtc"], oos[0]["timestampUtc"])

        combined = train + validation + oos
        expected = sorted(
            rows,
            key=lambda row: (row["timestampUtc"], row["marketStateId"]),
        )
        self.assertEqual(expected, combined)

    def test_evaluation_is_deterministic_and_detects_microstructure_signal(self):
        start = dt.datetime(2026, 1, 1, tzinfo=dt.timezone.utc)
        rows = [
            self._row(index, start + dt.timedelta(seconds=index))
            for index in range(90)
        ]

        first = evaluation.evaluate_rows(rows)
        second = evaluation.evaluate_rows(rows)

        self.assertEqual(first, second)
        self.assertEqual(
            "strict-temporal-no-shuffle",
            first["method"]["split"],
        )

        micro = first["candidateStatusRegistry"]["microstructure"]
        self.assertIn(
            micro["status"],
            {"Supported", "Rejected", "Inconclusive"},
        )
        self.assertGreater(micro["addOneBrierImprovement"], 0)
        self.assertGreater(micro["removeOneBrierDamage"], 0)
        self.assertEqual("Supported", micro["status"])

        correlations = first["correlation"]["redundantAbsCorrelationGte090"]
        self.assertTrue(
            any(
                {item["left"], item["right"]}
                == {"Return1s", "Velocity1s"}
                for item in correlations
            )
        )

        for family, status in first["candidateStatusRegistry"].items():
            self.assertIn(
                status["status"],
                {"Supported", "Rejected", "Inconclusive"},
                family,
            )

    def test_breakdowns_include_sample_counts_and_calibration_metrics(self):
        start = dt.datetime(2026, 1, 1, tzinfo=dt.timezone.utc)
        rows = [
            self._row(index, start + dt.timedelta(seconds=index))
            for index in range(90)
        ]

        report = evaluation.evaluate_rows(rows)
        baseline = report["breakdowns"]["baselineOos"]

        self.assertGreater(baseline["sampleCount"], 0)
        self.assertTrue(math.isfinite(baseline["brier"]))
        self.assertTrue(math.isfinite(baseline["logLoss"]))
        self.assertTrue(math.isfinite(baseline["ece"]))
        self.assertEqual(2, len(baseline["targetRateWilson95"]))
        self.assertIn("reliability", baseline)
        self.assertIn("session", report["breakdowns"])
        self.assertIn("regime", report["breakdowns"])
        self.assertIn("spread", report["breakdowns"])

    def test_entry_modes_require_common_cost_scenario_and_all_five_modes(self):
        outcomes = []
        for index, mode in enumerate(evaluation.REQUIRED_ENTRY_MODES):
            outcomes.append(
                {
                    "costScenarioName": "baseline",
                    "key": {"mode": mode},
                    "outcome": "targetFirst" if index % 2 == 0 else "adverseFirst",
                    "netResultPrice": 1.0 if index % 2 == 0 else -0.5,
                }
            )

        summary = evaluation.summarize_entry_modes(outcomes)

        self.assertEqual("complete-five-mode-comparison", summary["status"])
        self.assertEqual("baseline", summary["costScenarioName"])
        self.assertEqual([], summary["missingModes"])
        self.assertEqual(
            set(evaluation.REQUIRED_ENTRY_MODES),
            set(summary["modes"]),
        )

        mixed = [dict(item) for item in outcomes]
        mixed[-1]["costScenarioName"] = "stressed"
        with self.assertRaises(ValueError):
            evaluation.summarize_entry_modes(mixed)

    def test_loader_joins_future_labels_without_putting_them_in_features(self):
        state_id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
        replay = {
            "ordinal": 1,
            "featureState": {
                "marketStateId": state_id,
                "timestampUtc": "2026-01-01T00:00:00+00:00",
                "features": [
                    {
                        "name": "SpreadAtrRatio",
                        "value": 0.1,
                        "isAvailable": True,
                    },
                    {
                        "name": "AtrRatioM1",
                        "value": 1.0,
                        "isAvailable": True,
                    },
                ],
            },
        }
        label = {
            "marketStateId": state_id,
            "horizons": [
                {
                    "horizon": "00:05:00",
                    "barrierFirst": [
                        {
                            "side": "long",
                            "targetDistancePrice": 5.0,
                            "adverseBarrierPrice": 3.0,
                            "outcome": "targetFirst",
                        }
                    ],
                }
            ],
        }

        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            replay_path = root / "replay.jsonl"
            label_path = root / "labels.jsonl"
            replay_path.write_text(json.dumps(replay) + "\n", encoding="utf-8")
            label_path.write_text(json.dumps(label) + "\n", encoding="utf-8")

            rows, stats = evaluation.load_joined_rows(
                replay_path,
                label_path,
            )

        self.assertEqual(1, len(rows))
        self.assertEqual(1, rows[0]["target"])
        self.assertNotIn("target", rows[0]["features"])
        self.assertEqual(1, stats["joinedSupervisedRows"])

    @staticmethod
    def _row(index, timestamp):
        phase = (index % 20) - 10
        velocity = phase / 4.0
        target = 1 if velocity > 0 else 0
        independent = ((index * 7) % 17) / 17.0
        regime = 0.7 + (((index * 5) % 13) / 13.0)
        spread = 0.05 + (((index * 3) % 8) * 0.03)

        features = {
            "SpreadAtrRatio": spread,
            "AtrRatioM1": regime,
            "SessionCode": float(index % 3),
            "Return1s": velocity * 0.8,
            "Velocity1s": velocity,
            "Acceleration1s": velocity * 0.4,
            "DecelerationRatio": max(0.0, min(1.0, 0.5 - (velocity / 8.0))),
            "DirectionFlipAgeMs": float((index % 10) * 100),
            "BurstZScore": velocity * 0.3,
            "SweepDepthAtr": independent,
            "CloseBackInsideAtr": independent * 0.5,
            "DirectionFlipAfterTouch": float(index % 2),
            "MicroRetestOccurred": float((index // 2) % 2),
            "ResumeVelocity": independent - 0.5,
            "UpperMagnetDistanceAtr": 0.5 + independent,
            "LowerMagnetDistanceAtr": 0.7 + (1 - independent),
            "PullDelta": independent - 0.5,
            "InsideVacuum": float(index % 4 == 0),
            "AbsorptionUpScore": independent,
            "AbsorptionDownScore": 1 - independent,
            "BosDirection": float((index % 3) - 1),
            "FvgSizeAtr": 0.2 + independent,
            "FvgFillPct": independent * 100,
            "DisplacementRangeAtr": 0.5 + independent,
            "OrderBlockOverlapPct": (1 - independent) * 100,
            "AdxM1": 10 + (independent * 30),
            "M1CompressionScore": independent,
            "EmaSlopeM5": independent - 0.5,
            "TrendAlignmentScore": (independent * 2) - 1,
            "HtfConflict": float(index % 5 == 0),
        }

        return {
            "marketStateId": f"{index:032x}",
            "timestampUtc": timestamp,
            "features": features,
            "target": target,
        }


if __name__ == "__main__":
    unittest.main()
