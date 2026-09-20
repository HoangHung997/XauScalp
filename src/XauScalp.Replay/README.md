# XauScalp deterministic replay and first-passage research

XSP-008 replays the persisted market-event stream through the exact same `IXauFeatureEngine` implementation used by live processing. XSP-009 adds a **separate** future-label and entry-timing research phase.

## Core causality rule

```text
recorded MarketEvent stream
  -> ReplayEventScheduler
  -> same XauFeatureEngine
  -> ReplayOutputRecord
  -> deterministic replay hash
  -> separate FirstPassageLabelEngine / EntryModeExperiment
```

Future labels never enter `XauMarketState`, `IXauFeatureEngine`, or live feature computation.

## Replay ordering

- Source order is authoritative; replay never sorts or manufactures events.
- Equal timestamps preserve recorded source order.
- UTC timestamp regression fails closed.
- Upstream feed-gap markers stay explicit; missing ticks are never inferred.

## Timing modes

Timing changes wall-clock playback only:

- `Step`;
- `Realtime`;
- `Accelerated`.

It never changes feature or event order.

## First-passage labels

State-level labels use the ordered **mid-price** path after each causal market state. Mid is used here because the labels are direction-neutral research outcomes; execution-cost experiments separately use executable bid/ask.

For every configured horizon the label phase records:

- up/down first hit for each target distance (V1 includes 5 and 10 price units);
- target-first vs adverse-first vs censored for every target/adverse-barrier pair and Long/Short direction;
- Long MFE/MAE and times;
- mirrored Short MFE/MAE and times;
- whether the dataset ended before the requested horizon.

A future tick is scanned only after the source record that created the state. Ordered ticks, not M1 OHLC, decide which barrier occurred first.

## Entry-mode experiment

The experiment accepts externally supplied `EntryExperimentCandidate` values containing a market-state identity and an **intended side**. It does not create BUY/SELL opinions from features.

The same candidate is tested against all five modes:

1. `M1Close` — first causal tick at/after the setup M1 close boundary;
2. `IntrabarImmediate` — candidate tick;
3. `IntrabarDeceleration` — first state whose numeric opposing peak exists and `DecelerationRatio` crosses the configured threshold;
4. `IntrabarDirectionFlip` — first recent 500ms velocity sign flip into the supplied intended side;
5. `IntrabarMicroRetest` — ordered tick path advances, retraces, then resumes by configured price distances.

These are research triggers, not production winners or hard-coded trade rules.

## Cost-inclusive entry evaluation

Every mode for a run uses the exact same `ReplayCostScenario`.

For normalized one-lot comparison:

- Long entry = Ask + configured entry slippage;
- Long exit = Bid - configured exit slippage;
- Short entry = Bid - entry slippage;
- Short exit = Ask + exit slippage;
- slippage points are converted with the latest causal symbol `Point`;
- configured commission-per-lot is converted to price distance using `TickValue` and `TickSize`.

`NetResultPrice` therefore includes spread, two-sided slippage and the configured round-trip commission price equivalent.

## Metrics

For every mode × target × adverse barrier × holding horizon:

- candidate count / actual entry count / no-entry count;
- P(target first);
- false-entry rate = adverse-first or censored among actual entries;
- expected net price value after costs;
- average MFE / MAE;
- median time to target;
- profit factor, average win/loss;
- cumulative max drawdown in net price units.

All denominators are explicit.

## Export

`ResearchResultExporter` writes JSONL for:

- first-passage state labels;
- entry outcomes;
- entry-mode summaries.

## Determinism

Dataset and replay-output hashes remain SHA-256 over UTF-8 JSON lines. XSP-009 consumes those ordered outputs without modifying earlier feature snapshots.

Hand-crafted tests include two M1 paths with identical OHLC but opposite high/low order, proving OHLC alone cannot determine first passage.

No class in this project places broker orders or enables live trading.
