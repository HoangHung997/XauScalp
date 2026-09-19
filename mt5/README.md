# MT5 market-data bridge

XSP-003 adds a **market-data-only** MQL5 bridge: `XauScalpMarketBridge.mq5`.

It does not contain order placement, modification, position sizing, AI logic, or live-trading enablement.

## Use

1. Compile `XauScalpMarketBridge.mq5` in MetaEditor.
2. Attach it to the broker's actual gold symbol chart, for example `XAUUSD.G`.
3. Configure the C# application mapping separately:
   - canonical symbol: `XAUUSD`
   - broker symbol: `XAUUSD.G`
4. Point `Mt5NdjsonFileTransport` at the terminal common-files path for:
   `XauScalp/mt5-wire-v1.ndjson`.

The bridge appends NDJSON frames and never rewrites the wire spool. Every frame has a source sequence persisted through a terminal Global Variable so EA/terminal restarts do not intentionally reset ordering.

See `protocol/MT5_BRIDGE_PROTOCOL.md` for frame schemas, gap handling, and recovery.
