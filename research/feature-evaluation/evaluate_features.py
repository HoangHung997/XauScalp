#!/usr/bin/env python3
"""Deterministic temporal feature evaluation and ablation for XauScalp."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import math
import statistics
from collections import defaultdict
from pathlib import Path
from typing import Any, Iterable, Sequence

PIPELINE_VERSION = "xau-feature-evaluation-v1"
BASELINE_FEATURES = ("SpreadAtrRatio", "AtrRatioM1")

FEATURE_FAMILIES: dict[str, tuple[str, ...]] = {
    "microstructure": (
        "Return1s",
        "Velocity1s",
        "Acceleration1s",
        "DecelerationRatio",
        "DirectionFlipAgeMs",
        "BurstZScore",
    ),
    "liquidity_reaction": (
        "SweepDepthAtr",
        "CloseBackInsideAtr",
        "DirectionFlipAfterTouch",
        "MicroRetestOccurred",
        "ResumeVelocity",
    ),
    "magnet_vacuum": (
        "UpperMagnetDistanceAtr",
        "LowerMagnetDistanceAtr",
        "PullDelta",
        "InsideVacuum",
    ),
    "absorption": (
        "AbsorptionUpScore",
        "AbsorptionDownScore",
    ),
    "fvg_structure": (
        "BosDirection",
        "FvgSizeAtr",
        "FvgFillPct",
        "DisplacementRangeAtr",
        "OrderBlockOverlapPct",
    ),
    "regime_context": (
        "AdxM1",
        "M1CompressionScore",
        "EmaSlopeM5",
        "TrendAlignmentScore",
        "HtfConflict",
    ),
}

REQUIRED_ENTRY_MODES = (
    "m1Close",
    "intrabarImmediate",
    "intrabarDeceleration",
    "intrabarDirectionFlip",
    "intrabarMicroRetest",
)


def _parse_iso(value: str) -> dt.datetime:
    parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError("timestamp must include timezone")
    return parsed.astimezone(dt.timezone.utc)


def _duration_seconds(value: str) -> float:
    parts = value.split(":")
    if len(parts) != 3:
        raise ValueError(f"unsupported duration: {value}")
    hours = int(parts[0])
    minutes = int(parts[1])
    seconds = float(parts[2])
    return (hours * 3600) + (minutes * 60) + seconds


def _jsonl(path: Path) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    with path.open("r", encoding="utf-8") as handle:
        for line_number, line in enumerate(handle, start=1):
            if not line.strip():
                raise ValueError(f"blank JSONL line at {path}:{line_number}")
            value = json.loads(line)
            if not isinstance(value, dict):
                raise ValueError(f"JSONL record must be object at {path}:{line_number}")
            rows.append(value)
    return rows


def load_joined_rows(
    replay_jsonl: Path,
    labels_jsonl: Path,
    *,
    horizon_seconds: float = 300.0,
    target_distance: float = 5.0,
    adverse_barrier: float = 3.0,
    side: str = "long",
) -> tuple[list[dict[str, Any]], dict[str, int]]:
    """Join causal replay states to future labels only by immutable MarketStateId."""

    label_by_id = {
        str(item["marketStateId"]): item
        for item in _jsonl(labels_jsonl)
    }

    joined: list[dict[str, Any]] = []
    censored = 0
    missing_label = 0
    missing_state = 0

    for replay in _jsonl(replay_jsonl):
        state = replay.get("featureState")
        if not isinstance(state, dict):
            missing_state += 1
            continue

        state_id = str(state["marketStateId"])
        label = label_by_id.get(state_id)
        if label is None:
            missing_label += 1
            continue

        matching_horizon = None
        for horizon in label.get("horizons", []):
            if math.isclose(
                _duration_seconds(str(horizon["horizon"])),
                horizon_seconds,
                rel_tol=0,
                abs_tol=1e-9,
            ):
                matching_horizon = horizon
                break

        if matching_horizon is None:
            missing_label += 1
            continue

        matching_barrier = None
        for candidate in matching_horizon.get("barrierFirst", []):
            if (
                str(candidate["side"]) == side
                and math.isclose(
                    float(candidate["targetDistancePrice"]),
                    target_distance,
                    rel_tol=0,
                    abs_tol=1e-9,
                )
                and math.isclose(
                    float(candidate["adverseBarrierPrice"]),
                    adverse_barrier,
                    rel_tol=0,
                    abs_tol=1e-9,
                )
            ):
                matching_barrier = candidate
                break

        if matching_barrier is None:
            missing_label += 1
            continue

        outcome = str(matching_barrier["outcome"])
        if outcome == "censored":
            censored += 1
            continue
        if outcome not in ("targetFirst", "adverseFirst"):
            raise ValueError(f"unsupported label outcome: {outcome}")

        feature_values: dict[str, float] = {}
        for feature in state.get("features", []):
            if (
                feature.get("isAvailable") is True
                and isinstance(feature.get("value"), (int, float))
                and math.isfinite(float(feature["value"]))
            ):
                feature_values[str(feature["name"])] = float(feature["value"])

        joined.append(
            {
                "marketStateId": state_id,
                "timestampUtc": _parse_iso(str(state["timestampUtc"])),
                "features": feature_values,
                "target": 1 if outcome == "targetFirst" else 0,
            }
        )

    joined.sort(key=lambda row: (row["timestampUtc"], row["marketStateId"]))
    return joined, {
        "joinedSupervisedRows": len(joined),
        "censoredRows": censored,
        "missingLabelRows": missing_label,
        "nonFeatureReplayRows": missing_state,
    }


def _timestamp_groups(rows: Sequence[dict[str, Any]]) -> list[list[dict[str, Any]]]:
    groups: list[list[dict[str, Any]]] = []
    current_timestamp: dt.datetime | None = None

    for row in sorted(
        rows,
        key=lambda item: (item["timestampUtc"], item["marketStateId"]),
    ):
        timestamp = row["timestampUtc"]
        if current_timestamp != timestamp:
            groups.append([])
            current_timestamp = timestamp
        groups[-1].append(row)

    return groups


def temporal_split(
    rows: Sequence[dict[str, Any]],
    train_fraction: float = 0.60,
    validation_fraction: float = 0.20,
) -> tuple[list[dict[str, Any]], list[dict[str, Any]], list[dict[str, Any]]]:
    """Strict chronological split; equal timestamps cannot cross boundaries."""

    if not 0 < train_fraction < 1:
        raise ValueError("train_fraction must be in (0,1)")
    if not 0 < validation_fraction < 1:
        raise ValueError("validation_fraction must be in (0,1)")
    if train_fraction + validation_fraction >= 1:
        raise ValueError("train + validation fractions must be < 1")

    groups = _timestamp_groups(rows)
    if len(groups) < 5:
        raise ValueError("at least five distinct timestamps are required")

    train_end = max(1, min(len(groups) - 2, round(len(groups) * train_fraction)))
    validation_end = max(
        train_end + 1,
        min(
            len(groups) - 1,
            round(len(groups) * (train_fraction + validation_fraction)),
        ),
    )

    train = [row for group in groups[:train_end] for row in group]
    validation = [
        row for group in groups[train_end:validation_end] for row in group
    ]
    oos = [row for group in groups[validation_end:] for row in group]

    if not train or not validation or not oos:
        raise ValueError("temporal split produced an empty window")

    if not (
        train[-1]["timestampUtc"] < validation[0]["timestampUtc"]
        and validation[-1]["timestampUtc"] < oos[0]["timestampUtc"]
    ):
        raise AssertionError("temporal split leaked equal timestamps")

    return train, validation, oos


def walk_forward_windows(
    rows: Sequence[dict[str, Any]],
    folds: int = 3,
) -> list[
    tuple[list[dict[str, Any]], list[dict[str, Any]], list[dict[str, Any]]]
]:
    groups = _timestamp_groups(rows)
    if folds <= 0:
        raise ValueError("folds must be positive")

    window = max(2, len(groups) // 10)
    initial_train_end = len(groups) - ((folds + 1) * window)
    if initial_train_end < max(4, window):
        return []

    result = []
    for fold in range(folds):
        train_end = initial_train_end + (fold * window)
        validation_end = train_end + window
        test_end = min(len(groups), validation_end + window)

        train = [row for group in groups[:train_end] for row in group]
        validation = [
            row for group in groups[train_end:validation_end] for row in group
        ]
        test = [
            row for group in groups[validation_end:test_end] for row in group
        ]

        if train and validation and test:
            result.append((train, validation, test))

    return result


def _complete_rows(
    rows: Sequence[dict[str, Any]],
    feature_names: Sequence[str],
) -> list[dict[str, Any]]:
    required = tuple(dict.fromkeys(feature_names))
    return [
        row
        for row in rows
        if all(
            name in row["features"]
            and math.isfinite(float(row["features"][name]))
            for name in required
        )
    ]


def _normalization(
    rows: Sequence[dict[str, Any]],
    feature_names: Sequence[str],
) -> list[tuple[float, float]]:
    result = []
    for feature in feature_names:
        values = [float(row["features"][feature]) for row in rows]
        mean = statistics.fmean(values)
        variance = statistics.fmean((value - mean) ** 2 for value in values)
        std = math.sqrt(variance)
        result.append((mean, std if std > 1e-12 else 1.0))
    return result


def _vector(
    row: dict[str, Any],
    feature_names: Sequence[str],
    normalization: Sequence[tuple[float, float]],
) -> list[float]:
    return [
        (float(row["features"][feature]) - normalization[index][0])
        / normalization[index][1]
        for index, feature in enumerate(feature_names)
    ]


def _sigmoid(value: float) -> float:
    value = max(-40.0, min(40.0, value))
    return 1.0 / (1.0 + math.exp(-value))


def _logit(probability: float) -> float:
    probability = max(1e-9, min(1 - 1e-9, probability))
    return math.log(probability / (1 - probability))


def _fit_logistic(
    rows: Sequence[dict[str, Any]],
    feature_names: Sequence[str],
    normalization: Sequence[tuple[float, float]],
    *,
    epochs: int = 500,
    learning_rate: float = 0.05,
    l2: float = 0.001,
) -> dict[str, Any]:
    targets = [int(row["target"]) for row in rows]
    positive_rate = statistics.fmean(targets)
    if positive_rate in (0.0, 1.0):
        return {
            "constant": positive_rate,
            "intercept": 0.0,
            "weights": [0.0 for _ in feature_names],
        }

    weights = [0.0 for _ in feature_names]
    intercept = _logit(positive_rate)

    for _ in range(epochs):
        gradient_intercept = 0.0
        gradient_weights = [0.0 for _ in weights]

        for row in rows:
            x = _vector(row, feature_names, normalization)
            raw = intercept + sum(
                weight * value for weight, value in zip(weights, x, strict=True)
            )
            error = _sigmoid(raw) - int(row["target"])
            gradient_intercept += error
            for index, value in enumerate(x):
                gradient_weights[index] += error * value

        count = len(rows)
        intercept -= learning_rate * gradient_intercept / count
        for index in range(len(weights)):
            regularized = (gradient_weights[index] / count) + (l2 * weights[index])
            weights[index] -= learning_rate * regularized

    return {
        "constant": None,
        "intercept": intercept,
        "weights": weights,
    }


def _raw_probability(
    row: dict[str, Any],
    feature_names: Sequence[str],
    normalization: Sequence[tuple[float, float]],
    model: dict[str, Any],
) -> float:
    if model["constant"] is not None:
        return float(model["constant"])
    x = _vector(row, feature_names, normalization)
    raw = float(model["intercept"]) + sum(
        float(weight) * value
        for weight, value in zip(model["weights"], x, strict=True)
    )
    return _sigmoid(raw)


def _fit_platt(
    probabilities: Sequence[float],
    targets: Sequence[int],
) -> tuple[float, float]:
    if not probabilities:
        return 1.0, 0.0
    if len(set(targets)) < 2:
        return 1.0, 0.0

    logits = [_logit(probability) for probability in probabilities]
    a = 1.0
    b = 0.0

    for _ in range(300):
        grad_a = 0.0
        grad_b = 0.0
        for raw, target in zip(logits, targets, strict=True):
            error = _sigmoid((a * raw) + b) - target
            grad_a += error * raw
            grad_b += error

        count = len(targets)
        a -= 0.02 * grad_a / count
        b -= 0.02 * grad_b / count

    return a, b


def _predict(
    rows: Sequence[dict[str, Any]],
    feature_names: Sequence[str],
    normalization: Sequence[tuple[float, float]],
    model: dict[str, Any],
    calibration: tuple[float, float],
) -> list[float]:
    a, b = calibration
    result = []
    for row in rows:
        raw_probability = _raw_probability(
            row,
            feature_names,
            normalization,
            model,
        )
        result.append(_sigmoid((a * _logit(raw_probability)) + b))
    return result


def _wilson_interval(
    positive: int,
    total: int,
    z: float = 1.96,
) -> tuple[float | None, float | None]:
    if total <= 0:
        return None, None
    p = positive / total
    denominator = 1 + ((z * z) / total)
    center = (p + ((z * z) / (2 * total))) / denominator
    margin = (
        z
        * math.sqrt(
            (p * (1 - p) / total)
            + ((z * z) / (4 * total * total))
        )
        / denominator
    )
    return max(0.0, center - margin), min(1.0, center + margin)


def score_predictions(
    targets: Sequence[int],
    probabilities: Sequence[float],
) -> dict[str, Any]:
    if len(targets) != len(probabilities) or not targets:
        raise ValueError("non-empty aligned targets/probabilities required")

    clipped = [max(1e-9, min(1 - 1e-9, p)) for p in probabilities]
    brier = statistics.fmean(
        (probability - target) ** 2
        for target, probability in zip(targets, clipped, strict=True)
    )
    log_loss = -statistics.fmean(
        (target * math.log(probability))
        + ((1 - target) * math.log(1 - probability))
        for target, probability in zip(targets, clipped, strict=True)
    )

    reliability = []
    ece = 0.0
    bins = 10
    for index in range(bins):
        lower = index / bins
        upper = (index + 1) / bins
        bucket = [
            (target, probability)
            for target, probability in zip(targets, clipped, strict=True)
            if probability >= lower
            and (probability < upper or (index == bins - 1 and probability <= upper))
        ]
        if not bucket:
            continue
        realized = statistics.fmean(item[0] for item in bucket)
        predicted = statistics.fmean(item[1] for item in bucket)
        weight = len(bucket) / len(targets)
        ece += weight * abs(realized - predicted)
        reliability.append(
            {
                "lower": lower,
                "upper": upper,
                "sampleCount": len(bucket),
                "meanPrediction": predicted,
                "realizedRate": realized,
            }
        )

    positive = sum(targets)
    ci_low, ci_high = _wilson_interval(positive, len(targets))
    return {
        "sampleCount": len(targets),
        "positiveCount": positive,
        "negativeCount": len(targets) - positive,
        "targetRate": positive / len(targets),
        "targetRateWilson95": [ci_low, ci_high],
        "brier": brier,
        "logLoss": log_loss,
        "ece": ece,
        "reliability": reliability,
    }


def fit_and_score(
    train: Sequence[dict[str, Any]],
    validation: Sequence[dict[str, Any]],
    oos: Sequence[dict[str, Any]],
    feature_names: Sequence[str],
) -> tuple[dict[str, Any], list[float]]:
    normalization = _normalization(train, feature_names)
    model = _fit_logistic(train, feature_names, normalization)

    validation_raw = [
        _raw_probability(row, feature_names, normalization, model)
        for row in validation
    ]
    calibration = _fit_platt(
        validation_raw,
        [int(row["target"]) for row in validation],
    )

    probabilities = _predict(
        oos,
        feature_names,
        normalization,
        model,
        calibration,
    )
    metrics = score_predictions(
        [int(row["target"]) for row in oos],
        probabilities,
    )
    metrics["features"] = list(feature_names)
    metrics["calibration"] = {
        "method": "platt-validation-only",
        "a": calibration[0],
        "b": calibration[1],
    }
    return metrics, probabilities


def evaluate_variant(
    rows: Sequence[dict[str, Any]],
    feature_names: Sequence[str],
) -> dict[str, Any]:
    complete = _complete_rows(rows, feature_names)
    if len(complete) < 30 or len(_timestamp_groups(complete)) < 10:
        return {
            "status": "insufficient-data",
            "features": list(feature_names),
            "sampleCount": len(complete),
        }

    train, validation, oos = temporal_split(complete)
    metrics, _ = fit_and_score(train, validation, oos, feature_names)
    return {
        "status": "scored",
        "completeSampleCount": len(complete),
        "droppedMissingFeatureCount": len(rows) - len(complete),
        "trainCount": len(train),
        "validationCount": len(validation),
        "oos": metrics,
    }


def evaluate_pair(
    rows: Sequence[dict[str, Any]],
    left_features: Sequence[str],
    right_features: Sequence[str],
) -> dict[str, Any]:
    union = tuple(dict.fromkeys([*left_features, *right_features]))
    complete = _complete_rows(rows, union)

    if len(complete) < 30 or len(_timestamp_groups(complete)) < 10:
        return {
            "status": "insufficient-data",
            "commonSampleCount": len(complete),
            "leftFeatures": list(left_features),
            "rightFeatures": list(right_features),
            "walkForward": [],
        }

    train, validation, oos = temporal_split(complete)
    left, _ = fit_and_score(train, validation, oos, left_features)
    right, _ = fit_and_score(train, validation, oos, right_features)

    windows = []
    for fold, (wf_train, wf_validation, wf_oos) in enumerate(
        walk_forward_windows(complete),
        start=1,
    ):
        left_fold, _ = fit_and_score(
            wf_train,
            wf_validation,
            wf_oos,
            left_features,
        )
        right_fold, _ = fit_and_score(
            wf_train,
            wf_validation,
            wf_oos,
            right_features,
        )
        windows.append(
            {
                "fold": fold,
                "oosCount": len(wf_oos),
                "leftBrier": left_fold["brier"],
                "rightBrier": right_fold["brier"],
                "rightMinusLeftBrier": right_fold["brier"] - left_fold["brier"],
            }
        )

    return {
        "status": "scored",
        "commonSampleCount": len(complete),
        "droppedForFairComparison": len(rows) - len(complete),
        "leftFeatures": list(left_features),
        "rightFeatures": list(right_features),
        "leftOos": left,
        "rightOos": right,
        "rightMinusLeftBrier": right["brier"] - left["brier"],
        "rightMinusLeftLogLoss": right["logLoss"] - left["logLoss"],
        "walkForward": windows,
    }


def pearson_correlation(
    rows: Sequence[dict[str, Any]],
    left: str,
    right: str,
) -> tuple[int, float | None]:
    pairs = [
        (float(row["features"][left]), float(row["features"][right]))
        for row in rows
        if left in row["features"]
        and right in row["features"]
        and math.isfinite(float(row["features"][left]))
        and math.isfinite(float(row["features"][right]))
    ]
    if len(pairs) < 3:
        return len(pairs), None

    xs = [pair[0] for pair in pairs]
    ys = [pair[1] for pair in pairs]
    mean_x = statistics.fmean(xs)
    mean_y = statistics.fmean(ys)
    covariance = sum(
        (x - mean_x) * (y - mean_y)
        for x, y in pairs
    )
    variance_x = sum((x - mean_x) ** 2 for x in xs)
    variance_y = sum((y - mean_y) ** 2 for y in ys)

    if variance_x <= 1e-18 or variance_y <= 1e-18:
        return len(pairs), None

    return len(pairs), covariance / math.sqrt(variance_x * variance_y)


def correlation_report(
    rows: Sequence[dict[str, Any]],
    feature_names: Sequence[str],
) -> dict[str, Any]:
    pairs = []
    redundant = []
    unique = tuple(dict.fromkeys(feature_names))

    for left_index, left in enumerate(unique):
        for right in unique[left_index + 1 :]:
            count, correlation = pearson_correlation(rows, left, right)
            item = {
                "left": left,
                "right": right,
                "sampleCount": count,
                "correlation": correlation,
            }
            pairs.append(item)
            if (
                correlation is not None
                and count >= 30
                and abs(correlation) >= 0.90
            ):
                redundant.append(item)

    return {
        "pairCount": len(pairs),
        "pairs": pairs,
        "redundantAbsCorrelationGte090": redundant,
    }


def _breakdown_bucket_metrics(
    rows: Sequence[dict[str, Any]],
    probabilities: Sequence[float],
    bucket_name: str,
    bucket_fn,
) -> dict[str, Any]:
    grouped: dict[str, list[tuple[int, float]]] = defaultdict(list)
    for row, probability in zip(rows, probabilities, strict=True):
        key = bucket_fn(row)
        if key is not None:
            grouped[str(key)].append((int(row["target"]), probability))

    return {
        bucket_name: {
            key: score_predictions(
                [item[0] for item in items],
                [item[1] for item in items],
            )
            for key, items in sorted(grouped.items())
            if items
        }
    }


def baseline_breakdowns(
    rows: Sequence[dict[str, Any]],
    feature_names: Sequence[str],
) -> dict[str, Any]:
    complete = _complete_rows(rows, feature_names)
    if len(complete) < 30:
        return {"status": "insufficient-data"}

    train, validation, oos = temporal_split(complete)
    metrics, probabilities = fit_and_score(
        train,
        validation,
        oos,
        feature_names,
    )

    def feature(row: dict[str, Any], name: str) -> float | None:
        value = row["features"].get(name)
        return float(value) if value is not None and math.isfinite(float(value)) else None

    result: dict[str, Any] = {
        "status": "scored",
        "baselineOos": metrics,
    }
    result.update(
        _breakdown_bucket_metrics(
            oos,
            probabilities,
            "session",
            lambda row: (
                f"session-{int(feature(row, 'SessionCode'))}"
                if feature(row, "SessionCode") is not None
                else None
            ),
        )
    )
    result.update(
        _breakdown_bucket_metrics(
            oos,
            probabilities,
            "regime",
            lambda row: (
                "low-vol"
                if (feature(row, "AtrRatioM1") or 0) < 0.8
                else "high-vol"
                if (feature(row, "AtrRatioM1") or 0) > 1.2
                else "normal-vol"
            ),
        )
    )
    result.update(
        _breakdown_bucket_metrics(
            oos,
            probabilities,
            "spread",
            lambda row: (
                "tight"
                if (feature(row, "SpreadAtrRatio") or 0) < 0.10
                else "wide"
                if (feature(row, "SpreadAtrRatio") or 0) > 0.25
                else "normal"
            ),
        )
    )
    return result


def _family_status(
    add_pair: dict[str, Any],
    remove_pair: dict[str, Any],
) -> dict[str, Any]:
    if add_pair.get("status") != "scored" or remove_pair.get("status") != "scored":
        return {
            "status": "Inconclusive",
            "reason": "insufficient paired OOS evidence",
        }

    add_improvement = -float(add_pair["rightMinusLeftBrier"])
    remove_damage = -float(remove_pair["rightMinusLeftBrier"])
    # remove_pair left=full, right=without-family, so negative right-left means
    # removal improved; positive means removal damaged. Correct sign explicitly:
    remove_damage = float(remove_pair["rightMinusLeftBrier"])

    evidences = [add_improvement, remove_damage]
    median_effect = statistics.median(evidences)

    walk = [
        -float(item["rightMinusLeftBrier"])
        for item in add_pair.get("walkForward", [])
    ] + [
        float(item["rightMinusLeftBrier"])
        for item in remove_pair.get("walkForward", [])
    ]
    positive_fraction = (
        sum(effect > 0 for effect in walk) / len(walk)
        if walk
        else 0.0
    )

    if median_effect >= 0.002 and positive_fraction >= (2 / 3):
        status = "Supported"
        reason = "paired OOS Brier improves and walk-forward effect is mostly positive"
    elif median_effect <= -0.002 and positive_fraction <= (1 / 3):
        status = "Rejected"
        reason = "paired OOS Brier degrades and walk-forward effect is mostly negative"
    else:
        status = "Inconclusive"
        reason = "effect is small, mixed, or unstable across windows"

    return {
        "status": status,
        "reason": reason,
        "addOneBrierImprovement": add_improvement,
        "removeOneBrierDamage": remove_damage,
        "medianBrierEvidence": median_effect,
        "walkForwardPositiveFraction": positive_fraction,
        "walkForwardEvidenceCount": len(walk),
    }


def evaluate_rows(
    rows: Sequence[dict[str, Any]],
    *,
    baseline_features: Sequence[str] = BASELINE_FEATURES,
    feature_families: dict[str, Sequence[str]] = FEATURE_FAMILIES,
) -> dict[str, Any]:
    if len(rows) < 30:
        raise ValueError("at least 30 supervised rows are required")

    baseline = tuple(dict.fromkeys(baseline_features))
    family_map = {
        name: tuple(dict.fromkeys(features))
        for name, features in feature_families.items()
    }
    full = tuple(
        dict.fromkeys(
            [
                *baseline,
                *[
                    feature
                    for family in family_map.values()
                    for feature in family
                ],
            ]
        )
    )

    ablation: dict[str, Any] = {}
    registry: dict[str, Any] = {}

    for family_name, family_features in family_map.items():
        add_features = tuple(dict.fromkeys([*baseline, *family_features]))
        remove_features = tuple(
            feature for feature in full if feature not in family_features
        )

        add_pair = evaluate_pair(rows, baseline, add_features)
        remove_pair = evaluate_pair(rows, full, remove_features)
        ablation[family_name] = {
            "addOneFamily": add_pair,
            "removeOneFamily": remove_pair,
        }
        registry[family_name] = _family_status(add_pair, remove_pair)

    return {
        "pipelineVersion": PIPELINE_VERSION,
        "method": {
            "split": "strict-temporal-no-shuffle",
            "normalization": "train-only",
            "calibration": "platt-validation-only",
            "ablationFairness": "paired comparison uses identical complete rows and time windows",
            "statusRule": "Supported/Rejected/Inconclusive from paired OOS Brier plus walk-forward stability",
        },
        "sampleCount": len(rows),
        "baselineFeatures": list(baseline),
        "fullCandidateFeatures": list(full),
        "baseline": evaluate_variant(rows, baseline),
        "fullCandidate": evaluate_variant(rows, full),
        "correlation": correlation_report(rows, full),
        "ablation": ablation,
        "candidateStatusRegistry": registry,
        "breakdowns": baseline_breakdowns(rows, baseline),
    }


def summarize_entry_modes(
    outcomes: Sequence[dict[str, Any]],
) -> dict[str, Any]:
    if not outcomes:
        return {"status": "not-provided", "modes": {}}

    scenarios = {
        str(item["costScenarioName"])
        for item in outcomes
    }
    if len(scenarios) != 1:
        raise ValueError("entry-mode comparison requires one common cost scenario")

    grouped: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for item in outcomes:
        key = item.get("key", {})
        grouped[str(key.get("mode"))].append(item)

    modes: dict[str, Any] = {}
    for mode in REQUIRED_ENTRY_MODES:
        items = grouped.get(mode, [])
        entered = [item for item in items if item.get("outcome") != "noEntry"]
        target = [item for item in entered if item.get("outcome") == "targetFirst"]
        adverse = [item for item in entered if item.get("outcome") == "adverseFirst"]
        censored = [item for item in entered if item.get("outcome") == "censored"]
        net = [
            float(item["netResultPrice"])
            for item in entered
            if item.get("netResultPrice") is not None
        ]
        modes[mode] = {
            "candidateRows": len(items),
            "entryRows": len(entered),
            "targetFirstRows": len(target),
            "adverseFirstRows": len(adverse),
            "censoredRows": len(censored),
            "pTargetFirst": len(target) / len(entered) if entered else None,
            "expectedNetPriceAfterCosts": statistics.fmean(net) if net else None,
        }

    missing = [mode for mode in REQUIRED_ENTRY_MODES if not grouped.get(mode)]
    return {
        "status": "complete-five-mode-comparison" if not missing else "incomplete",
        "costScenarioName": next(iter(scenarios)),
        "missingModes": missing,
        "modes": modes,
    }


def write_report(path: Path, report: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(report, handle, indent=2, sort_keys=True)
        handle.write("\n")


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--replay-jsonl", type=Path, required=True)
    parser.add_argument("--labels-jsonl", type=Path, required=True)
    parser.add_argument("--entry-outcomes-jsonl", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--horizon-seconds", type=float, default=300.0)
    parser.add_argument("--target-distance", type=float, default=5.0)
    parser.add_argument("--adverse-barrier", type=float, default=3.0)
    parser.add_argument("--side", choices=("long", "short"), default="long")
    return parser.parse_args()


def main() -> int:
    args = _parse_args()
    rows, load_stats = load_joined_rows(
        args.replay_jsonl,
        args.labels_jsonl,
        horizon_seconds=args.horizon_seconds,
        target_distance=args.target_distance,
        adverse_barrier=args.adverse_barrier,
        side=args.side,
    )
    report = evaluate_rows(rows)
    report["input"] = {
        "loadStats": load_stats,
        "horizonSeconds": args.horizon_seconds,
        "targetDistancePrice": args.target_distance,
        "adverseBarrierPrice": args.adverse_barrier,
        "side": args.side,
    }

    if args.entry_outcomes_jsonl:
        report["entryModeComparison"] = summarize_entry_modes(
            _jsonl(args.entry_outcomes_jsonl)
        )
    else:
        report["entryModeComparison"] = {
            "status": "not-provided",
            "modes": {},
        }

    write_report(args.output, report)
    print(json.dumps({"output": str(args.output), "sampleCount": len(rows)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
