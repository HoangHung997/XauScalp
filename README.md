# XauScalp

XauScalp is a research-first autonomous XAUUSD scalping application focused on short, repeatable moves rather than long trend holding.

## Product direction

The first product target is XAUUSD scalping with research labels around approximately 5 and 10 price-unit moves. These labels are research targets, not guaranteed profit targets.

The app MUST support exactly two interchangeable decision-model backends:

1. **JEV** — external decision model adapter.
2. **XAU Native AI** — our own XAU-specialized numeric decision model.

Both models consume the same `XauMarketState` and must return the same `XauDecision` contract. Neither model may directly place orders. Risk and execution remain deterministic application services.

## Non-negotiable engineering rules

- No martingale, loss-recovery DCA, grid rescue, or hedge-recovery logic in the core product.
- No copied EA signal is accepted as truth. Every external EA/indicator is only a research source.
- Features are numeric evidence, not hard-coded author opinions such as `FVG = BUY`.
- Intrabar M1 behavior and tick sequence are first-class research data.
- Historical research must avoid look-ahead leakage.
- Intrabar research requires real tick ordering; M1 OHLC alone is insufficient.
- Backtests/replays must include spread, slippage assumptions, latency metadata, and broker/symbol normalization.
- JEV and XAU Native AI share one feature schema and one output schema.
- Lot sizing, hard risk boundaries, order lifecycle, recovery, persistence, and broker constraints are outside the AI model.
- Production decisions must be reproducible from versioned data, feature schema, model version, settings, and logs.

## Canonical documentation

Start here:

- [Product specification](docs/00_PRODUCT_SPEC.md)
- [System architecture](docs/01_ARCHITECTURE.md)
- [Market-state and feature schema](docs/02_FEATURE_SCHEMA.md)
- [JEV and XAU Native model contract](docs/03_DECISION_MODELS.md)
- [Tick recorder and replay research protocol](docs/04_REPLAY_AND_RESEARCH.md)
- [Risk and execution specification](docs/05_RISK_AND_EXECUTION.md)
- [Implementation roadmap and work order](docs/06_IMPLEMENTATION_ROADMAP.md)
- [Research register and EA-audit findings](docs/07_RESEARCH_REGISTER.md)
- [Definition of Done](docs/08_DEFINITION_OF_DONE.md)
- [Developer handoff](docs/09_DEVELOPER_HANDOFF.md)

## Development principle

Do not begin by trying to build a profitable strategy. First build a trustworthy measurement and replay system. The project should be able to answer, with the same XAU tick stream, whether a candidate feature or entry method improves outcomes such as:

- probability of +5 before the configured adverse barrier;
- probability of +10 before the configured adverse barrier;
- MFE / MAE;
- expected value after costs;
- time to target;
- false-entry rate;
- drawdown;
- sensitivity to spread, latency, session, volatility regime, and broker feed.

Only after that evidence exists should the production model be promoted beyond shadow/demo use.

## Current status

Repository initialized with product-management specification and implementation tasks. Implementation has not yet been accepted as complete.
