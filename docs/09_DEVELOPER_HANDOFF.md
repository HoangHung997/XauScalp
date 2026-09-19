# Developer Handoff / Execution Rules

## 1. Roles

- **Product owner:** human repository owner; final authority on capital/live deployment.
- **Product management / specification:** owns requirements, architecture intent, acceptance criteria, research discipline, and review.
- **Implementers:** write code, tests, tooling, and documentation for assigned GitHub Issues.

Implementers should not redesign the product into a different trading philosophy without a documented requirement change.

## 2. Read order before coding

1. `README.md`
2. `docs/00_PRODUCT_SPEC.md`
3. `docs/01_ARCHITECTURE.md`
4. `docs/02_FEATURE_SCHEMA.md`
5. task-specific document
6. assigned GitHub Issue

If code and specification conflict, stop and raise the conflict in the Issue/PR.

## 3. Branch and PR convention

Recommended:

```text
task/xsp-###-short-name
```

PR title:

```text
XSP-###: short description
```

One issue should normally map to one reviewable PR. Split oversized implementation internally only when the issue remains traceable.

## 4. What implementers may decide

Implementers may choose:
- internal class organization;
- libraries with compatible licenses;
- performance optimizations;
- test implementation details;
- UI controls/layout details that do not change product semantics.

## 5. What requires product review

Do not independently change:
- the two-model product decision;
- `XauMarketState` semantics;
- `XauDecision` semantics;
- target definitions;
- risk boundaries;
- no-martingale/no-DCA rule;
- feature causality;
- replay label semantics;
- production vs shadow authority;
- live-trading defaults.

Propose changes explicitly in PR/Issue.

## 6. Evidence standard

A chart screenshot is not enough.

For trading/research claims attach:
- dataset/run ID;
- date window;
- sample count;
- cost assumptions;
- code commit;
- settings/model version;
- out-of-sample status.

## 7. Anti-overfit rule

Do not optimize ten thresholds on one period and report the best result as validation.

If tuning occurs:
- tune on train;
- select on validation;
- freeze;
- report untouched OOS;
- then walk forward.

## 8. Safety rule

When uncertain:
- fail closed;
- do not trade;
- log reason.

Never "make it trade" by bypassing:
- stale checks;
- spread guard;
- daily loss;
- broker ownership;
- model schema validation.

## 9. Source-audit rule

If an external bot/EA is used for research:
- no blind copy;
- document license;
- document actual code behavior;
- extract formulas/features into our own clean implementation;
- add provenance to research register.

## 10. Required PR summary

Every PR should state:

```text
Issue:
What changed:
What did not change:
Tests:
Evidence:
Known limitations:
Risk impact:
Replay/live parity impact:
Docs updated:
```

## 11. Completion marker

Do not mark an issue done when:
- TODOs remain that affect acceptance criteria;
- tests are skipped to obtain green CI;
- runtime behavior is unverified;
- docs disagree with implementation.

## 12. Initial implementation instruction

Start from infrastructure, not strategy tuning.

The first developer objective is:

```text
MT5 ticks
 -> persisted ordered stream
 -> deterministic replay
 -> identical feature outputs
```

Do not spend the first iteration optimizing BUY/SELL logic.
