# XSP-017 Demo Readiness Gate

Status: **fail closed until real MT5 demo evidence exists**.

This gate distinguishes two evidence classes:

1. `CI_REHEARSAL_NOT_BROKER_DEMO` — deterministic software rehearsal using production XauScalp components with fake external provider/broker adapters.
2. `BROKER_DEMO` — evidence from an actual MT5 demo account/feed. Only this class can satisfy the external portion of XSP-017.

A successful GitHub Actions run is necessary but **not sufficient** to claim demo readiness.

## Automated CI rehearsal

CI verifies:

- recorded ordered MarketEvent fixture -> same XauFeatureEngine -> deterministic replay;
- identical replay dataset/output hashes and feature states;
- JEV primary + XAU Native shadow authority separation;
- JEV outage with StopNewTrades and explicit Native fallback;
- Hard Risk Engine healthy authorization and stale-data/decision rejection;
- ExecutionEngine submit/fill -> durable journal -> process restart/reconnect/reconciliation -> no duplicate submit;
- live-money authorization remains disabled.

The CI evidence artifact is intentionally named/reported as rehearsal, never as broker-demo evidence.

## Required external broker-demo evidence

Run XauScalp against an MT5 **demo** account and produce a JSON manifest matching
`docs/evidence/xsp017-broker-demo-manifest.example.json`.

Required evidence:

- exact 40-hex code commit + successful GitHub Actions run URL;
- canonical symbol `XAUUSD`;
- real/demo XAU ordered tick dataset ID + SHA-256, with `datasetId = sha256:<datasetSha256>`;
- replay run ID + replay output SHA-256 for the same dataset;
- feature parity pass;
- replay determinism pass;
- JEV/XAU Native primary-shadow pass;
- risk-engine pass;
- one demo execution identifier;
- restart/reconnect pass with broker state present;
- model outage pass;
- stale-data pass;
- positive tick count;
- measured JEV and XAU Native P95 latency;
- explicit slippage/commission/latency cost assumptions;
- known limitations;
- an empty `unresolvedP0P1CorrectnessIssues` list;
- `capturedAtUtc` explicitly in UTC;
- `liveMoneyEnabled=false`.

Do **not** include account number, password, API key, token, secret or other credentials in evidence files.

## Validation

Generate CI rehearsal evidence:

```bash
python research/demo-readiness/generate_ci_rehearsal_evidence.py \
  --output artifacts/xsp017-ci-rehearsal.json
```

Validate external demo evidence and require it:

```bash
python research/demo-readiness/generate_ci_rehearsal_evidence.py \
  --external-demo-manifest path/to/xsp017-broker-demo.json \
  --output artifacts/xsp017-evidence.json \
  --require-external
```

Without a validated external manifest, the tool exits non-zero under
`--require-external` and reports:

`blocked-external-broker-demo-evidence`.

## Live money

Passing XSP-017 does **not** authorize live money. Live authorization remains a separate explicit Product Owner decision after broker-specific demo/shadow evidence and risk review.
