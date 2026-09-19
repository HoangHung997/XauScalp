# Decision Models: JEV and XAU Native AI

## 1. Fixed product decision

The app supports exactly two primary decision models:

- `Jev`
- `XauNative`

This is a product-level setting, not two separate trading applications.

## 2. Shared input

Both receive the exact same immutable `XauMarketState` snapshot.

A model must not query MT5/broker state independently because that would make JEV and XAU Native incomparable.

## 3. Shared output contract

Suggested domain shape:

```csharp
public enum TradeAction
{
    Wait = 0,
    Long = 1,
    Short = -1
}

public sealed record XauDecision(
    TradeAction Action,
    double ActionProbability,
    double PUp5First,
    double PDown5First,
    double PUp10First,
    double PDown10First,
    double PAdverseBarrierFirst,
    double PContinuation,
    double PReversal,
    double PFalseBreak,
    double Confidence,
    double? PHold,
    double? PExitNow,
    double? PTp5FromHere,
    double? PTp10FromHere,
    string ModelId,
    string ModelVersion,
    string FeatureSchemaVersion,
    DateTimeOffset EvaluatedAtUtc,
    TimeSpan EvaluationLatency);
```

Probabilities must be bounded [0,1]. The application must validate them before use.

## 4. JEV adapter

Responsibilities:
- map `XauMarketState` to provider request;
- request typed outputs;
- parse/validate response;
- enforce timeout;
- expose provider/model version;
- log request metadata without leaking secrets;
- support circuit breaker;
- never place trades.

Implementation requirements:
- API key stored using OS-appropriate secret storage, not committed settings;
- production version pinned;
- request/response schema versioned;
- retries must be bounded and must not create multiple trading decisions for one state;
- stale response must be rejected;
- provider failure must be visible to runtime and telemetry.

Do not hard-code product logic into the JEV prompt/adapter such as "FVG means buy". Send market evidence.

## 5. JEV evaluation cadence

JEV should normally be event-driven to control latency/cost.

Candidate triggers:
- sweep;
- magnet/major level touch;
- velocity peak/deceleration;
- direction flip;
- micro-retest;
- new M1 bar;
- large spread normalization;
- periodic maximum-staleness refresh.

Trigger policy is researchable and versioned.

## 6. XAU Native AI

Purpose: local XAU-specialized numeric decision model.

### 6.1 V0 baseline

Before a complex network, implement a baseline that proves:
- feature ingestion;
- training split;
- calibration;
- export;
- C# inference;
- deterministic versioning.

A calibrated logistic model, gradient-boosted model exported appropriately, or small MLP is acceptable for V0.

### 6.2 V1 multi-head design

Preferred conceptual architecture:

```text
XauMarketState
      |
Shared Numeric Encoder
      |
  +---+---+---+---+---+
  |   |   |   |   |   |
Act  +5  +10  Rev Cont FalseBreak
```

Potential heads:
- action classification;
- +5/-5 first-passage;
- +10/-10 first-passage;
- adverse-first;
- continuation;
- reversal;
- false-break;
- confidence/calibration.

The exact neural architecture is a research decision. Do not make it a product dependency.

## 7. Calibration

A probability-producing model must be evaluated for calibration, not only accuracy.

Report:
- Brier score;
- reliability/calibration curves;
- log loss where applicable;
- expected calibration error or equivalent;
- probability-bucket realized outcomes.

A model that says 0.80 must be tested for whether comparable events realize near that frequency out-of-sample.

## 8. Primary/shadow comparison

For each eligible state:

```text
StateId
Primary model decision
Shadow model decision
Future labels
Execution result if a trade was actually taken
```

Scoring must distinguish:
- decision quality on all research states;
- production trade outcomes, which are selection-biased by risk/execution filters.

## 9. Failure policy

Settings:

```text
PrimaryDecisionModel = Jev | XauNative
ShadowComparisonEnabled = true/false
JevFailurePolicy = StopNewTrades | FallbackToXauNative
```

If JEV fails:
- no silent fallback;
- log reason;
- if fallback enabled, label decision source as fallback;
- fallback model must satisfy freshness/schema checks.

If XAU Native fails:
- default no new trade;
- do not fall back to JEV unless a future product decision explicitly adds that behavior.

## 10. Model promotion

A new model version cannot replace production merely because training metrics improved.

Required evidence:
- frozen out-of-sample segment;
- walk-forward evaluation;
- cost-inclusive replay;
- probability calibration;
- regime/session breakdown;
- shadow run;
- no risk-control regression;
- reproducible model artifact hash and training manifest.

## 11. No direct model control of risk

Model may express probability/confidence. It may not:
- choose account risk percentage;
- override max daily loss;
- enlarge a hard stop beyond product risk rules;
- open multiple rescue positions;
- disable spread/news/latency guards.

Those remain deterministic application policy.
