# XAU Native offline training

Production inference is implemented in `src/XauScalp.DecisionModels`. Python is used only for offline training/evaluation.

## Inputs

The trainer consumes two XSP-008/XSP-009 JSONL exports:

1. replay records containing causal `featureState` snapshots;
2. first-passage labels.

Rows are joined only by immutable `MarketStateId`. Future labels are never copied into `XauMarketState`.

Example:

```bash
python research/training/xau_native_baseline.py \
  --replay-jsonl data/replay.jsonl \
  --labels-jsonl data/labels.jsonl \
  --output-dir artifacts/xau-native-v0 \
  --model-version xau-native-v0-001 \
  --code-commit <git-sha>
```

## Temporal validation

The V0 trainer:

- sorts by UTC time;
- never randomly shuffles time-series samples;
- never splits identical timestamps across windows;
- fits feature normalization on train only;
- fits logistic coefficients on train only;
- fits Platt calibration on validation only;
- reports untouched OOS Brier score, log loss, ECE, reliability buckets, and sample counts;
- reports OOS breakdown by SessionCode and an M1RangeAtrRatio regime bucket when those fields are available.

The default split is 60% train / 20% validation / 20% OOS.

## V0 targets

Learned from XSP-009 first-passage labels:

- `up5First`;
- `down5First`;
- `up10First`;
- `down10First`;
- `longAdverseFirst`;
- `shortAdverseFirst`.

`continuation`, `reversal`, and `falseBreak` remain explicit 0.5 constant heads until their target semantics are separately defined. The artifact records them as `constant-untrained`.

## Outputs

The trainer writes:

- `<version>.json` — stable linear artifact;
- `<version>.artifact-manifest.json` — artifact SHA-256 and compatibility identity;
- `<version>.training-manifest.json` — source hashes, temporal split, features, trainer version, commit and settings;
- `<version>.oos-report.json` — calibration/OOS evidence.

A better training metric alone is not a production-promotion signal. Real promotion still requires frozen OOS, walk-forward, cost-inclusive replay and shadow evidence.

## Fixtures

`fixtures/xau-native-fixture.json` is synthetic and exists only to prove Python ↔ C# numerical parity and artifact provenance. It is not XAU edge evidence and must not be promoted.
