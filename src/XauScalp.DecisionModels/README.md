# XAU Native decision-model runtime

This project owns provider-independent decision-model adapters and the local XAU Native inference runtime.

## XAU Native V0

XSP-013 starts with a calibrated linear/logistic multi-head baseline rather than a complex neural network. The artifact is language-neutral JSON and production inference is pure .NET 8.

The runtime:

- consumes only the immutable `XauMarketState`;
- requires the exact artifact `FeatureSchemaVersion`;
- fails closed on stale/not-ready or missing required P0 features;
- validates artifact identity and SHA-256 against its manifest;
- returns the shared `XauDecision` contract;
- never accesses MT5, chooses lot size, changes risk limits, or submits an order.

Learned V0 heads are:

- +5 first;
- -5 first;
- +10 first;
- -10 first;
- Long adverse-first;
- Short adverse-first.

Continuation, reversal, and false-break remain explicit constant baseline heads until those target semantics are separately defined and trained. They are **not** represented as learned evidence.

The action is derived inside the model adapter from calibrated first-passage edge:

```text
long edge  = P(+5 first) - P(long adverse first)
short edge = P(-5 first) - P(short adverse first)
```

If neither exceeds the artifact's versioned minimum edge, the model returns `Wait`.

## Artifact safety

The committed artifact under `research/training/fixtures` is a **synthetic contract/parity fixture only**. It must never be promoted as an XAU trading model or performance claim.

Real artifacts must be produced by the offline training pipeline from replay/label exports and retain:

- feature schema version;
- temporal split metadata;
- dataset hashes;
- trainer version and seed;
- calibration/OOS report;
- artifact SHA-256.

ONNX remains an allowed future runtime format. V0 JSON linear inference is intentionally stable, dependency-light, and independently verifiable in both Python and C#.
