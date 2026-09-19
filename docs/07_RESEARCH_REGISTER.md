# Research Register: External EA/Indicator Audits

Purpose: preserve useful ideas without blindly copying systems.

Status names:
- `CANDIDATE`
- `KEEP-FOR-REPLAY`
- `NEGATIVE-EXAMPLE`
- `INCONCLUSIVE`
- `REJECT-AS-STRATEGY`

## 1. CCBSN / Can Cu Bu Sieng Nang v3.0.6

Observed design:
- many entry indicators/filters;
- DCA;
- martingale-style lot escalation;
- hedge/recovery;
- sniper/recovery behavior.

Keep:
- spread/slippage filtering concepts;
- ATR/ADX/EMA distance/context ideas;
- signal ideas only as raw candidate features.

Reject:
- DCA recovery;
- martingale;
- hedge rescue;
- lottery sizing.

Status: `REJECT-AS-STRATEGY`, feature source only.

## 2. Liquidity Full Suite v1.0

Observed:
- real DOM mode where broker supplies volume;
- estimated-liquidity mode otherwise;
- swing stops;
- HVN;
- round numbers;
- previous day/week levels;
- absorption;
- magnets;
- vacuum;
- breakout validation.

Keep:
- magnet strength and distance;
- pull differential;
- vacuum width;
- absorption;
- sweep/reaction;
- distinction between real DOM and estimated liquidity.

Important:
Estimated liquidity is statistical inference, not real resting order book.

Status: `KEEP-FOR-REPLAY`.

## 3. FvgGold-EA
Source: https://github.com/foeed/FvgGold-EA

Observed audit notes:
- FVG scoring and context are useful research ideas;
- "Require OB" behavior in source acts more like score bonus than an absolute requirement;
- daily P/L implementation observed to risk repeated historical accumulation;
- GTC pending-order lifecycle needs careful invalidation;
- optimization target based heavily on win rate is not accepted as evidence.

Keep:
- FVG size/age/fill;
- displacement;
- OB overlap as a numeric feature;
- session context.

Status: `KEEP-FOR-REPLAY`, do not copy runtime unchanged.

## 4. RB_XAUUSD_MT5
Source: https://github.com/RicardoBarato/xauusd-mt5-expert-advisor

Observed:
- M1 impulse;
- M5 volatility/squeeze;
- M15 structure/ADX;
- H1 EMA context;
- volatility ratio.

Audit concerns:
- position-management ownership isolation must be stronger;
- trade-transaction daily-loss accounting requires symbol/magic isolation;
- direct-breakout stop-distance handling showed inconsistent price/point update behavior.

Keep:
- ATR current / ATR historical ratio;
- squeeze/release;
- multi-timeframe regime context.

Status: `KEEP-FOR-REPLAY`.

## 5. AurumNeuro Vanguard
Public MQL5 CodeBase source/release.

Observed:
- velocity;
- fields named curvature/entropy/causal dynamics/Hamiltonian;
- online/adaptive concepts.

Audit interpretation:
- do not treat scientific-sounding names as evidence;
- some published feature formulas are much simpler than terminology suggests;
- velocity is useful candidate;
- real acceleration should be independently defined.

Keep:
- velocity concept;
- online model monitoring/adaptation ideas.

Status: `CANDIDATE`.

## 6. ZetaBurst Scalper

Observed:
- short rolling tick window;
- statistical burst/z-score concept;
- momentum/reversion modes.

Important:
Public description/research did not establish XAU edge. Treat as microstructure inspiration only.

Keep:
- tick return windows;
- burst z-score;
- tick velocity/rate;
- execution-cost sensitivity.

Status: `KEEP-FOR-REPLAY`.

## 7. Gold ICT OrderBlock / orderBlock
Source: https://github.com/cfournel/orderBlock

Observed:
- BOS/MSS/CHoCH/FVG/order-block concepts;
- sweep logic;
- extensive state and execution machinery.

Audit note:
A simple wick breach may be classified as sweep in some source paths; our product should represent sweep depth, close-back behavior, and reaction numerically rather than inherit a binary label.

Keep:
- structure feature candidates.

Status: `KEEP-FOR-REPLAY`.

## 8. XAUUSD Price Action Confluence
Source: https://github.com/joe-mw/xauusd_ea

Observed:
- M1 EMA trend context;
- pin bar;
- engulfing;
- fake breakout;
- pullback;
- support/resistance;
- uses closed-bar/new-bar signal evaluation.

Strong research value:
The fake-break definition includes level penetration and close-back-inside behavior.

Do not keep:
Equal-weight "3 of 5 signals" as truth because several signals may measure the same rejection phenomenon.

Status: `KEEP-FOR-REPLAY`.

## 9. BAKOME Ultimate ICT Gold Scalper
Source: https://github.com/BAKOME-Hub/BAKOMEGoldScalper

Audit findings:
- code computes liquidity/FVG/OB structures;
- observed entry path primarily uses kill-zone plus simple market bias instead of requiring those structures;
- position sizing formula mixes units and should not be reused;
- break-even/trailing logic compares monetary profit with ATR price-distance concepts in observed source;
- trailing behavior may only modify once;
- daily counters/reset behavior requires correction.

Keep:
- kill-zone/session as context;
- swing/liquidity ideas only after independent implementation.

Status: `REJECT-AS-STRATEGY`, `NEGATIVE-EXAMPLE` for unit-safety.

## 10. Gold Dual Engine Scalper
Source: https://github.com/MithunCyDev/MT5-Expert-advisor

Observed concept:
- compression;
- burst continuation;
- liquidity sweep reversal;
- VWAP/VWMA;
- exhaustion;
- divergence;
- session/risk modules.

Audit concerns:
- news filter is a stub in observed source;
- array/index semantics require careful verification;
- sweep volume baseline implementation does not appear to use the full configured lookback in the observed path;
- VWAP slope implementation requires runtime verification and may not represent historical-to-current slope as intended.

Keep:
- burst;
- deceleration;
- exhaustion as raw components.

Status: `KEEP-FOR-REPLAY`, source implementation not trusted wholesale.

## 11. Bands Sweeps
Source: https://github.com/razorcell/bands_sweeps

Observed:
- OnTick live evaluation;
- forming M1 usage;
- 10-tick speed calculation;
- entry modes including immediate/deceleration/directional stop;
- liquidity sweep/deep sweep;
- squeeze and higher-timeframe context;
- BOS/momentum exit concepts.

High-value research idea:
Intrabar timing based on price speed deceleration or direction flip, rather than always waiting for M1 close.

Keep:
- live M1;
- tick velocity;
- peak velocity;
- deceleration ratio;
- directional flip;
- deep-sweep normalization;
- micro timing.

Status: `KEEP-FOR-REPLAY`, high priority.

## 12. Goldvein
Public repository/research pipeline.

Value:
- causal feature research;
- cost-inclusive event-driven testing;
- temporal validation/walk-forward mindset.

Keep:
- methodology for Replay Lab.

Status: `KEEP-FOR-RESEARCH-METHOD`.

## 13. Research rule for future bots

For every new source:

1. archive URL/file/hash;
2. read source when available;
3. identify actual execution path;
4. compare implementation to README/marketing;
5. detect unused settings/dead modules;
6. verify units;
7. verify indexing/time causality;
8. verify position ownership;
9. verify daily/reset logic;
10. extract numeric features only;
11. record status here;
12. add to replay only if it is genuinely distinct.

Do not expand production architecture for every new EA.
