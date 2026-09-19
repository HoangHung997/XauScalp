# XauScalp — Product Specification

Status: **Baseline specification v1.0**  
Product scope: **XAUUSD scalp research and autonomous execution**  
Primary research objective: short, repeatable XAUUSD moves around **5 and 10 price units** rather than long-duration trend holding.

## 1. Product objective

Build a small, evidence-driven XAUUSD scalping application that:

1. records trustworthy live/tick data;
2. converts market behavior into a deterministic, versioned numeric market state;
3. supports exactly two interchangeable decision-model backends:
   - JEV;
   - XAU Native AI;
4. keeps risk and execution outside the AI;
5. can replay the same XAU tick stream deterministically;
6. measures whether each feature, entry method, and model actually adds edge after trading costs;
7. supports shadow comparison before real-money promotion.

The system is not an "AI army", council, academy, multi-agent society, or long-horizon discretionary trader.

## 2. Core product hypothesis

The market state immediately before a short XAU move can contain useful information in four broad classes:

- microstructure and intrabar dynamics;
- liquidity behavior;
- price structure;
- volatility/regime/context.

The application must not assume that any indicator or external EA rule is profitable. All imported ideas are candidate evidence only.

## 3. Two decision models

Settings MUST expose exactly two primary model choices:

### 3.1 JEV

External decision-model adapter.

JEV receives the same versioned `XauMarketState` as the native model and returns the same `XauDecision`.

JEV:
- MUST NOT place broker orders;
- MUST NOT control lot size;
- MUST NOT bypass risk rules;
- MUST be version-pinned in production;
- MUST have timeout/circuit-breaker behavior;
- MAY be used as production, shadow, or research-only model.

Provider/API-specific details belong in the adapter and configuration, not in domain logic.

### 3.2 XAU Native AI

Locally controlled model specialized for XAUUSD scalp decisions.

Purpose:
- approximate the useful behavior of a typed decision model: state in, multiple probabilities/decisions out;
- learn specifically from XAU data;
- eventually operate locally with low latency;
- remain independently testable against JEV.

Initial architecture should use a shared numeric encoder with multiple output heads. The first implementation may start with a simpler calibrated baseline as long as the public model contract is already final.

## 4. Primary vs shadow model

Only one model is the **production decision source** at a time.

Optional shadow mode:
- the second model receives the same market state;
- its output is logged;
- it cannot submit or modify orders;
- results are scored later against identical future market outcomes.

This is comparison infrastructure, not a third decision model.

## 5. Market-timeframe philosophy

### H1 / M15 / M5
Used primarily as context:
- regime;
- structure;
- volatility;
- liquidity areas;
- higher-timeframe directional information.

Prefer closed-bar data for stable higher-timeframe features unless a feature explicitly declares itself live.

### M1
Execution frame.

M1 must include both:
- closed M1 history;
- the currently forming M1 candle.

### Tick layer
Actual entry timing research is tick/intrabar aware.

The system MUST support researching:
- entry on M1 close;
- intrabar immediate entry;
- intrabar deceleration;
- intrabar directional flip;
- intrabar micro-retest.

These are research entry modes, not assumptions about what is best.

## 6. Why current M1 and ticks are first-class

A one-minute candle loses event order. Two bars can share identical OHLC values while the high and low occurred in opposite sequences.

For a 5–10 price-unit scalp, that path information may be material.

Therefore:
- M1 OHLC alone MUST NOT be used to validate intrabar strategies;
- replay of intrabar strategies MUST use ordered tick data;
- live M1 progress, tick velocity, acceleration/deceleration, and direction flips are candidate features.

## 7. Candidate feature families

V1 feature families:

1. execution/cost;
2. live M1;
3. tick microstructure;
4. liquidity;
5. liquidity reaction;
6. structure;
7. regime;
8. higher-timeframe context;
9. open-position/exit state.

The canonical field list is in `02_FEATURE_SCHEMA.md`.

## 8. Model outputs

Both models MUST emit a common typed result including at minimum:

- `Action = Long | Short | Wait`;
- probability of +5 first;
- probability of -5 first;
- probability of +10 first;
- probability of -10 first;
- probability of adverse barrier before target;
- continuation probability;
- reversal probability;
- false-break probability;
- confidence;
- model/version metadata.

When a position is open, the result may additionally include:
- hold probability;
- exit-now probability;
- probability of reaching the next +5/+10 from current price.

A model recommendation is evidence. Risk and execution remain authoritative.

## 9. Explicit non-goals

The core product MUST NOT include:
- martingale;
- loss-recovery DCA;
- grid rescue;
- lottery sizing;
- hedge-recovery cycles;
- averaging down as loss recovery;
- model-controlled account-risk limits;
- hard-coded "indicator says BUY" logic copied from an external EA;
- direct LLM free-text reasoning in the real-time execution loop.

## 10. Evidence policy

Every external EA/indicator/source is treated as one of:
- candidate feature source;
- engineering pattern;
- negative example;
- benchmark.

Claims in README/product pages are not accepted as fact until source or runtime behavior supports them.

Feature promotion requires replay evidence on common data.

## 11. Product success criteria

V1 is successful when it can:

- record and replay ordered XAU ticks deterministically;
- generate the same feature state during live and replay for the same event history;
- run both model adapters through the same interface;
- compare M1-close and intrabar entry modes fairly;
- calculate first-passage labels for +5 and +10 research targets;
- include spread/slippage/costs;
- prevent the AI from bypassing hard risk controls;
- generate an evidence report showing which features/models improve or degrade out-of-sample performance.

Profitability is not a Definition-of-Done criterion for the software platform itself. It is a research result to be measured.
