# XauScalp Feature Engine

XSP-005 implements the first P0 causal feature slice. It is a measurement engine, not a BUY/SELL rule engine.

## Version

- Feature schema: `xau-features-v1`
- Engine semantics: `xau-feature-engine-xsp005-v1`

Any future semantic change that makes recorded values incompatible requires a schema/version review before a model is trained or promoted.

## Price choices

- Forming M1 OHLC uses **Bid**, exactly matching the causal bar semantics from XSP-004.
- Tick microstructure uses **mid = (Bid + Ask) / 2** so spread-only changes are less likely to masquerade as directional price movement.
- Spread is measured separately as `Ask - Bid`.

No feature uses Last as a fallback price.

## Rolling-return and velocity semantics

For a window `W`, the engine treats the last observed midpoint at or before `T-W` as the causal price state at the left boundary.

`ReturnW = Mid(T) - MidStep(T-W)`

`VelocityW = ReturnW / W_seconds`

This is deterministic under irregular tick spacing and does not interpolate future ticks.

## Acceleration

For an acceleration window `W`:

1. split W into two equal causal halves;
2. compute price velocity over the first half;
3. compute price velocity over the second half;
4. `Acceleration = (recentVelocity - priorVelocity) / halfWindowSeconds`.

No future sample is used.

## Peaks and deceleration

- `PeakVelocityUp`: maximum positive 500ms velocity in the trailing 5s.
- `PeakVelocityDown`: maximum magnitude of negative 500ms velocity in the trailing 5s.
- dominant peak = max(up peak, down peak).
- current velocity is projected onto that dominant direction.
- `DecelerationRatio = clamp((peak - alignedCurrent) / peak, 0, 1)`.
- A complete stop or reversal after the dominant peak therefore approaches 1.

These are numeric evidence only; they do not imply an entry.

## Direction flip

`DirectionFlipAgeMs` measures elapsed time since the sign of the causal 500ms velocity last changed between non-zero directions. It remains unavailable until a real flip is observed.

## Tick activity

Initial windows:

- counts/rates: 250ms, 1s, 5s;
- interval mean/std: trailing 5s;
- up/down tick ratio: trailing 5s, using midpoint changes;
- micro ranges: 1s and 5s.

Flat midpoint transitions remain in the ratio denominator, so `UpTickRatio + DownTickRatio` may be less than 1.

## Burst z-score

The current partial UTC-second tick count is compared with the tick counts of the previous 15 **completed** UTC-second buckets.

`BurstZScore = (currentCount - historicalMean) / historicalStd`

The field is unavailable during warm-up or when historical standard deviation is zero; the engine does not invent an epsilon to manufacture a score.

## Forming M1

All M1 fields come from `FormingBarState` owned by the same causal bar implementation used by live/replay.

- `M1BarProgressPct` is 0..100.
- `M1LiveBodyPrice = liveBid - open` (signed).
- `M1LiveBodyRatio = abs(body) / liveRange`.
- upper/lower wick ratios are relative to current live range.
- zero-range candle ratios are defined as 0.
- `M1DistanceFromOpen = liveBid - open` (signed).
- `M1DistanceFromHigh = high - liveBid`.
- `M1DistanceFromLow = liveBid - low`.

No final closed M1 high/low/close is imported into the forming snapshot.

## Execution/external context

Some P0 fields require sources implemented by later tasks:

- causal M1 ATR for spread/range ATR ratios;
- latency/slippage estimator;
- economic-calendar/news context;
- session schedule.

They are present in the schema but are explicitly **unavailable** until causal external observations are supplied through `FeatureExternalContext`. Missing data is never silently coerced to zero.

Symbol specification fields are populated from `SymbolSpecificationEvent`.

## Warm-up and readiness

A rolling field is unavailable until its complete causal window has elapsed from the first observed tick.

Full XSP-005 tick-history readiness requires 15 seconds because the burst baseline uses 15 completed seconds. Overall `RequiredP0Ready` additionally requires symbol metadata, ATR, cost estimates, news context, and a configured session schedule.

## Determinism

`XauMarketState.MarketStateId` is deterministically derived from schema/source/symbol/sequence/time rather than a random GUID. Given the same event prefix and configuration, live and replay produce the same feature values and state identity.

The engine never receives labels or future outcomes.
