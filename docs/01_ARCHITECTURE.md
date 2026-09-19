# System Architecture

## 1. Architectural rule

There is one market-state pipeline, two interchangeable decision models, one risk engine, and one execution path.

```text
XAUUSD / MT5
    |
    v
Market Data Gateway
    |
    +--> Raw Tick Recorder
    |
    v
Bar / Time Normalization
    |
    v
Feature Engine
    |
    v
XauMarketState
    |
    v
Decision Model Router
   / \
  /   \
JEV   XAU Native AI
  \   /
   \ /
    v
XauDecision
    |
    v
Hard Risk Engine
    |
    v
Trade Planner
    |
    v
Execution Engine
    |
    v
MT5 / Broker

In parallel:
Raw data + Feature states --> Replay / Labels --> Evaluation --> Native training
```

## 2. Preferred implementation stack

Initial target:
- Windows desktop/service;
- C# / .NET 8+ for production application;
- MT5/MQL5 broker bridge;
- ONNX Runtime for local native inference when model format permits;
- Python is allowed for offline research/training, but production contracts stay language-neutral and versioned.

Do not couple domain logic to UI, MT5, JEV SDK, Python, or a specific database.

## 3. Recommended solution layout

```text
/src
  XauScalp.Domain
  XauScalp.MarketData
  XauScalp.Features
  XauScalp.DecisionModels
  XauScalp.Risk
  XauScalp.Execution
  XauScalp.Persistence
  XauScalp.Replay
  XauScalp.App

/mt5
  XauScalpBridge.mq5
  protocol/

/research
  training/
  notebooks-or-scripts/
  feature-evaluation/

/tests
  XauScalp.Domain.Tests
  XauScalp.Features.Tests
  XauScalp.Replay.Tests
  XauScalp.Risk.Tests
  XauScalp.Execution.Tests
  XauScalp.IntegrationTests

/docs
```

Exact project names may vary only if contracts and dependency directions remain the same.

## 4. Dependency direction

Allowed:
- App -> application/domain interfaces;
- adapters -> domain/application interfaces;
- Replay -> domain/features;
- Decision adapters -> decision-model contract;
- Execution adapter -> execution contract.

Forbidden:
- Domain -> MT5;
- Domain -> UI;
- Feature engine -> JEV;
- JEV adapter -> broker API;
- XAU Native model -> broker API;
- Risk engine -> UI state.

## 5. Core interfaces

### 5.1 Market data

```csharp
public interface IMarketDataSource
{
    IAsyncEnumerable<MarketEvent> StreamAsync(CancellationToken ct);
}
```

Market events should include:
- tick;
- bar-open/update/close;
- connection status;
- symbol specification changes if applicable.

### 5.2 Feature engine

```csharp
public interface IXauFeatureEngine
{
    XauMarketState Update(MarketEvent marketEvent);
}
```

Requirements:
- deterministic;
- causal;
- no future data;
- versioned schema;
- same implementation reused by live and replay wherever practical.

### 5.3 Decision model

```csharp
public interface IXauDecisionModel
{
    string ModelId { get; }
    string ModelVersion { get; }

    Task<XauDecision> EvaluateAsync(
        XauMarketState state,
        CancellationToken ct);
}
```

Implementations:
- `JevDecisionModel`;
- `XauNativeDecisionModel`.

### 5.4 Risk

```csharp
public interface IRiskEngine
{
    RiskDecision Evaluate(
        XauMarketState state,
        XauDecision modelDecision,
        PortfolioState portfolio,
        RiskSettings settings);
}
```

### 5.5 Execution

```csharp
public interface IExecutionGateway
{
    Task<ExecutionResult> SubmitAsync(TradePlan plan, CancellationToken ct);
    Task<ExecutionResult> ModifyAsync(PositionCommand command, CancellationToken ct);
    Task<ExecutionResult> CloseAsync(PositionCommand command, CancellationToken ct);
}
```

## 6. Model router

Settings contain:
- `PrimaryDecisionModel = Jev | XauNative`;
- `ShadowComparisonEnabled`;
- optional shadow model (must be the other of the two);
- `FailurePolicy = StopNewTrades | FallbackToXauNative` when primary is JEV.

There is no third model.

Fallback decisions MUST be logged with:
- primary failure reason;
- elapsed time;
- fallback model/version;
- whether fallback was allowed by current operating mode.

## 7. Event-driven evaluation

Do not blindly call a cloud model on every tick.

Feature engine updates every event, but model evaluation may be triggered by meaningful state changes such as:
- liquidity sweep detected;
- velocity peak/deceleration threshold crossed;
- direction flip;
- micro-retest;
- magnet touch;
- FVG/zone entry;
- spread normalization;
- new M1 bar;
- periodic maximum staleness timer.

XAU Native may run at a higher local frequency than JEV while preserving the same contract.

## 8. Data provenance

Every `XauMarketState` MUST carry:
- UTC timestamp;
- broker/server timestamp when available;
- symbol and broker symbol name;
- feature schema version;
- feed/source identifier;
- sequence number;
- whether each higher-timeframe field is closed-bar or live;
- liquidity source: `RealDom | Estimated | None`.

## 9. Persistence

Persist at minimum:
- raw ticks;
- normalized bars;
- feature snapshots used for decisions;
- model decisions;
- risk decisions;
- trade plans;
- order/deal events;
- position lifecycle;
- settings snapshot;
- model versions;
- feature schema version;
- replay run metadata.

Never rely only on human-readable logs.

## 10. Execution state machine

Minimum states:

```text
Idle
 -> Evaluating
 -> Armed
 -> Submitting
 -> Open
 -> Modifying / Closing
 -> Closed
 -> Cooldown
```

Order commands need stable correlation/idempotency IDs so retries cannot create duplicate positions.

## 11. Runtime safety boundaries

The model cannot:
- choose arbitrary lot size;
- disable daily loss lock;
- increase risk limits;
- ignore spread/latency guards;
- change broker credentials;
- send orders directly.

If feature/model services fail or become stale, default behavior is **no new trade**, not "best effort trading".

## 12. Initial UI scope

The UI should be operational, not theatrical.

Required screens:
- Dashboard;
- Settings;
- Model status;
- Live market/feature diagnostics;
- Positions/orders;
- Replay/research runs;
- Logs/incidents.

The Settings screen must visibly expose the two primary decision-model choices.
