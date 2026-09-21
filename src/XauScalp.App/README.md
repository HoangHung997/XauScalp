# XauScalp Windows Desktop Shell

The desktop shell is operational rather than theatrical.

Tabs:

- **Settings** — exactly JEV and XAU Native AI, shadow comparison, JEV provider reference/version/timeout/failure policy, Native artifact/version/device/readiness, broker/data settings, and hard-risk settings.
- **Operational Status** — feed health, feature readiness/staleness, selected model/version/latency/error, risk lock, broker connection, and current position.

The WPF project contains no broker execution implementation and no hard-risk bypass. It builds validated Domain settings through `XauScalp.App.Core`.

Live-money authorization is deliberately not writable from this UI. XSP-015 operates in Research / Demo mode; live money requires a separate explicit Product Owner authorization after demo-readiness evidence.
