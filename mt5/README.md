# MT5 bridges

XauScalp uses **two separate MQL5 bridges** so market-data capture is not coupled to order authority.

## 1. Market-data bridge

`XauScalpMarketBridge.mq5` is market-data-only.

It does not contain order placement, modification, position sizing, AI logic, or live-trading enablement.

Use:

1. Compile `XauScalpMarketBridge.mq5` in MetaEditor.
2. Attach it to the broker's actual gold symbol chart, for example `XAUUSD.G`.
3. Configure the C# mapping separately:
   - canonical symbol: `XAUUSD`
   - broker symbol: `XAUUSD.G`
4. Point `Mt5NdjsonFileTransport` at the terminal Common Files path for
   `XauScalp/mt5-wire-v1.ndjson`.

The bridge appends NDJSON frames and never rewrites the wire spool. Every frame has a source sequence persisted through a terminal Global Variable so EA/terminal restarts do not intentionally reset ordering.

See `protocol/MT5_BRIDGE_PROTOCOL.md`.

## 2. Demo execution bridge

`XauScalpDemoExecutionBridge.mq5` exists for the XSP-017 broker-demo gate.

It is **hard locked to MT5 demo accounts**:

- initialization fails when the account is not `ACCOUNT_TRADE_MODE_DEMO`;
- the account mode is rechecked while running;
- every command requires `demoOnly=true`;
- symbol, bridge session, magic number, position ticket and correlation ownership are checked fail-closed;
- uncertain dispatched commands are reconciled, never blindly resent.

The C# side is `Mt5DemoExecutionBrokerGateway` behind the existing `IExecutionBrokerGateway` boundary. Models still cannot call the broker directly and hard risk remains upstream of `ExecutionEngine`.

Default Common Files:

- `XauScalp/mt5-demo-execution-commands-v1.ndjson`
- `XauScalp/mt5-demo-execution-events-v1.ndjson`

See `protocol/MT5_DEMO_EXECUTION_PROTOCOL.md`.

### MetaEditor verification

GitHub Actions does not contain MetaEditor. The MQL5 source is code-reviewed in CI-adjacent work, but an actual broker-demo evidence run must include successful compilation in the target MT5 installation. Do not claim broker-demo readiness from .NET CI alone.

### Live money

Neither bridge authorizes live money. The demo execution bridge intentionally refuses it.


## Economic-calendar context

The market-data bridge also records causal MT5 economic-calendar availability for USD high-impact events into the same ordered NDJSON source stream. It records nearest past/upcoming distance or an explicit unavailable record with the MT5 error code. The feature engine never converts calendar failure into "no news"; unavailable news remains fail-closed and replay sees the exact same persisted event.
