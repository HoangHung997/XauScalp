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

## Real MT5 demo execution path

The repository includes a separate demo-only execution path for this gate:

- MQL5: `mt5/XauScalpDemoExecutionBridge.mq5`;
- C#: `Mt5DemoExecutionBrokerGateway` + `Mt5DemoFileExecutionTransport`;
- protocol: `mt5/protocol/MT5_DEMO_EXECUTION_PROTOCOL.md`.

The MQL5 EA refuses non-demo account mode. This is intentionally separate from the market-data-only `XauScalpMarketBridge.mq5`.

GitHub CI cannot compile MQL5 or connect to a broker. Successful MetaEditor compilation and actual MT5 demo behavior therefore belong to the external evidence pack.

## Broker-demo runner workflow

Use a **new dedicated evidence directory** for each acceptance run. Do not reuse a directory that contains raw ticks without the corresponding live-feature snapshot journal, because live/replay parity is exact and intentionally fails on missing evidence.

1. Build the exact main commit whose 40-hex SHA is placed in the config and confirm its GitHub Actions main run is green.
2. Compile both current MQL5 sources in MetaEditor:
   - `mt5/XauScalpMarketBridge.mq5`
   - `mt5/XauScalpDemoExecutionBridge.mq5`
3. Attach both EAs to the broker's actual XAU demo symbol. The execution EA refuses non-demo account mode.
4. Copy `docs/evidence/xsp017-demo-runner-config.example.json` outside the repository, replace placeholders, and keep only secret **references** such as `env:XAUSCALP_JEV_API_KEY`. Never put the secret itself in the JSON.
5. Use a real XAU Native artifact produced from recorded/replayed XAU data. The committed synthetic parity fixture is not promotable broker-demo evidence.
6. Run the integrated demo runtime:

```bash
dotnet run --project src/XauScalp.DemoRunner -- run path/to/demo-config.json
```

The runner persists ordered market events, feature hashes, primary/shadow telemetry, hard-risk state, execution lifecycle, broker P/L synchronization, and non-secret observations. It resumes from the highest actual source sequence and fails closed after an unresolved feed gap or stale broker tick.

7. After enough data and at least one hard-risk-authorized demo execution, verify the exact dataset twice and compare every replayed feature state to its live hash:

```bash
dotnet run --project src/XauScalp.DemoRunner -- replay path/to/demo-config.json
```

8. Run the fault/recovery drills from a fresh process while an owned demo position/order is still present for the restart drill:

```bash
dotnet run --project src/XauScalp.DemoRunner -- drill path/to/demo-config.json
```

The command proves injected JEV outage -> StopNewTrades, stale ready-state -> `state-stale`, and restart/reconnect -> broker reconciliation with no extra submit command.

9. Build the final non-secret broker-demo manifest. The command requires the two compiled `.ex5` files and hashes them:

```bash
dotnet run --project src/XauScalp.DemoRunner -- evidence path/to/demo-config.json
```

It writes `xsp017-broker-demo.json` in the evidence directory and calculates measured P95 JEV/XAU Native latency from successful same-run decision telemetry.

10. Validate the generated manifest with the repository's independent Python gate:

```bash
python research/demo-readiness/generate_ci_rehearsal_evidence.py \
  --external-demo-manifest <evidence-dir>/xsp017-broker-demo.json \
  --output <evidence-dir>/xsp017-evidence.json \
  --require-external
```

Only the resulting validated non-secret evidence can close XSP-017. Passing it still does not authorize live money.

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
- successful MetaEditor compile of both current MQL5 bridges on the target MT5 installation, proven by SHA-256 of the generated `.ex5` artifacts;
- one demo execution identifier produced through Hard Risk -> ExecutionEngine -> MT5 demo adapter (not a hand-crafted broker command);
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
