# Tick Replay and Research Protocol

## 1. Purpose

The replay system is the scientific core of XauScalp.

Its job is not to make a backtest look good. Its job is to answer, on the same ordered XAU data, whether a candidate feature, entry timing method, model, or exit policy improves outcomes after realistic costs.

## 2. Required data granularity

### Intrabar studies
Must use ordered ticks.

M1 OHLC is not sufficient because it cannot tell whether high or low occurred first.

### Higher-timeframe studies
Bars may be derived from recorded ticks or loaded from trusted historical data, but their time alignment must be reproducible.

## 3. Raw tick record

Minimum tick fields:

```text
SequenceId
TimestampUtc
BrokerTimestamp
Bid
Ask
Last (if available)
TickVolume or flags (if available)
SourceId
Symbol
BrokerSymbol
```

Optional:
- real DOM snapshot reference;
- connection/session state;
- feed gap marker.

Ticks must be append-only.

## 4. Canonical replay ordering

Ordering precedence:

1. source sequence number when trustworthy;
2. broker millisecond timestamp;
3. ingestion sequence.

When timestamps collide, preserve original ingestion order.

## 5. Causality rule

At replay time `T`, feature code may only see events with time/sequence <= `T`.

Forbidden:
- using final M1 high/low before the candle closes;
- using future volume;
- using a later swing confirmation to pretend a swing was known earlier;
- using future FVG mitigation status in an earlier feature;
- using final session high/low before the session has produced it.

Every structure/liquidity feature needs a causal definition.

## 6. Research target definitions

Primary research price targets:

```text
+5 price units
-5 price units
+10 price units
-10 price units
```

"Price unit" must be represented as an actual XAU price distance, not broker points. Conversion belongs in symbol normalization.

Because stop size is not yet fixed, label generation must support a configurable adverse-barrier matrix, for example:

```text
2, 3, 4, 5 price units
```

Do not hard-code one stop into the dataset.

## 7. First-passage labels

For each research state at T0, compute:

```text
Up5HitTime
Down5HitTime
Up10HitTime
Down10HitTime

MFE
MAE
TimeToMFE
TimeToMAE
```

For each configured adverse barrier:

```text
P-target label:
Did +5 occur before -barrier?
Did -5 occur before +barrier?
Did +10 occur before -barrier?
Did -10 occur before +barrier?
```

If neither side is reached before the maximum research horizon, label as censored/timeout rather than forcing a win/loss.

Initial maximum holding horizons should be configurable (e.g. 1, 3, 5, 10, 15 minutes). Do not assume one is optimal.

## 8. Entry timing experiment

The following must be testable on identical candidate setups:

### A. M1_CLOSE
Decision only after bar close using causal closed-bar features.

### B. INTRABAR_IMMEDIATE
Enter at first eligible intrabar state.

### C. INTRABAR_DECELERATION
Condition already exists, opposing/impulse velocity reaches a peak, then falls by a configured relative amount.

### D. INTRABAR_DIRECTION_FLIP
Opposing velocity reaches a peak, decays toward zero, and measurable velocity appears in the intended trade direction.

### E. INTRABAR_MICRO_RETEST
Initial reversal/continuation move occurs, price retests the relevant micro level, then resumes.

These are experimental modes. None is the default winner until replay evidence says so.

## 9. Cost model

Every replay report must declare:

- actual recorded spread when available;
- commission model;
- swap irrelevant/zero for very short holds unless broker applies it;
- slippage scenario;
- execution latency scenario.

At minimum provide:
- ideal/no-added-slippage research;
- realistic baseline;
- stressed cost scenario.

A strategy that only survives zero-cost replay is not considered viable.

## 10. Broker normalization

Store both:
- normalized price distances;
- raw broker points/tick size.

Research code must handle:
- symbol suffixes;
- different XAU digits;
- tick-size differences;
- contract/tick-value differences.

Do not compare "50 points" across brokers without normalization.

## 11. Temporal validation

No random train/test shuffle for time-series decision claims.

Minimum process:

```text
Train window
 -> Validation window
 -> freeze parameters/model
 -> Out-of-sample window
 -> walk forward
```

Report per window and aggregate.

## 12. Feature evaluation

For every feature/family:

1. verify no leakage;
2. inspect missing/stale rates;
3. univariate outcome relationship;
4. redundancy/correlation;
5. add to common baseline;
6. ablate from common baseline;
7. repeat across walk-forward windows;
8. report by session/regime/spread bucket.

A feature is promoted because it adds stable out-of-sample information, not because its chart looks convincing.

## 13. Required metrics

At minimum:

```text
Sample count
Trade/event count
P(+5 first)
P(+10 first)
P(adverse first)
Expected value after costs
Profit factor (when strategy simulation is used)
Win rate
Average win
Average loss
MFE
MAE
Median time to +5
Median time to +10
False-entry rate
Max drawdown
Exposure time
Spread/slippage cost
Calibration metrics for probability models
```

Always show denominator/sample size.

## 14. Selection-bias warning

Two report types must remain separate:

### State-level model quality
Score predictions on all eligible research states.

### Executed-trade quality
Score only trades that passed risk/execution and were actually simulated/executed.

Do not infer model quality solely from production trades; they are a filtered subset.

## 15. Determinism

A replay run must record:

```text
RunId
DataSetId/hash
FeatureSchemaVersion
FeatureEngineVersion
ModelId/version/hash
Settings hash
Cost scenario
Code commit
Start/end timestamps
Random seed where applicable
```

The same run manifest should reproduce the same results within defined numeric tolerance.

## 16. Data splits and leakage tests

Automated tests must include:
- feature state cannot access later ticks;
- final bar values unavailable before close;
- session high/low only grows causally;
- swing confirmation timestamps are respected;
- labels are generated in a separate phase and are never present in live feature objects.

## 17. Promotion language

Use:
- Candidate;
- Supported by current replay;
- Rejected;
- Inconclusive;
- Shadow-ready;
- Demo-ready.

Do not label a feature "profitable" from one backtest window.
