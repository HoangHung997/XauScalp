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

Full XSP-005 tick-history readiness requires 15 seconds because the burst baseline uses 15 completed seconds. Overall `RequiredP0Ready` additionally requires symbol metadata, ATR, cost estimates, news context, a configured session schedule, and an explicitly observed `Connected` market-data state. Unknown, reconnecting, or disconnected connection state fails closed.

## Determinism

`XauMarketState.MarketStateId` is deterministically derived from schema/source/symbol/sequence/time rather than a random GUID. Given the same event prefix and configuration, live and replay produce the same feature values and state identity.

The engine never receives labels or future outcomes.


## XSP-006 causal liquidity and reaction evidence

XSP-006 extends the same feature engine with estimated-liquidity evidence. `XauMarketState.LiquiditySource` is therefore `Estimated`; no CFD price-derived field is labeled `RealDom`.

### Causal swing availability

A closed M1 bar becomes a swing candidate only after two later closed M1 bars exist. A swing high must exceed the highs of the two closed bars on each side; a swing low must be below the lows of the two bars on each side. The feature timestamp is never backdated to pretend the swing was known before confirmation.

Equal-high/equal-low strength clusters confirmed swings within `0.10 * AtrM1`:

- one level: strength 0;
- two clustered levels: 0.5;
- three or more: capped at 1.

### Sweep and close-back

Sweep detection uses ordered midpoint ticks against already-known estimated liquidity levels:

- upper/buy-side sweep: previous mid <= level and current mid > level;
- lower/sell-side sweep: previous mid >= level and current mid < level;
- sweep depth is maximum outward penetration / causal M1 ATR;
- close-back is distance returned inside the swept level / ATR.

A breakout that has not returned inside retains `CloseBackInsideAtr = 0`; it is not relabeled as rejection.

### Previous day/week levels

PDH/PDL and PWH/PWL are built from the observed midpoint stream and become available only after the UTC day/week actually rolls over. Current-day/current-week final extrema are never exposed as previous-period values early.

### Estimated magnets, pull and vacuum

Upper/lower candidate liquidity combines confirmed swings with prior-day/prior-week extrema. Magnet distance is normalized by ATR. Magnet score is inverse distance, adjusted only by numeric equal-level strength. Pull is a distance-weighted magnet score; `PullDelta = PullUp - PullDown`.

`InsideVacuum = 1` is a research candidate when both nearest upper and lower estimated liquidity distances are at least 0.75 ATR. It is not an entry rule.

### Reaction sequence

For the most recent causal touch/sweep (retained for 60 seconds), the engine records:

- touch age;
- sweep side/depth;
- wick/body ratio at touch;
- close-back depth;
- tick-volume ratio;
- velocity into the level and peak velocity at the level;
- deceleration after touch;
- post-touch direction-flip flag/delay;
- ordered micro-retest state and resume velocity.

Micro-retest is only marked after the ordered path performs: sweep/close-back -> move at least 0.10 ATR away -> retest to within 0.05 ATR of the level -> resume at least 0.10 ATR away.

Absorption scores are explicitly **estimated candidate scores**, combining deceleration, close-back and tick-volume evidence. They are not DOM and do not imply BUY/SELL.
