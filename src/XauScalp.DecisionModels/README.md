# Decision Models

This project contains the only two product decision-model backends:

- JEV
- XAU Native AI

Both implement `IXauDecisionModel`, consume immutable `XauMarketState`, and return `XauDecision`. No class in this project can place broker orders or choose account risk.

## XAU Native V0

The native V0 runtime and offline training pipeline are documented in `research/training/README.md`.

## JEV adapter

`JevDecisionModel` is provider-independent. A provider integration implements `IJevProviderClient`; domain and risk/execution layers never depend on a provider SDK.

The adapter:

- maps the complete versioned market-state evidence to typed `JevProviderRequest`;
- uses one deterministic request ID across bounded retries;
- pins provider model ID/version;
- validates response schema, state ID, feature schema, model version, timestamps, action and every probability;
- rejects stale state/response data;
- applies per-attempt timeout/cancellation;
- retries only transient provider failures;
- opens a circuit breaker after the configured number of failed evaluations;
- emits telemetry that contains no API secret;
- never sizes or submits a trade.

### Secrets

Production Windows hosts should store the provider credential as a **Generic Credential** in Windows Credential Manager and configure only its target name/reference.

`WindowsCredentialManagerJevSecretProvider` reads that credential at runtime. The secret value is wrapped in `JevSecret`, whose `ToString()` is always redacted. Secrets, passwords and API tokens must never be committed to repository settings or telemetry.

CI uses fake secret/provider implementations only.

## JEV failure policy

`JevDecisionCoordinator` applies the existing product setting:

- `StopNewTrades`: a JEV adapter failure returns no decision and explicitly stops new trades.
- `FallbackToXauNative`: only the XAU Native model may be used as fallback. Its decision must reference the same MarketStateId/schema and is explicitly marked as fallback in the coordinator result/telemetry.

External cancellation is not converted into fallback.

This coordinator does not implement shadow comparison; primary/shadow authority remains XSP-014.
