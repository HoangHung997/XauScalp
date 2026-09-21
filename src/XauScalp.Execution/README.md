# XauScalp execution lifecycle

XSP-011 provides the safe order/position state machine after a trade has already passed hard risk authorization.

## Idempotency

Every plan has a stable `TradeIntentId`.

- Reusing the same ID with a different plan fails closed.
- Reusing the same ID in an active/uncertain lifecycle triggers broker reconciliation, not another submit.
- Explicit broker reject/requote is retried only when the broker adapter marks it `SafeToRetry`.
- Timeout/transport/unknown exceptions are treated as potentially accepted by the broker and become `UnknownNeedsReconciliation`.

The engine never blindly resends an unknown order.

## Effective state vs command result

The append-only journal stores both:

- broker-facing `ExecutionResult.State`;
- local `EffectiveState`.

This matters for modify/close failures. A rejected modification can return `Failed` while the position remains effectively `Open`; the system does not lose ownership of the live position.

## Reconciliation

Startup/reconnect reconciliation queries broker positions/orders/known-closed intents.

For every local active or uncertain intent it tries to prove one of:

- owned broker position -> `Open`;
- owned pending broker order -> `Accepted`;
- broker history says closed -> `Closed`;
- otherwise it remains unsafe/unknown and new entry readiness is false.

Owned broker positions/orders without local history are reported as unsafe orphans.

Foreign positions are never imported or managed.

## Ownership

Modify/close management requires exact:

- magic number;
- runtime instance ID;
- strategy ID;
- TradeIntentId;
- broker position identity when known.

## MT5 demo adapter

XSP-017 adds `Mt5DemoExecutionBrokerGateway` and `Mt5DemoFileExecutionTransport` as a broker-specific adapter behind `IExecutionBrokerGateway`.

The adapter is intentionally demo-only:

- requires a fresh, demo-verified bridge heartbeat before writing a command;
- binds commands to the current bridge session;
- validates configured broker symbol and ownership;
- treats timeout after durable command append as unknown, forcing broker reconciliation;
- maps unknown owned broker positions/orders to deterministic orphan identities so unsafe state cannot be ignored;
- never grants execution authority to a decision model.

The MQL5 peer is `mt5/XauScalpDemoExecutionBridge.mq5`. Protocol details are in `mt5/protocol/MT5_DEMO_EXECUTION_PROTOCOL.md`.

## Audit data

`ExecutionResult` records requested/fill price, requested/filled volume, slippage points, latency, broker retcode and order/deal IDs.

`ExecutionJournalJsonlStore` in Persistence reconstructs the lifecycle after process restart.

## Live money

This layer does not enable live money. The XSP-017 MQL5 adapter fails initialization on non-demo accounts, and any future live transport would require a separate explicitly approved product change.
