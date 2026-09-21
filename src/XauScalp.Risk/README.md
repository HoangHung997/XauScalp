# XauScalp hard risk engine

The Risk project is deterministic and has no dependency on UI, JEV provider SDKs, Python or MT5 order submission.

`HardRiskEngine` consumes only the shared `XauMarketState`, `XauDecision`, `PortfolioState`, `RiskSettings`, plus injected broker/risk context and the product-owned daily ledger.

## Position sizing

For the versioned deterministic protective-stop distance:

```text
riskMoney = equity * MaxRiskPerTradePct

ticksToStop = stopPriceDistance / tickSize
lossPerLot = ticksToStop * tickValue

rawLots = riskMoney / lossPerLot
lots = floor_to_volume_step(min(rawLots, brokerMaxLot))
actualRiskMoney = lots * lossPerLot
```

The engine rejects invalid tick size/value, broker volume metadata, stop distance, below-minimum lot and insufficient post-entry free margin.

The model never selects lot size or account risk.

## Hard guards

Before authorization the engine checks:

- immutable MarketStateId/schema linkage;
- `Wait` cannot become a trade;
- P0 readiness;
- feature/state/decision/portfolio freshness;
- AllowLong / AllowShort;
- actual spread price and `SpreadAtrRatio`;
- high-impact news, failing closed when news blocking is enabled but data is unavailable;
- current and estimated post-entry free margin;
- product-owned daily money/% loss;
- product-owned daily trade count;
- loss and execution-failure cooldowns;
- product-owned concurrent positions.

## Ownership isolation

Daily lock state comes from `IOwnedRiskLedger`, not from account-wide `PortfolioState.RealizedPnlToday`.

Concurrent positions count only matching XauScalp strategy/magic ownership. A foreign EA position may coexist in the broker portfolio without being counted or managed as XauScalp.

## Restart/day boundary

`RiskLedgerJsonlStore` in the Persistence project is append-only. It records:

- explicit trading-day start + starting equity;
- owned trade-close net P/L inputs;
- execution failures.

On process restart it reconstructs the current configured trading day from disk. If the current day has not been explicitly initialized, the risk engine fails closed with `risk-ledger-not-ready`.

Live-money authorization is still outside this project and remains disabled by default.
