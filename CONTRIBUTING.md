# Contributing to XauScalp

## Prerequisites

- .NET SDK 8.0 or later compatible with `global.json`.
- Git.
- No broker credentials are required for the bootstrap solution.

## One-command build and test

From the repository root:

```bash
dotnet build XauScalp.sln --configuration Release
```

Run all automated tests:

```bash
dotnet test XauScalp.sln --configuration Release
```

Verify repository formatting:

```bash
dotnet format XauScalp.sln --verify-no-changes
```

The CI workflow performs restore, formatting verification, build, and tests on every pull request and on pushes to `main`.

## Branch and pull-request convention

Use:

```text
task/xsp-###-short-name
```

PR titles use:

```text
XSP-###: short description
```

Each PR must reference its GitHub Issue and satisfy `docs/08_DEFINITION_OF_DONE.md`.

## Architecture guardrails

- Domain must not depend on MT5, UI, JEV SDKs, or Python.
- JEV and XAU Native AI remain the only two product decision models.
- Risk and execution are deterministic application boundaries outside AI.
- No martingale, recovery DCA, grid rescue, hedge recovery, or lottery sizing.
- Live-money trading remains disabled unless separately authorized by the Product Owner.

XSP-001 intentionally contains no BUY/SELL strategy logic.
