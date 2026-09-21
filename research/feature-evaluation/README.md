# XauScalp feature evaluation and ablation

XSP-016 evaluates candidate evidence after XSP-008 replay and XSP-009 first-passage labeling. It does **not** promote a feature because an external EA used it.

## Method

- Join replay feature states and future labels only by immutable `MarketStateId`.
- Exclude censored labels from supervised target-first/adverse-first classification and report the excluded count.
- Strict temporal train -> validation -> OOS; equal timestamps never cross a split boundary.
- Fit normalization on train only.
- Fit deterministic logistic baseline on train only.
- Fit Platt calibration on validation only.
- Report untouched OOS Brier, log loss, ECE, reliability buckets, target-rate Wilson 95% interval and sample counts.
- Walk-forward uses expanding train windows with separate validation/OOS slices.

## Common baseline

The intentionally small baseline is:

- `SpreadAtrRatio`;
- `AtrRatioM1`.

Candidate families are then tested with both:

1. add-one-family versus the same baseline on identical rows/time windows;
2. remove-one-family versus the same full candidate model on identical rows/time windows.

This preserves paired fairness even when some features are missing.

## Candidate families

High-priority families cover:

- velocity / acceleration / deceleration / direction flip / burst;
- sweep depth + close-back + micro-retest;
- magnet / pull / vacuum;
- absorption;
- FVG / BOS / displacement / order-block;
- regime / ADX / squeeze / HTF context.

## Status registry

A family becomes `Supported`, `Rejected`, or `Inconclusive` from paired OOS Brier evidence plus walk-forward stability.

These labels mean only **supported/rejected by the current replay evidence**. They are not profitability claims and are never based on EA provenance.

## Redundancy and breakdowns

The report includes pairwise Pearson correlation and flags |correlation| >= 0.90 when at least 30 paired samples exist.

Baseline OOS metrics are also broken down by:

- `SessionCode`;
- ATR-ratio volatility regime;
- spread/ATR bucket.

## M1-close vs intrabar

Optional XSP-009 entry-outcome JSONL is checked under one common cost scenario and summarized for exactly:

- `m1Close`;
- `intrabarImmediate`;
- `intrabarDeceleration`;
- `intrabarDirectionFlip`;
- `intrabarMicroRetest`.

No mode is declared the winner by product assumption.

## Example

```bash
python research/feature-evaluation/evaluate_features.py \
  --replay-jsonl data/replay.jsonl \
  --labels-jsonl data/labels.jsonl \
  --entry-outcomes-jsonl data/entry-outcomes.jsonl \
  --output artifacts/feature-evaluation.json
```
