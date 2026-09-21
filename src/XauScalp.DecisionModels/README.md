# Decision Models

This project contains the only two product decision-model backends:

- JEV
- XAU Native AI

Both implement `IXauDecisionModel`, consume immutable `XauMarketState`, and return `XauDecision`. No class in this project can place broker orders or choose account risk.

## XAU Native V0

The native V0 runtime and offline training pipeline are documented in `research/training/README.md`.

## JEV adapter

`JevDecisionModel` is provider-independent. A provider integration implements `IJevProviderClient`; domain and risk/execution layers never depend on a provider SDK.

The adapter pins provider version, validates typed probabilities/schema/state identity, rejects stale responses, uses bounded retry/timeout/circuit breaker behavior, and obtains secrets through OS-backed providers such as Windows Credential Manager.

## Primary / shadow authority

`PrimaryShadowDecisionOrchestrator` is the single comparison boundary.

- Primary and shadow receive the same immutable `XauMarketState` instance.
- The shadow result is persisted but is never exposed through the authoritative trade sink.
- Only a primary Long/Short decision can reach `IAuthoritativeTradeDecisionSink`.
- A primary Wait causes zero downstream trade-decision calls even if shadow says Long/Short.
- If primary JEV uses the configured XAU Native fallback, the fallback is explicitly marked.
- A shadow failure never silently becomes primary authority.
- External cancellation stops the whole evaluation.

`DecisionComparisonRecord` persists both roles. `DecisionFutureLabels` may be attached later by MarketStateId after replay labels exist. `ExecutedTradeOutcome` may only link to the authoritative primary decision.

`DecisionComparisonReportBuilder` intentionally exposes two separate sections:

1. **All-state** probability scoring for primary and shadow decisions with future labels.
2. **Executed-trade** outcomes only for decisions that actually became authoritative trades.

This separation prevents selection-biased executed trades from being presented as overall model quality.

Durable JSONL storage is provided by `XauScalp.Persistence.DecisionComparisonJsonlStore`.

Primary/shadow comparison does not create `TradePlan` and does not bypass hard risk. The authoritative sink is an input to the later risk/execution pipeline, not broker execution itself.
