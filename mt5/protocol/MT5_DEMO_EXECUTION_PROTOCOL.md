# MT5 demo execution protocol v1

Protocol version: `xau-mt5-demo-exec-v1`

This protocol exists only to connect the production `ExecutionEngine` to an **MT5 demo account** for XSP-017 broker-demo evidence. It is not a live-money transport.

## Safety boundary

`XauScalpDemoExecutionBridge.mq5` fails initialization unless MT5 reports `ACCOUNT_TRADE_MODE_DEMO`.

Every command also carries `demoOnly=true`. The bridge re-checks demo account mode before processing commands and rejects a command when:

- account mode is not demo;
- protocol version does not match;
- bridge session id is stale/different;
- broker symbol differs from the attached chart symbol;
- magic number differs from the bridge input;
- position ownership checks fail.

The C# transport independently requires fresh `bridgeReady` + `bridgeHeartbeat` records with `demoAccountVerified=true` before it appends a command.

Passing XSP-017 never enables live money.

## Common files

Both sides use the MT5 terminal **Common Files** directory:

- commands: `XauScalp/mt5-demo-execution-commands-v1.ndjson`
- events: `XauScalp/mt5-demo-execution-events-v1.ndjson`

The files are append-only NDJSON. Do not edit or truncate them while a demo session is running. Archive them between evidence sessions when a clean evidence bundle is desired.

The market-data bridge remains separate and continues to write `XauScalp/mt5-wire-v1.ndjson`.

## Bridge session handshake

At startup the MQL5 bridge emits:

- `bridgeReady`;
- `bridgeHeartbeat` periodically.

Both include:

- `protocolVersion`;
- `bridgeSessionId`;
- `demoAccountVerified`;
- `brokerSymbol`;
- `magicNumber`;
- UTC heartbeat time.

The C# transport binds each new command to the currently fresh session. Old commands from a previous EA/terminal session cannot execute in the new session.

## Command envelope

Commands contain:

- unique `commandId`;
- active `bridgeSessionId`;
- operation: `submit | modify | close | queryState`;
- `TradeIntentId` where applicable;
- broker symbol;
- ownership magic;
- shortened broker comment derived from `TradeIntentId`;
- volume/side/protection fields where applicable;
- configured max slippage;
- `demoOnly=true`.

The full `TradeIntentId` remains in the command spool. The short MT5 comment is only correlation metadata and is not the authoritative local identifier.

## Durable dispatch marker

Before `submit`, `modify`, or `close`, the bridge appends a `dispatching` event **before** calling `CTrade`.

If the bridge restarts after that marker but before a final response:

- it never blindly resends the old command;
- submit recovery searches owned broker position/order/history by symbol + magic + correlation comment;
- modify/close recovery reports an unknown outcome and requires reconciliation.

This matches the `ExecutionEngine` rule that an uncertain broker outcome becomes `UnknownNeedsReconciliation`, not an automatic duplicate submission.

## Execution response

`executionResult` contains:

- command/session/operation/TradeIntent identity;
- outcome: `accepted | partiallyFilled | filled | rejected | requote | unknown`;
- broker order/deal/position identifiers when available;
- requested/fill price;
- requested/filled volume;
- measured bridge call latency;
- observed slippage points when measurable;
- broker retcode/message;
- `safeToRetry`.

The C# adapter further suppresses `safeToRetry` whenever any broker order/deal/position identity is present.

## Reconciliation state

`queryState` returns a `state` event containing owned:

- positions;
- pending orders;
- known closed trade intents.

Ownership filtering requires the configured broker symbol and magic. Modify/close additionally require the exact position ticket and correlation comment.

An owned broker position/order without a known full `TradeIntentId` is represented by the C# gateway as a deterministic orphan identity, causing reconciliation to fail closed rather than silently importing or managing it as a known local trade.

## Broker/account-mode limitations to record in evidence

Actual XSP-017 evidence must record the target broker and account mode behavior. In particular:

- MT5 hedging vs netting semantics can affect position identity/comment behavior;
- a broker may alter/truncate comments;
- broker fill policy and retcodes vary;
- commission may not be known at submit time;
- Common Files must be accessible to both the EA and C# process.

If correlation metadata is not preserved by the target broker, the bridge must fail closed; do not weaken ownership checks just to make the drill pass.

## Secrets

No account number, password, API key, access token, or credential is part of this protocol or evidence files.
