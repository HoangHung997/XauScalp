import datetime as dt
import json
import math
import sys
import tempfile
import unittest
from pathlib import Path

TRAINING_DIR = Path(__file__).resolve().parents[1]
if str(TRAINING_DIR) not in sys.path:
    sys.path.insert(0, str(TRAINING_DIR))

import xau_native_baseline as baseline  # noqa: E402


class XauNativeBaselineTests(unittest.TestCase):
    def test_frozen_fixture_matches_offline_inference(self):
        fixture_dir = TRAINING_DIR / "fixtures"
        artifact = json.loads(
            (fixture_dir / "xau-native-fixture.json").read_text(encoding="utf-8")
        )
        vector = json.loads(
            (fixture_dir / "xau-native-parity-vector.json").read_text(
                encoding="utf-8"
            )
        )

        predicted = baseline.offline_predict(artifact, vector["features"])

        self.assertEqual(vector["expected"]["action"], predicted["action"])
        for key, expected in vector["expected"].items():
            if key == "action":
                continue
            self.assertAlmostEqual(expected, predicted[key], places=12)

    def test_temporal_split_never_shuffles_or_splits_equal_timestamp(self):
        start = dt.datetime(2026, 1, 1, tzinfo=dt.timezone.utc)
        rows = []
        for index in range(30):
            timestamp = start + dt.timedelta(seconds=index // 2)
            rows.append(
                self._row(index, timestamp)
            )

        train, validation, oos = baseline.temporal_split(rows)

        self.assertLess(train[-1]["timestampUtc"], validation[0]["timestampUtc"])
        self.assertLess(validation[-1]["timestampUtc"], oos[0]["timestampUtc"])
        combined = train + validation + oos
        self.assertEqual(
            sorted(rows, key=lambda row: (row["timestampUtc"], row["marketStateId"])),
            combined,
        )

    def test_training_is_deterministic_and_has_oos_calibration_report(self):
        start = dt.datetime(2026, 1, 1, tzinfo=dt.timezone.utc)
        rows = [
            self._row(index, start + dt.timedelta(seconds=index))
            for index in range(120)
        ]

        first_artifact, first_report, first_manifest = baseline.train_from_rows(
            rows,
            model_version="unit-v1",
        )
        second_artifact, second_report, second_manifest = baseline.train_from_rows(
            rows,
            model_version="unit-v1",
        )

        self.assertEqual(first_artifact, second_artifact)
        self.assertEqual(first_report, second_report)
        self.assertEqual(first_manifest, second_manifest)

        split = first_report["temporalSplit"]
        self.assertEqual(120, split["trainCount"] + split["validationCount"] + split["oosCount"])
        self.assertEqual(
            "strict-temporal-no-shuffle",
            first_manifest["temporalSplit"]["method"],
        )

        for head_name in baseline.LEARNED_HEADS:
            oos = first_report["heads"][head_name]["oos"]
            self.assertGreater(oos["sampleCount"], 0)
            self.assertTrue(math.isfinite(oos["brier"]))
            self.assertTrue(math.isfinite(oos["logLoss"]))
            self.assertTrue(math.isfinite(oos["ece"]))
            self.assertIn("reliability", oos)

    def test_loader_joins_replay_and_label_exports_by_market_state_id(self):
        state_id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
        replay = {
            "ordinal": 0,
            "featureState": {
                "marketStateId": state_id,
                "timestampUtc": "2026-01-01T00:00:00+00:00",
                "featureSchemaVersion": "xau-features-v1",
                "features": [
                    {
                        "name": "Return1s",
                        "value": 1.0,
                        "isAvailable": True,
                    },
                    {
                        "name": "Velocity1s",
                        "value": 1.0,
                        "isAvailable": True,
                    },
                    {
                        "name": "DecelerationRatio",
                        "value": 0.8,
                        "isAvailable": True,
                    },
                    {
                        "name": "SpreadAtrRatio",
                        "value": 0.1,
                        "isAvailable": True,
                    },
                ],
            },
        }

        def barrier(side, target, outcome):
            return {
                "side": side,
                "targetDistancePrice": target,
                "adverseBarrierPrice": 3.0,
                "outcome": outcome,
            }

        label = {
            "marketStateId": state_id,
            "horizons": [
                {
                    "horizon": "00:05:00",
                    "barrierFirst": [
                        barrier("long", 5.0, "targetFirst"),
                        barrier("short", 5.0, "adverseFirst"),
                        barrier("long", 10.0, "adverseFirst"),
                        barrier("short", 10.0, "targetFirst"),
                    ],
                }
            ],
        }

        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            replay_path = root / "replay.jsonl"
            labels_path = root / "labels.jsonl"
            replay_path.write_text(json.dumps(replay) + "\n", encoding="utf-8")
            labels_path.write_text(json.dumps(label) + "\n", encoding="utf-8")

            rows = baseline.load_joined_rows(
                replay_path,
                labels_path,
                horizon_seconds=300,
                adverse_barrier=3.0,
            )

        self.assertEqual(1, len(rows))
        targets = rows[0]["targets"]
        self.assertEqual(1, targets["up5First"])
        self.assertEqual(0, targets["down5First"])
        self.assertEqual(0, targets["up10First"])
        self.assertEqual(1, targets["down10First"])
        self.assertEqual(0, targets["longAdverseFirst"])
        self.assertEqual(1, targets["shortAdverseFirst"])

    @staticmethod
    def _row(index, timestamp):
        phase = index % 12
        up5 = 1 if phase >= 6 else 0
        down5 = 1 - up5
        up10 = 1 if phase in (8, 9, 10, 11) else 0
        down10 = 1 if phase in (0, 1, 2, 3) else 0

        return {
            "marketStateId": f"{index:032x}",
            "timestampUtc": timestamp,
            "featureSchemaVersion": "xau-features-v1",
            "features": {
                "Return1s": (phase - 5.5) / 5.0,
                "Velocity1s": ((index % 7) - 3) / 3.0,
                "DecelerationRatio": (index % 10) / 9.0,
                "SpreadAtrRatio": 0.05 + ((index % 5) * 0.01),
                "SessionCode": float(index % 3),
                "M1RangeAtrRatio": 0.5 + ((index % 9) * 0.2),
            },
            "targets": {
                "up5First": up5,
                "down5First": down5,
                "up10First": up10,
                "down10First": down10,
                "longAdverseFirst": 1 - up5,
                "shortAdverseFirst": 1 - down5,
            },
        }


if __name__ == "__main__":
    unittest.main()
