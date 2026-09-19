# Definition of Done

## 1. General task DoD

A task is not complete because code compiles.

Minimum:
- acceptance criteria satisfied;
- automated tests added;
- no known critical bug hidden in comments;
- logging/telemetry added where operationally relevant;
- documentation updated;
- no secrets committed;
- code reviewed against this product specification;
- CI green.

## 2. Market data DoD

- ordered ticks recorded;
- reconnect/feed-gap markers exist;
- symbol metadata stored;
- UTC/broker-time behavior tested;
- duplicate/out-of-order handling tested;
- replay can read the exact persisted format.

## 3. Feature engine DoD

- deterministic;
- causal;
- unit documented;
- missing-data semantics defined;
- live/replay parity test;
- no future bar values;
- schema version emitted;
- golden test vectors for critical features.

## 4. Intrabar feature DoD

For velocity/deceleration/direction-flip:
- defined mathematically;
- tested on synthetic tick sequences;
- tested on irregular tick spacing;
- spread/bid/mid choice documented;
- no M1-close dependency accidentally introduced.

## 5. Replay DoD

- same input dataset produces same results;
- run manifest complete;
- first-passage labels verified by hand-crafted sequences;
- MFE/MAE verified;
- costs configurable;
- no-lookahead test suite passes;
- M1-close and intrabar experiments run from identical source data.

## 6. JEV adapter DoD

- contract tests against fake provider;
- timeout;
- cancellation;
- malformed response handling;
- bounded retry;
- stale-response rejection;
- API key not logged;
- pinned model/version captured;
- provider outage results in configured safe behavior.

## 7. XAU Native DoD

- temporal data split;
- no leakage;
- reproducible training manifest;
- artifact hash;
- calibration report;
- out-of-sample report;
- runtime output schema parity;
- inference latency measured;
- missing-feature behavior tested.

## 8. Risk DoD

Unit tests must cover:
- tick size/value conversion;
- min/max lot;
- volume step;
- min stop distance;
- spread block;
- daily loss lock;
- cooldown;
- stale state;
- foreign-position isolation;
- restart reconstruction.

No critical risk decision may depend only on UI state.

## 9. Execution DoD

Test:
- accepted fill;
- rejection;
- requote/price change;
- timeout/unknown;
- retry without duplicate order;
- partial fill where supported;
- modify failure;
- close failure;
- restart with open position;
- reconnect;
- broker position not found locally;
- local position not found at broker.

## 10. Shadow mode DoD

- shadow model receives identical state;
- shadow cannot submit order;
- outputs persisted;
- future labels can be attached later;
- primary/shadow report distinguishes all-state scoring from executed-trade scoring.

## 11. UI DoD

Settings visibly contain:
- JEV;
- XAU Native AI;
- one selected primary;
- shadow toggle;
- JEV failure policy;
- model version/status;
- risk settings.

UI must not create a third model option.

## 12. Demo readiness

Before demo autonomy:
- at least one complete tick dataset recorded and replayed;
- feature parity demonstrated;
- cost model enabled;
- hard stop/risk lock verified;
- outage/restart drills pass;
- evidence pack generated;
- no known P0/P1 correctness issue.

## 13. Live readiness

Live-money authorization is separate from software completion.

It requires explicit human approval after:
- demo/shadow evidence;
- broker-specific spread/slippage characterization;
- model calibration;
- risk-limit review;
- incident/recovery drill.

No developer may silently enable live trading as part of feature completion.
