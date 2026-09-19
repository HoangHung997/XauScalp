# XauMarketState and Feature Schema v1

Status: candidate schema for implementation and replay.  
Important: inclusion in this schema does **not** mean a feature has proven edge.

## 1. Rules

Every feature must define:
- name;
- type;
- unit;
- source timeframe/window;
- live vs closed-bar semantics;
- causal computation;
- missing-value behavior.

Do not encode author conclusions such as `ICT_BUY=true` when the underlying numeric evidence can be represented directly.

Normalize by ATR or price-unit distance where useful so research remains comparable across volatility regimes.

## 2. Identity and provenance

Mandatory:

```text
TimestampUtc
BrokerTimestamp
SequenceId
Symbol
BrokerSymbol
Bid
Ask
Mid
FeatureSchemaVersion
DataSourceId
LiquiditySource = RealDom | Estimated | None
```

## 3. Execution/cost features — P0

```text
SpreadPrice
SpreadPoints
SpreadAtrRatio
TickSize
TickValue
MinStopDistance
EstimatedLatencyMs
EstimatedSlippagePoints
SessionCode
MinuteOfDay
DayOfWeek
NewsDistanceBeforeSec
NewsDistanceAfterSec
IsHighImpactNewsWindow
```

If news data is unavailable, explicitly mark availability; never silently report "not news".

## 4. Live M1 features — P0

```text
M1BarAgeMs
M1BarProgressPct
M1Open
M1LiveHigh
M1LiveLow
M1LivePrice
M1LiveRangePrice
M1LiveBodyPrice
M1LiveBodyRatio
M1UpperWickRatio
M1LowerWickRatio
M1DistanceFromOpen
M1DistanceFromHigh
M1DistanceFromLow
M1RangeAtrRatio
```

These describe the forming candle. They must never be confused with final closed-bar values.

## 5. Tick microstructure features — P0

Rolling windows should be configurable. Initial windows:

```text
250 ms
500 ms
1 s
2 s
3 s
5 s
10 s
15 s
```

Candidate outputs:

```text
Return250ms
Return500ms
Return1s
Return2s
Return3s
Return5s
Return10s

Velocity500ms
Velocity1s
Velocity3s
Velocity5s

Acceleration1s
Acceleration3s

PeakVelocityUp
PeakVelocityDown
DecelerationRatio
DirectionFlipAgeMs

TickCount250ms
TickCount1s
TickCount5s
MeanTickIntervalMs
TickIntervalStdMs
UpTickRatio
DownTickRatio
MicroRange1s
MicroRange5s
BurstZScore
```

Definitions must use ordered ticks and document bid/mid/last choice.

## 6. Liquidity features — P0/P1

P0:

```text
NearestSwingHighDistanceAtr
NearestSwingLowDistanceAtr

BuySideSweepDepthAtr
SellSideSweepDepthAtr
CloseBackInsideDistanceAtr

EqualHighStrength
EqualLowStrength

UpperMagnetScore
LowerMagnetScore
UpperMagnetDistanceAtr
LowerMagnetDistanceAtr

PullUp
PullDown
PullDelta

InsideVacuum
VacuumUpWidthAtr
VacuumDownWidthAtr

AbsorptionUpScore
AbsorptionDownScore

PdhDistanceAtr
PdlDistanceAtr
PwhDistanceAtr
PwlDistanceAtr
```

For estimated liquidity, the score must retain its source components where possible.

P1 real-DOM-only candidates:

```text
DomBidAskImbalance
NearestBidWallRelativeSize
NearestAskWallRelativeSize
WallPulled
WallConsumed
```

Do not fake DOM fields from CFD price behavior.

## 7. Liquidity reaction features — P0

These are intentionally separate from "a level exists".

```text
LiquidityTouchAgeMs
SweepOccurred
SweepDirection
SweepDepthAtr
WickBodyRatioAtSweep
CloseBackInsideAtr
VolumeRatioAtSweep
VelocityIntoLevel
PeakVelocityAtLevel
DecelerationAfterTouch
DirectionFlipAfterTouch
DirectionFlipDelayMs
MicroRetestOccurred
MicroRetestDepthAtr
ResumeVelocity
```

The important research question is not just where liquidity is, but how price enters and leaves it.

## 8. Structure features — P0/P1

```text
BosDirection
BosDistanceAtr
BosAgeSec

MssDirection
MssAgeSec

ChochDirection
ChochAgeSec

BullFvgDistanceAtr
BearFvgDistanceAtr
FvgSizeAtr
FvgAgeSec
FvgFillPct

BullOrderBlockDistanceAtr
BearOrderBlockDistanceAtr
OrderBlockOverlapPct

DisplacementRangeAtr
DisplacementBodyRatio
```

No field should mean "therefore buy".

## 9. Regime features — P0

```text
AtrM1
AtrM5
AtrRatioM1
AtrRatioM5
AdxM1
AdxM5

M1CompressionScore
M5CompressionScore
M1Squeeze
M5Squeeze
SecondsSinceM1SqueezeRelease
SecondsSinceM5SqueezeRelease

EmaSlopeM5
EmaSlopeM15
EmaSlopeH1
TrendAlignmentScore
```

ATR ratios should be current ATR divided by a causal historical baseline.

## 10. Higher-timeframe context — P0

```text
M5DirectionState
M15DirectionState
H1DirectionState
M5VolatilityState
M15StructureState
DistanceToM5MeanAtr
DistanceToM15MeanAtr
HtfAlignedUp
HtfAlignedDown
HtfConflict
```

Higher-timeframe context is evidence, not a mandatory directional gate unless replay later proves that a gate is superior.

## 11. Tier-2 candidates

Keep available for later ablation, but do not allow V1 scope to explode:

```text
Rsi
RsiDivergence
VwapDistance
VwapSlope
VwmaSlope
BollingerPosition
RoundNumberDistance
PremiumDiscountPosition
FibonacciPosition
AsianHighDistance
AsianLowDistance
LondonHighDistance
LondonLowDistance
NewYorkHighDistance
NewYorkLowDistance
```

## 12. Position/exit-state features — P0 when a position exists

```text
PositionSide
EntryPrice
CurrentPnlPrice
CurrentR
MfePrice
MaePrice
TimeInTradeMs
VelocityWithTrade
VelocityAgainstTrade
AccelerationWithTrade
DistanceToNextMagnetAtr
DistanceToOppositeLiquidityAtr
NewOppositeSweep
MomentumDecayScore
MicroDirectionFlipAgainst
StructureBreakAgainst
```

## 13. Feature versioning

A feature schema change requires:
- version increment;
- migration note;
- replay cache invalidation where semantics changed;
- model compatibility declaration.

A model artifact must declare the exact schema version it expects.

## 14. Missing and stale data

Never coerce missing data to a plausible numeric zero without an availability flag.

Examples:
- no real DOM;
- economic calendar unavailable;
- insufficient tick history after startup;
- not enough bars for ATR baseline.

State should expose data readiness. Decision execution must be blocked when required P0 data is stale beyond configured tolerance.

## 15. Feature-ablation requirement

Every candidate feature must eventually face:
- univariate usefulness check;
- correlation/redundancy analysis;
- ablation from a multivariate baseline;
- time-split out-of-sample evaluation;
- regime/session stability check.

The target is not maximum feature count. The expected end state may be roughly 15–30 useful features after evidence-based pruning.
