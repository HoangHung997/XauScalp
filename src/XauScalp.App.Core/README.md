# XauScalp App Core

This project contains the testable presentation model for the Windows desktop shell.

Safety boundaries:

- exactly two model choices are exposed: JEV and XAU Native AI;
- the JEV field stores a **secret reference** only, never an API key value;
- desktop controls build the existing Domain settings contracts; Hard Risk Engine remains authoritative;
- the UI has no broker-order API and cannot bypass risk;
- Live-money authorization is read-only `false` in XSP-015 and requires a separate Product Owner decision outside software completion.

The operational status model exposes feed health, feature readiness/staleness, selected model/version/latency/error, risk lock, broker connection and current position.
