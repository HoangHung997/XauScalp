# XauScalp deterministic replay

XSP-008 replays the persisted market-event stream through the exact same `IXauFeatureEngine` implementation used by live processing.

## Core rule

Replay is an evidence pipeline, not a strategy simulator.

```text
recorded MarketEvent stream
  -> ReplayEventScheduler
  -> same XauFeatureEngine
  -> ReplayOutputRecord
  -> streaming sink + deterministic hash
```

Future labels are not accepted by the replay runner. First-passage labels belong to XSP-009 and are computed in a separate phase after feature snapshots exist.

## Ordering and causality

- Source order is authoritative; replay never sorts or manufactures events.
- Equal timestamps preserve the recorded source order.
- A UTC timestamp regression fails closed instead of silently reordering evidence.
- The feature engine receives one event at a time and therefore cannot inspect a future tick.
- Non-tick context events update feature context; feature snapshots are emitted only for actual `TickEvent` records.
- Current/forming M1 semantics remain those of XSP-004/XSP-005; final bar values are not injected early.

Upstream feed-gap/anomaly markers remain records in the replay output. Replay does not infer missing ticks.

## Timing modes

Timing changes wall-clock playback only; it never changes event order or feature semantics.

- `Step`: each recorded event requires a step gate release.
- `Realtime`: waits the recorded UTC delta between consecutive events.
- `Accelerated`: waits recorded delta divided by a positive acceleration factor.

Tests inject a fake delay so timing policy can be verified without slowing CI.

## Cost-scenario hook

Every run declares a `ReplayCostScenario` containing:

- scenario name;
- optional estimated latency;
- optional estimated slippage;
- commission per lot.

Raw recorded spread is never rewritten. A causal `IReplayFeatureContextProvider` receives the current event plus cost scenario and may produce a `FeatureExternalContext` for that event. This keeps cost assumptions explicit without mutating the historical feed.

## Manifest

The final `ReplayRunManifest` records:

- deterministic run ID;
- dataset SHA-256 / dataset ID;
- feature schema and feature-engine versions;
- model ID/version/artifact hash (explicit `none` before model stages);
- settings version/hash;
- cost scenario;
- timing mode/factor;
- code commit;
- start/end timestamps;
- event/tick counts;
- random seed.

## Hashes and numeric tolerance

Dataset and replay-output hashes are SHA-256 over UTF-8 canonical JSON lines in recorded order.

For the same .NET runtime, code, settings, cost scenario, feature engine, and input stream, hash equality is expected exactly.

When diagnosing cross-runtime floating-point behavior, numeric feature comparison tolerance is `1e-12`; this tolerance is diagnostic only and is **not** used to hide an output-hash mismatch.

## Streaming exports

`JsonlReplayRecordSink` writes each event plus the feature snapshot generated at that event without holding the whole dataset in memory. `InMemoryReplayRecordSink` exists for tests and small research runs.

No replay class in XSP-008 places orders, chooses trade direction, or contains future outcome labels.
