#!/usr/bin/env python3
"""Deterministic XAU Native V0 baseline trainer.

Production inference does not depend on Python. This module joins causal replay
feature snapshots with separately generated first-passage labels, performs a
strict temporal split, trains calibrated logistic heads, emits a versioned JSON
artifact, and writes an OOS/calibration evidence report.

No random shuffle is used for time-series claims.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import math
from pathlib import Path
from typing import Any, Iterable

TRAINER_VERSION = "xau-native-baseline-trainer-v1"
ARTIFACT_FORMAT_VERSION = "xau-native-linear-v1"
ARTIFACT_MANIFEST_VERSION = "xau-native-artifact-manifest-v1"

LEARNED_HEADS = (
    "up5First",
    "down5First",
    "up10First",
    "down10First",
    "longAdverseFirst",
    "shortAdverseFirst",
)
CONSTANT_HEADS = (
    "continuation",
    "reversal",
    "falseBreak",
)
DEFAULT_FEATURES = (
    "Return1s",
    "Velocity1s",
    "DecelerationRatio",
    "SpreadAtrRatio",
)


def _finite(value: Any) -> bool:
    return isinstance(value, (int, float)) and math.isfinite(float(value))


def _sigmoid(value: float) -> float:
    value = max(-40.0, min(40.0, value))
    return 1.0 / (1.0 + math.exp(-value))


def _parse_utc(value: str) -> dt.datetime:
    parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError(f"timestamp must contain an offset: {value}")
    return parsed.astimezone(dt.timezone.utc)


def _timespan_seconds(value: str) -> float:
    # System.Text.Json TimeSpan format is normally d.hh:mm:ss.fffffff or hh:mm:ss.
    day_part = 0
    time_part = value
    if "." in value and value.index(".") < value.index(":"):
        day_text, time_part = value.split(".", 1)
        day_part = int(day_text)

    pieces = time_part.split(":")
    if len(pieces) != 3:
        raise ValueError(f"unsupported TimeSpan value: {value}")

    hours = int(pieces[0])
    minutes = int(pieces[1])
    seconds = float(pieces[2])
    return (day_part * 86400) + (hours * 3600) + (minutes * 60) + seconds


def _jsonl(path: Path) -> Iterable[dict[str, Any]]:
    with path.open("r", encoding="utf-8") as handle:
        for line_number, line in enumerate(handle, start=1):
            stripped = line.strip()
            if not stripped:
                continue
            try:
                value = json.loads(stripped)
            except json.JSONDecodeError as exc:
                raise ValueError(f"invalid JSONL at {path}:{line_number}") from exc
            if not isinstance(value, dict):
                raise ValueError(f"expected object JSONL at {path}:{line_number}")
            yield value


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _flatten_available_features(feature_state: dict[str, Any]) -> dict[str, float]:
    result: dict[str, float] = {}
    for feature in feature_state.get("features", []):
        if (
            feature.get("isAvailable") is True
            and _finite(feature.get("value"))
            and isinstance(feature.get("name"), str)
        ):
            result[feature["name"]] = float(feature["value"])
    return result


def _find_horizon(
    horizons: list[dict[str, Any]],
    horizon_seconds: float,
) -> dict[str, Any] | None:
    for horizon in horizons:
        raw = horizon.get("horizon")
        if isinstance(raw, str) and abs(_timespan_seconds(raw) - horizon_seconds) < 1e-9:
            return horizon
    return None


def _barrier_binary_target(
    horizon: dict[str, Any],
    side: str,
    target_distance: float,
    adverse_barrier: float,
    want_adverse: bool,
) -> int | None:
    for label in horizon.get("barrierFirst", []):
        if (
            label.get("side") == side
            and abs(float(label.get("targetDistancePrice", math.nan)) - target_distance) < 1e-9
            and abs(float(label.get("adverseBarrierPrice", math.nan)) - adverse_barrier) < 1e-9
        ):
            outcome = label.get("outcome")
            if outcome == "censored":
                return None
            if outcome not in ("targetFirst", "adverseFirst"):
                raise ValueError(f"unknown first-passage outcome: {outcome}")
            if want_adverse:
                return int(outcome == "adverseFirst")
            return int(outcome == "targetFirst")
    return None


def load_joined_rows(
    replay_jsonl: Path,
    labels_jsonl: Path,
    *,
    horizon_seconds: float = 300.0,
    adverse_barrier: float = 3.0,
) -> list[dict[str, Any]]:
    """Join replay feature states to future labels by immutable MarketStateId."""

    states: dict[str, dict[str, Any]] = {}
    for record in _jsonl(replay_jsonl):
        state = record.get("featureState")
        if not isinstance(state, dict):
            continue
        state_id = state.get("marketStateId")
        if not isinstance(state_id, str) or not state_id:
            raise ValueError("replay feature state is missing marketStateId")
        if state_id in states:
            raise ValueError(f"duplicate marketStateId in replay output: {state_id}")
        states[state_id] = {
            "marketStateId": state_id,
            "timestampUtc": _parse_utc(state["timestampUtc"]),
            "featureSchemaVersion": state["featureSchemaVersion"],
            "features": _flatten_available_features(state),
        }

    rows: list[dict[str, Any]] = []
    seen_labels: set[str] = set()

    for label in _jsonl(labels_jsonl):
        state_id = label.get("marketStateId")
        if not isinstance(state_id, str) or not state_id:
            raise ValueError("label row is missing marketStateId")
        if state_id in seen_labels:
            raise ValueError(f"duplicate label marketStateId: {state_id}")
        seen_labels.add(state_id)

        state = states.get(state_id)
        if state is None:
            continue

        horizon = _find_horizon(label.get("horizons", []), horizon_seconds)
        if horizon is None:
            continue

        targets = {
            "up5First": _barrier_binary_target(
                horizon, "long", 5.0, adverse_barrier, False
            ),
            "down5First": _barrier_binary_target(
                horizon, "short", 5.0, adverse_barrier, False
            ),
            "up10First": _barrier_binary_target(
                horizon, "long", 10.0, adverse_barrier, False
            ),
            "down10First": _barrier_binary_target(
                horizon, "short", 10.0, adverse_barrier, False
            ),
            "longAdverseFirst": _barrier_binary_target(
                horizon, "long", 5.0, adverse_barrier, True
            ),
            "shortAdverseFirst": _barrier_binary_target(
                horizon, "short", 5.0, adverse_barrier, True
            ),
        }

        rows.append({**state, "targets": targets})

    rows.sort(key=lambda row: (row["timestampUtc"], row["marketStateId"]))
    return rows


def _complete_rows(
    rows: list[dict[str, Any]],
    feature_names: tuple[str, ...],
) -> list[dict[str, Any]]:
    complete: list[dict[str, Any]] = []
    for row in rows:
        features = row["features"]
        if all(name in features and _finite(features[name]) for name in feature_names):
            complete.append(row)
    return complete


def temporal_split(
    rows: list[dict[str, Any]],
    train_fraction: float = 0.60,
    validation_fraction: float = 0.20,
) -> tuple[list[dict[str, Any]], list[dict[str, Any]], list[dict[str, Any]]]:
    if not 0 < train_fraction < 1:
        raise ValueError("train_fraction must be in (0,1)")
    if not 0 < validation_fraction < 1:
        raise ValueError("validation_fraction must be in (0,1)")
    if train_fraction + validation_fraction >= 1:
        raise ValueError("train + validation fractions must be < 1")
    if len(rows) < 6:
        raise ValueError("at least six ordered rows are required for temporal split")

    ordered = sorted(rows, key=lambda row: (row["timestampUtc"], row["marketStateId"]))
    train_cut = max(1, int(len(ordered) * train_fraction))
    validation_cut = max(train_cut + 1, int(len(ordered) * (train_fraction + validation_fraction)))

    # Never split equal timestamps across windows.
    while (
        train_cut < len(ordered)
        and ordered[train_cut - 1]["timestampUtc"] == ordered[train_cut]["timestampUtc"]
    ):
        train_cut += 1

    validation_cut = max(validation_cut, train_cut + 1)
    while (
        validation_cut < len(ordered)
        and ordered[validation_cut - 1]["timestampUtc"]
        == ordered[validation_cut]["timestampUtc"]
    ):
        validation_cut += 1

    if train_cut >= len(ordered) - 1 or validation_cut >= len(ordered):
        raise ValueError("temporal split leaves an empty validation or OOS window")

    train = ordered[:train_cut]
    validation = ordered[train_cut:validation_cut]
    oos = ordered[validation_cut:]

    if not train or not validation or not oos:
        raise ValueError("temporal split produced an empty window")

    if train[-1]["timestampUtc"] >= validation[0]["timestampUtc"]:
        raise ValueError("train/validation timestamps overlap")
    if validation[-1]["timestampUtc"] >= oos[0]["timestampUtc"]:
        raise ValueError("validation/OOS timestamps overlap")

    return train, validation, oos


def _normalization(
    train: list[dict[str, Any]],
    feature_names: tuple[str, ...],
) -> list[dict[str, float | str]]:
    result: list[dict[str, float | str]] = []
    for name in feature_names:
        values = [float(row["features"][name]) for row in train]
        mean = sum(values) / len(values)
        variance = sum((value - mean) ** 2 for value in values) / len(values)
        stddev = math.sqrt(variance)
        if stddev < 1e-12:
            stddev = 1.0
        result.append({"featureName": name, "mean": mean, "stdDev": stddev})
    return result


def _vector(
    row: dict[str, Any],
    normalization: list[dict[str, float | str]],
) -> list[float]:
    return [
        (float(row["features"][item["featureName"]]) - float(item["mean"]))
        / float(item["stdDev"])
        for item in normalization
    ]


def _fit_logistic(
    vectors: list[list[float]],
    targets: list[int],
    *,
    iterations: int = 700,
    learning_rate: float = 0.05,
    l2: float = 1e-4,
) -> tuple[float, list[float]]:
    if len(vectors) != len(targets) or not vectors:
        raise ValueError("logistic training requires aligned non-empty samples")
    if set(targets) != {0, 1}:
        raise ValueError("logistic training window must contain both classes")

    width = len(vectors[0])
    weights = [0.0] * width
    intercept = 0.0

    for _ in range(iterations):
        grad_intercept = 0.0
        grad_weights = [0.0] * width

        for vector, target in zip(vectors, targets, strict=True):
            raw = intercept + sum(w * x for w, x in zip(weights, vector, strict=True))
            error = _sigmoid(raw) - target
            grad_intercept += error
            for index, value in enumerate(vector):
                grad_weights[index] += error * value

        scale = 1.0 / len(vectors)
        intercept -= learning_rate * grad_intercept * scale
        for index in range(width):
            gradient = (grad_weights[index] * scale) + (l2 * weights[index])
            weights[index] -= learning_rate * gradient

    return intercept, weights


def _raw_scores(
    rows: list[dict[str, Any]],
    normalization: list[dict[str, float | str]],
    intercept: float,
    weights: list[float],
) -> list[float]:
    result: list[float] = []
    for row in rows:
        vector = _vector(row, normalization)
        result.append(intercept + sum(w * x for w, x in zip(weights, vector, strict=True)))
    return result


def _fit_platt(
    raw_scores: list[float],
    targets: list[int],
    *,
    iterations: int = 500,
    learning_rate: float = 0.03,
) -> tuple[float, float]:
    if len(raw_scores) != len(targets) or not raw_scores:
        return 1.0, 0.0
    if len(set(targets)) < 2:
        return 1.0, 0.0

    a = 1.0
    b = 0.0
    for _ in range(iterations):
        grad_a = 0.0
        grad_b = 0.0
        for raw, target in zip(raw_scores, targets, strict=True):
            error = _sigmoid((a * raw) + b) - target
            grad_a += error * raw
            grad_b += error
        scale = 1.0 / len(raw_scores)
        a -= learning_rate * grad_a * scale
        b -= learning_rate * grad_b * scale

    return a, b


def _predict_head(
    row: dict[str, Any],
    normalization: list[dict[str, float | str]],
    head: dict[str, Any],
) -> float:
    if head["mode"] == "constant":
        return float(head["constantProbability"])

    vector = _vector(row, normalization)
    raw = float(head["intercept"]) + sum(
        float(weight) * value
        for weight, value in zip(head["weights"], vector, strict=True)
    )
    calibrated = (float(head["calibrationA"]) * raw) + float(head["calibrationB"])
    return _sigmoid(calibrated)


def _reliability(
    probabilities: list[float],
    targets: list[int],
    bins: int = 10,
) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    for index in range(bins):
        low = index / bins
        high = (index + 1) / bins
        bucket = [
            (probability, target)
            for probability, target in zip(probabilities, targets, strict=True)
            if probability >= low and (probability < high or (index == bins - 1 and probability <= high))
        ]
        if not bucket:
            continue
        result.append(
            {
                "lower": low,
                "upper": high,
                "count": len(bucket),
                "meanPrediction": sum(item[0] for item in bucket) / len(bucket),
                "realizedRate": sum(item[1] for item in bucket) / len(bucket),
            }
        )
    return result


def _binary_metrics(probabilities: list[float], targets: list[int]) -> dict[str, Any]:
    if not probabilities:
        return {"sampleCount": 0}

    eps = 1e-12
    brier = sum((p - y) ** 2 for p, y in zip(probabilities, targets, strict=True)) / len(targets)
    log_loss = -sum(
        y * math.log(max(eps, min(1 - eps, p)))
        + (1 - y) * math.log(max(eps, min(1 - eps, 1 - p)))
        for p, y in zip(probabilities, targets, strict=True)
    ) / len(targets)

    reliability = _reliability(probabilities, targets)
    ece = sum(
        bucket["count"]
        / len(targets)
        * abs(bucket["meanPrediction"] - bucket["realizedRate"])
        for bucket in reliability
    )

    return {
        "sampleCount": len(targets),
        "positiveRate": sum(targets) / len(targets),
        "brier": brier,
        "logLoss": log_loss,
        "ece": ece,
        "reliability": reliability,
    }


def _session_key(row: dict[str, Any]) -> str:
    value = row["features"].get("SessionCode")
    if _finite(value):
        return str(int(float(value)))
    return "unavailable"


def _regime_key(row: dict[str, Any]) -> str:
    value = row["features"].get("M1RangeAtrRatio")
    if not _finite(value):
        return "unavailable"
    numeric = float(value)
    if numeric < 0.75:
        return "low"
    if numeric <= 1.50:
        return "normal"
    return "high"


def _breakdown(
    rows: list[dict[str, Any]],
    head: dict[str, Any],
    normalization: list[dict[str, float | str]],
    key_function,
) -> dict[str, Any]:
    grouped: dict[str, list[dict[str, Any]]] = {}
    for row in rows:
        target = row["targets"].get(head["name"])
        if target is None:
            continue
        grouped.setdefault(key_function(row), []).append(row)

    result: dict[str, Any] = {}
    for key, group in sorted(grouped.items()):
        probabilities = [_predict_head(row, normalization, head) for row in group]
        targets = [int(row["targets"][head["name"]]) for row in group]
        result[key] = _binary_metrics(probabilities, targets)
    return result


def train_from_rows(
    rows: list[dict[str, Any]],
    *,
    feature_names: tuple[str, ...] = DEFAULT_FEATURES,
    feature_schema_version: str = "xau-features-v1",
    model_version: str = "baseline-v0",
    train_fraction: float = 0.60,
    validation_fraction: float = 0.20,
    seed: int = 0,
) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any]]:
    del seed  # V0 optimizer is deliberately deterministic and uses no random initialization.

    complete = _complete_rows(rows, feature_names)
    train, validation, oos = temporal_split(
        complete,
        train_fraction,
        validation_fraction,
    )
    normalization = _normalization(train, feature_names)

    heads: list[dict[str, Any]] = []
    head_reports: dict[str, Any] = {}

    for head_name in LEARNED_HEADS:
        train_labeled = [row for row in train if row["targets"].get(head_name) is not None]
        validation_labeled = [
            row for row in validation if row["targets"].get(head_name) is not None
        ]
        oos_labeled = [row for row in oos if row["targets"].get(head_name) is not None]

        train_targets = [int(row["targets"][head_name]) for row in train_labeled]
        if len(train_labeled) < 4 or len(set(train_targets)) < 2:
            raise ValueError(
                f"head {head_name} needs at least four train samples and both classes"
            )

        vectors = [_vector(row, normalization) for row in train_labeled]
        intercept, weights = _fit_logistic(vectors, train_targets)

        validation_targets = [
            int(row["targets"][head_name]) for row in validation_labeled
        ]
        validation_raw = _raw_scores(
            validation_labeled,
            normalization,
            intercept,
            weights,
        )
        calibration_a, calibration_b = _fit_platt(
            validation_raw,
            validation_targets,
        )

        head = {
            "name": head_name,
            "mode": "logistic",
            "intercept": intercept,
            "weights": weights,
            "calibrationA": calibration_a,
            "calibrationB": calibration_b,
            "constantProbability": None,
            "trainingStatus": "learned",
        }
        heads.append(head)

        oos_targets = [int(row["targets"][head_name]) for row in oos_labeled]
        oos_probabilities = [
            _predict_head(row, normalization, head) for row in oos_labeled
        ]
        head_reports[head_name] = {
            "trainSampleCount": len(train_labeled),
            "trainPositiveRate": sum(train_targets) / len(train_targets),
            "validationSampleCount": len(validation_labeled),
            "oos": _binary_metrics(oos_probabilities, oos_targets),
            "oosBySession": _breakdown(
                oos_labeled,
                head,
                normalization,
                _session_key,
            ),
            "oosByRegime": _breakdown(
                oos_labeled,
                head,
                normalization,
                _regime_key,
            ),
        }

    for head_name in CONSTANT_HEADS:
        heads.append(
            {
                "name": head_name,
                "mode": "constant",
                "intercept": 0.0,
                "weights": [],
                "calibrationA": 1.0,
                "calibrationB": 0.0,
                "constantProbability": 0.5,
                "trainingStatus": "constant-untrained",
            }
        )

    artifact = {
        "artifactFormatVersion": ARTIFACT_FORMAT_VERSION,
        "modelId": "xau-native",
        "modelVersion": model_version,
        "featureSchemaVersion": feature_schema_version,
        "featureNames": list(feature_names),
        "normalization": normalization,
        "heads": heads,
        "actionPolicy": {"minimumEdge": 0.05},
    }

    split_manifest = {
        "method": "strict-temporal-no-shuffle",
        "trainCount": len(train),
        "validationCount": len(validation),
        "oosCount": len(oos),
        "trainStartUtc": train[0]["timestampUtc"].isoformat(),
        "trainEndUtc": train[-1]["timestampUtc"].isoformat(),
        "validationStartUtc": validation[0]["timestampUtc"].isoformat(),
        "validationEndUtc": validation[-1]["timestampUtc"].isoformat(),
        "oosStartUtc": oos[0]["timestampUtc"].isoformat(),
        "oosEndUtc": oos[-1]["timestampUtc"].isoformat(),
    }

    report = {
        "trainerVersion": TRAINER_VERSION,
        "evidenceStatus": "oos-pipeline-output-not-automatic-production-promotion",
        "featureSchemaVersion": feature_schema_version,
        "modelVersion": model_version,
        "sampleCountAfterRequiredFeatureFilter": len(complete),
        "droppedForMissingRequiredFeatures": len(rows) - len(complete),
        "temporalSplit": split_manifest,
        "heads": head_reports,
        "constantUntrainedHeads": list(CONSTANT_HEADS),
    }

    training_manifest = {
        "trainerVersion": TRAINER_VERSION,
        "featureSchemaVersion": feature_schema_version,
        "modelId": "xau-native",
        "modelVersion": model_version,
        "features": list(feature_names),
        "temporalSplit": split_manifest,
        "optimizer": {
            "type": "deterministic-batch-logistic-gradient-descent",
            "calibration": "platt-on-validation",
            "randomShuffle": False,
        },
    }

    return artifact, report, training_manifest


def offline_predict(
    artifact: dict[str, Any],
    features: dict[str, float],
) -> dict[str, Any]:
    normalization = artifact["normalization"]
    row = {"features": features}

    probabilities = {
        head["name"]: _predict_head(row, normalization, head)
        for head in artifact["heads"]
    }

    long_edge = probabilities["up5First"] - probabilities["longAdverseFirst"]
    short_edge = probabilities["down5First"] - probabilities["shortAdverseFirst"]
    minimum_edge = float(artifact["actionPolicy"]["minimumEdge"])

    if long_edge >= minimum_edge and long_edge >= short_edge:
        action = "long"
        action_probability = probabilities["up5First"]
        adverse = probabilities["longAdverseFirst"]
    elif short_edge >= minimum_edge:
        action = "short"
        action_probability = probabilities["down5First"]
        adverse = probabilities["shortAdverseFirst"]
    else:
        action = "wait"
        action_probability = max(
            0.0,
            min(
                1.0,
                1.0
                - max(
                    probabilities["up5First"],
                    probabilities["down5First"],
                ),
            ),
        )
        adverse = max(
            probabilities["longAdverseFirst"],
            probabilities["shortAdverseFirst"],
        )

    confidence = max(
        0.0,
        min(
            1.0,
            2.0
            * max(
                abs(probabilities["up5First"] - 0.5),
                abs(probabilities["down5First"] - 0.5),
            ),
        ),
    )

    return {
        "action": action,
        "actionProbability": action_probability,
        "pUp5First": probabilities["up5First"],
        "pDown5First": probabilities["down5First"],
        "pUp10First": probabilities["up10First"],
        "pDown10First": probabilities["down10First"],
        "pAdverseBarrierFirst": adverse,
        "pContinuation": probabilities["continuation"],
        "pReversal": probabilities["reversal"],
        "pFalseBreak": probabilities["falseBreak"],
        "confidence": confidence,
    }


def _write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(value, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")


def train_files(
    replay_jsonl: Path,
    labels_jsonl: Path,
    output_dir: Path,
    *,
    feature_names: tuple[str, ...],
    feature_schema_version: str,
    model_version: str,
    code_commit: str,
    horizon_seconds: float,
    adverse_barrier: float,
    seed: int,
) -> dict[str, Path]:
    rows = load_joined_rows(
        replay_jsonl,
        labels_jsonl,
        horizon_seconds=horizon_seconds,
        adverse_barrier=adverse_barrier,
    )

    artifact, report, training_manifest = train_from_rows(
        rows,
        feature_names=feature_names,
        feature_schema_version=feature_schema_version,
        model_version=model_version,
        seed=seed,
    )

    training_manifest.update(
        {
            "seed": seed,
            "codeCommit": code_commit,
            "sources": {
                "replayJsonl": str(replay_jsonl),
                "replaySha256": sha256_file(replay_jsonl),
                "labelsJsonl": str(labels_jsonl),
                "labelsSha256": sha256_file(labels_jsonl),
                "joinedRowCount": len(rows),
                "horizonSeconds": horizon_seconds,
                "adverseBarrierPrice": adverse_barrier,
            },
        }
    )

    output_dir.mkdir(parents=True, exist_ok=True)
    artifact_path = output_dir / f"{model_version}.json"
    report_path = output_dir / f"{model_version}.oos-report.json"
    training_manifest_path = output_dir / f"{model_version}.training-manifest.json"
    artifact_manifest_path = output_dir / f"{model_version}.artifact-manifest.json"

    _write_json(artifact_path, artifact)
    _write_json(report_path, report)
    _write_json(training_manifest_path, training_manifest)

    artifact_hash = sha256_file(artifact_path)
    artifact_manifest = {
        "artifactManifestVersion": ARTIFACT_MANIFEST_VERSION,
        "artifactFileName": artifact_path.name,
        "artifactSha256": artifact_hash,
        "artifactFormatVersion": ARTIFACT_FORMAT_VERSION,
        "modelId": "xau-native",
        "modelVersion": model_version,
        "featureSchemaVersion": feature_schema_version,
        "trainingManifestFileName": training_manifest_path.name,
    }
    _write_json(artifact_manifest_path, artifact_manifest)

    return {
        "artifact": artifact_path,
        "artifactManifest": artifact_manifest_path,
        "trainingManifest": training_manifest_path,
        "oosReport": report_path,
    }


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--replay-jsonl", type=Path, required=True)
    parser.add_argument("--labels-jsonl", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument(
        "--features",
        default=",".join(DEFAULT_FEATURES),
        help="comma-separated required feature names",
    )
    parser.add_argument("--feature-schema-version", default="xau-features-v1")
    parser.add_argument("--model-version", required=True)
    parser.add_argument("--code-commit", required=True)
    parser.add_argument("--horizon-seconds", type=float, default=300.0)
    parser.add_argument("--adverse-barrier", type=float, default=3.0)
    parser.add_argument("--seed", type=int, default=0)
    return parser.parse_args()


def main() -> int:
    args = _parse_args()
    feature_names = tuple(
        item.strip() for item in args.features.split(",") if item.strip()
    )
    if not feature_names:
        raise ValueError("at least one feature is required")

    paths = train_files(
        args.replay_jsonl,
        args.labels_jsonl,
        args.output_dir,
        feature_names=feature_names,
        feature_schema_version=args.feature_schema_version,
        model_version=args.model_version,
        code_commit=args.code_commit,
        horizon_seconds=args.horizon_seconds,
        adverse_barrier=args.adverse_barrier,
        seed=args.seed,
    )

    print(json.dumps({key: str(value) for key, value in paths.items()}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
