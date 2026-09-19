# XauScalp.MarketData

## XSP-004 bar/time semantics

`CausalBarAggregator` is the single bar-building implementation intended for both live processing and replay.

### Causality

- Input is the recorded/ordered `TickEvent` stream.
- UTC `TickEvent.TimestampUtc` defines M1/M5/M15/H1 bucket boundaries.
- V1 OHLC bars use **Bid** price, matching the broker-chart price stream; ask/spread remain separate evidence.
- The aggregator never sorts input and rejects a UTC timestamp regression.
- Duplicate timestamps are valid and are processed in arrival order.
- A future tick never updates an earlier closed bar.
- `FormingBarState` and `ClosedBarState` are different Domain types.
- A bar is closed when the first later tick proves its UTC bucket has ended.
- If no ticks occur for one or more buckets, no empty candles are invented.
- The tick that crosses a boundary triggers `Closed` then `Opened` events for that timeframe.
- Derived bar events keep the triggering tick sequence id for deterministic provenance/order.

### Current M1 age/progress

`GetFormingSnapshot(M1, asOfUtc)` exposes:

- immutable forming state as known now;
- age from the UTC bucket open;
- progress clamped to `[0,1]`;
- whether the nominal bucket boundary has elapsed.

The snapshot never substitutes a final H/L/C before a `ClosedBarState` exists.

### Broker/session clock

Bar buckets are UTC and therefore do not shift when a broker changes server/DST offset.

`MarketClockNormalizer` separately derives broker wall-clock/session context from an explicit, versioned list of effective UTC-offset segments. This makes broker daylight/server-time changes reproducible in replay without relying on the host machine's current timezone database.

The raw `TickEvent.BrokerTimestamp` is retained as source metadata and is not rewritten.

### Missing data

Feed gaps remain explicit `FeedGapEvent` evidence from XSP-003. The bar builder never fills missing ticks or missing candles.
