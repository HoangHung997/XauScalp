# MT5 Bridge Protocol — mt5-wire-v1

## Purpose

This protocol transports raw market evidence from the MQL5 bridge to the .NET application. It has no order/execution commands.

The bridge writes newline-delimited JSON (NDJSON) to the MetaTrader common-files area. The .NET side tails the file through `Mt5NdjsonFileTransport`.

## Ordering

Every emitted bridge frame has a monotonically increasing `sequence`.

The sequence is stored in a MetaTrader terminal Global Variable named:

```text
XauScalp.Sequence.<broker-symbol>
```

Price equality and timestamp equality are **not** deduplication keys. Two ticks with identical prices and the same broker millisecond remain two observations when their source sequences differ.

The C# gateway does not sort arriving frames. If it observes:

- `observed > highest + 1`: it emits a `FeedGapEvent(MissingRange)`;
- `observed <= highest`: it emits a `FeedGapEvent(DuplicateOrOutOfOrder)`.

It then emits the actual received frame. Missing ticks are never manufactured.

## Frame types

### Tick

Required fields:

```json
{"type":"tick","sequence":42,"brokerSymbol":"XAUUSD.G","brokerTimeMsc":1789834600123,"bid":3680.10,"ask":3680.30,"last":0,"volume":7,"flags":6}
```

`flags` is the original MQL5 tick flag bitmask. The .NET gateway maps BID/ASK/LAST/VOLUME bits into the Domain `TickFlags`.

`last <= 0` becomes an unavailable/null last price rather than a fabricated value.

### Symbol specification

```json
{"type":"symbol","sequence":43,"brokerSymbol":"XAUUSD.G","digits":2,"point":0.01,"tickSize":0.01,"tickValue":1.0,"contractSize":100,"minVolume":0.01,"maxVolume":100,"volumeStep":0.01,"minStopDistance":0.50}
```

The bridge emits specification metadata on initialization and again after reconnect.

### Connection

```json
{"type":"connection","sequence":44,"brokerSymbol":"XAUUSD.G","state":"disconnected","reason":"terminal disconnected"}
```

Supported states are `connected`, `reconnecting`, and `disconnected`. The current file bridge directly observes connected/disconnected transitions; `reconnecting` is reserved for transports that can expose that state.

## Timestamps

- `brokerTimeMsc` is copied from `MqlTick.time_msc`.
- `MarketEvent.BrokerTimestamp` is created from that epoch-millisecond value.
- `MarketEvent.TimestampUtc` is stamped by the .NET gateway at ingestion using an injected UTC clock.

Later session/server-time normalization belongs to XSP-004. XSP-003 preserves the raw broker timestamp and does not rewrite tick history to make clock gaps look continuous.

## Symbol mapping

The MQL bridge emits the actual chart/broker symbol. The .NET gateway requires an explicit `BrokerSymbolMapping`, for example:

```text
Canonical: XAUUSD
Broker:    XAUUSD.G
```

A different broker symbol fails closed instead of being silently mixed into the dataset.

## Raw dataset

`AppendOnlyJsonlMarketEventStore` writes one canonical Domain `MarketEvent` JSON object per line.

Properties:

- append only;
- no update/delete/truncate API;
- write order is recorder order;
- malformed/truncated lines fail reading rather than being silently skipped;
- model selection is not part of recording.

Production default flushes every record. Tests/research bursts may explicitly use a larger flush batch and rely on disposal/final flush.

## Restart and recovery

The MT5 wire spool is itself append-only. The canonical raw dataset is also append-only.

For a clean application restart:

1. open the existing raw dataset;
2. read `GetLastSequenceIdAsync()`;
3. create `Mt5NdjsonFileTransport(..., startAfterSourceSequenceId: lastSequence)`;
4. replay the bridge spool from the beginning while skipping its already-recorded monotonic prefix;
5. append only newer frames.

This favors correctness over startup speed. A future indexed checkpoint may optimize the scan, but it must not weaken raw-history integrity.

If the bridge sequence regresses (for example, its terminal Global Variable was manually removed), the gateway emits a sequence anomaly instead of silently treating the data as continuous.

## Partial writes

The .NET file transport buffers characters until a newline is present. It never parses an unterminated tail as a completed frame. In finite/offline mode an incomplete final frame is an error.

## Safety

This protocol is market-data-only. It contains no API key, password, broker login, order request, lot sizing, or risk bypass.


## News context frames

The market-data bridge emits ordered `type=news` frames from the MT5 economic calendar. The frame contains:

- the same monotonic source sequence as ticks/specification/connection frames;
- broker symbol;
- `available`;
- seconds to nearest upcoming USD high-impact event;
- seconds since nearest past USD high-impact event;
- source identity;
- MT5 source error code when unavailable.

When the MT5 calendar cannot be queried, distances are `null` and `available=false`. Consumers must not substitute zero or claim "no news". Because the frame is persisted with raw market evidence, live and replay use identical causal news semantics.
