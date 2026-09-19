# Risk and Execution Specification

## 1. Principle

AI recommends. Deterministic code authorizes and executes.

No model can bypass this document.

## 2. Explicitly forbidden core behavior

- martingale;
- recovery DCA;
- loss averaging;
- grid rescue;
- lottery sizing;
- hedge rescue;
- increasing risk because a previous trade lost.

## 3. Risk settings

Initial settings contract should support:

```text
MaxRiskPerTradePct
MaxDailyLossPct
MaxDailyLossMoney
MaxTradesPerDay
MaxConcurrentPositions
MaxSpreadPrice
MaxSpreadAtrRatio
MaxSlippagePoints
MaxDecisionAgeMs
MaxFeatureAgeMs
MinFreeMarginPct
CooldownAfterLossSec
CooldownAfterExecutionFailureSec
HighImpactNewsBlockBeforeSec
HighImpactNewsBlockAfterSec
AllowLong
AllowShort
```

Defaults are product configuration, not model output.

## 4. Position sizing

Lot sizing MUST be computed from actual monetary risk using broker symbol metadata.

Conceptually:

```text
riskMoney = equity * riskPct

ticksToStop = stopPriceDistance / tickSize

lossPerLot = ticksToStop * tickValue

lots = riskMoney / lossPerLot
```

Then:
- floor to volume step;
- enforce min/max broker volume;
- check free margin;
- fail closed on invalid tick size/value.

Never use formulas that mix ATR price values with account currency without tick-value conversion.

## 5. Stop and target research vs execution

Research may evaluate multiple stop/adverse barriers.

Production trade plans need one explicit protective stop before order authorization.

A model may suggest probabilities; it does not get permission to omit hard protection.

## 6. Order lifecycle

All commands require a unique `TradeIntentId`.

Minimum states:

```text
Created
RiskRejected
Authorized
Submitting
Accepted
PartiallyFilled
Filled
Open
ModifyPending
ClosePending
Closed
Failed
UnknownNeedsReconciliation
```

Retries must use the same correlation/idempotency identity.

## 7. Reconciliation

On startup/reconnect:
- query broker positions/orders/deals;
- match by magic/comment/correlation metadata where possible;
- never assume local state is authoritative;
- classify unmatched broker positions visibly;
- block unsafe duplicate entry until reconciliation completes.

## 8. Position ownership

Every management action must verify:
- symbol;
- strategy/model runtime instance;
- magic/ownership identifier;
- expected position identity.

The system must not move or close another EA's XAU position merely because the symbol matches.

## 9. Spread/slippage handling

Before submit:
- check current spread;
- check age of quote/state/decision;
- check broker trading status.

After fill:
- record requested price;
- fill price;
- slippage;
- actual spread;
- latency;
- broker retcode.

Research reports should later use the empirical distribution, not only configured assumptions.

## 10. Decision freshness

A decision is invalid when:
- feature schema mismatch;
- state too old;
- model response exceeded max age;
- market sequence materially advanced beyond policy;
- spread/volatility changed beyond safety threshold while waiting.

Reject stale cloud responses rather than "using them anyway".

## 11. One-position default

V1 default:
- maximum one XauScalp position at a time.

The infrastructure may support a configurable cap, but multi-position behavior is not required for proving the initial edge.

## 12. Exit behavior

V1 must support deterministic protective exit plus researchable model-assisted exit.

Possible exit events:
- hard SL;
- hard/fixed research TP;
- model `ExitNow` when enabled;
- momentum decay;
- direction flip against trade;
- opposite liquidity event;
- time stop;
- risk emergency close.

Exit policy and version must be logged.

## 13. Daily lock

Daily loss calculations must:
- filter only this product's owned positions/deals unless account-wide risk mode is explicitly chosen;
- include commission and realized P/L;
- reset on a configured trading-day boundary;
- survive process restart through persisted state/reconstruction.

Do not implement a "daily" counter that only resets when the application restarts.

## 14. News

If news blocking is enabled but the calendar/feed is unavailable:
- mark news data unavailable;
- follow configured fail policy;
- never silently behave as though there is no news.

## 15. Failure policy

Default:
- data stale -> no new trades;
- model unavailable -> no new trades unless explicit JEV-to-Native fallback;
- risk engine error -> no new trades;
- broker state unknown -> reconcile, then decide;
- order result unknown -> do not resend blindly.

## 16. Audit record

Every attempted decision must link:

```text
MarketStateId
DecisionId
ModelId/version
RiskDecision
TradeIntentId
Order/deal ids
Settings version/hash
Outcome
```

This linkage is required for later replay comparison.
