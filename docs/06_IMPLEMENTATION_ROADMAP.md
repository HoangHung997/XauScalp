# Implementation Roadmap and Work Order

This file defines build order. GitHub Issues are the execution units.

## Phase 0 — Repository and contracts

### XSP-001 Bootstrap solution, CI, formatting, tests
Deliver:
- .NET solution/project skeleton;
- test projects;
- build/test workflow;
- coding conventions;
- no trading logic yet.

### XSP-002 Domain contracts and settings
Deliver:
- `XauMarketState`;
- `XauDecision`;
- market events;
- risk decision;
- trade plan;
- execution result;
- two-model setting and fallback setting;
- serialization/versioning tests.

Dependency: XSP-001.

## Phase 1 — Trustworthy data foundation

### XSP-003 MT5 gateway and raw tick recorder
Deliver:
- MT5 bridge/gateway;
- ordered tick ingestion;
- connection/reconnect events;
- append-only tick persistence;
- symbol metadata capture.

Dependencies: XSP-001, XSP-002.

### XSP-004 Bar aggregation and time normalization
Deliver:
- causal M1/M5/M15/H1 bars;
- forming M1 state;
- closed-bar semantics;
- broker/UTC/session time normalization;
- tests for boundary transitions.

Dependency: XSP-003.

## Phase 2 — Feature engine

### XSP-005 P0 execution/live-M1/microstructure features
Deliver:
- spread/cost features;
- live M1 fields;
- tick windows;
- velocity;
- acceleration;
- deceleration;
- direction flip;
- burst z-score;
- deterministic replay parity tests.

Dependencies: XSP-003, XSP-004.

### XSP-006 P0 liquidity/reaction features
Deliver:
- causal swings;
- sweep depth;
- close-back;
- equal high/low;
- estimated magnets/vacuum/absorption;
- reaction sequence features;
- explicit real-vs-estimated liquidity source.

Dependency: XSP-005.

### XSP-007 P0 structure/regime/context features
Deliver:
- BOS/MSS/CHoCH definitions;
- FVG metrics;
- order-block distance/overlap candidate;
- ATR ratios;
- compression/squeeze;
- ADX;
- M5/M15/H1 context.

Dependency: XSP-005.

## Phase 3 — Replay laboratory

### XSP-008 Deterministic tick replay engine
Deliver:
- tick event scheduler;
- same feature engine as live;
- run manifest/hash;
- cost scenarios;
- no-lookahead tests.

Dependencies: XSP-004, XSP-005.

### XSP-009 Label engine and entry-mode experiment
Deliver:
- +5/+10 first-passage labels;
- adverse barrier matrix;
- MFE/MAE;
- time-to-target;
- M1_CLOSE vs intrabar modes;
- censored outcomes;
- exportable results.

Dependency: XSP-008.

## Phase 4 — Runtime safety and execution

### XSP-010 Risk engine and sizing
Deliver:
- tick-value/tick-size-based sizing;
- daily limits;
- spread/news/freshness guards;
- ownership isolation;
- test matrix.

Dependency: XSP-002.

### XSP-011 Execution state machine and reconciliation
Deliver:
- idempotent trade intents;
- submit/modify/close;
- broker reconciliation;
- restart recovery;
- slippage/latency recording.

Dependencies: XSP-003, XSP-010.

## Phase 5 — Decision models

### XSP-012 JEV adapter
Deliver:
- provider-independent adapter implementation;
- typed request/response;
- validation;
- timeout/circuit breaker;
- version pinning;
- secrets handling;
- fake provider tests.

Dependencies: XSP-002, XSP-005.

### XSP-013 XAU Native baseline training and inference
Deliver:
- temporal split;
- baseline calibrated model;
- artifact manifest;
- ONNX/local inference or equivalent stable runtime;
- output contract parity with JEV.

Dependencies: XSP-005, XSP-008, XSP-009.

### XSP-014 Shadow comparator and decision telemetry
Deliver:
- primary/shadow invocation;
- no-execution guarantee for shadow;
- state/decision/future-label linkage;
- comparison report.

Dependencies: XSP-012, XSP-013.

## Phase 6 — App and research workflow

### XSP-015 Desktop settings/status UI
Deliver:
- exactly two primary model options;
- JEV configuration;
- XAU Native configuration;
- fallback policy;
- shadow toggle;
- risk settings;
- data/model readiness/status.

Dependencies: XSP-002, XSP-012, XSP-013.

### XSP-016 Feature evaluation and ablation pipeline
Deliver:
- common baseline;
- feature/family ablation;
- calibration;
- session/regime breakdown;
- walk-forward report;
- candidate status registry.

Dependencies: XSP-009, XSP-013.

### XSP-017 Demo-readiness integration gate
Deliver:
- end-to-end replay;
- broker demo/shadow run;
- restart/reconnect tests;
- stale-data/model outage tests;
- evidence pack;
- go/no-go checklist for controlled demo.

Dependencies: all critical-path tasks above.

## Critical path

```text
001
 -> 002
 -> 003
 -> 004
 -> 005
 -> 008
 -> 009
 -> 013
 -> 014
 -> 017
```

Parallel branches:
- 006/007 after 005;
- 010 after 002;
- 011 after 003+010;
- 012 after 002+005;
- 015 after model adapters.

## Implementation priority rule

Do not spend time polishing UI while raw data/replay causality is untrusted.

The first meaningful milestone is:
**record ticks -> replay identical ticks -> regenerate identical features**.

The second is:
**generate trustworthy first-passage labels and compare entry modes**.

The third is:
**run JEV and XAU Native through the same contract in shadow/replay**.
