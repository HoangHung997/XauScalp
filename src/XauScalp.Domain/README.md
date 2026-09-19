# XauScalp.Domain contracts

XSP-002 establishes the versioned contracts shared by live ingestion, replay, both decision models, hard risk, and execution.

## Rules

- Domain has no dependency on MT5, UI, a JEV provider SDK, or Python.
- Exactly two decision-model enum values exist: `Jev` and `XauNative`.
- Market and decision timestamps whose names end in `Utc` require UTC offset `+00:00`.
- Contract versions are explicit and fail closed when an unsupported contract version is deserialized.
- `FeatureSchemaVersion` and `ModelVersion` are separate compatibility dimensions.
- Missing numeric features are represented as unavailable with a reason; they are not silently converted to zero.
- `TradePlan` can only represent Long/Short. A model `Wait` result never becomes a trade plan.
- An authorized `RiskDecision` requires approved volume, monetary risk, and a protective stop.
- Live-money authorization is not part of these contracts.

## JSON

Use `XauJson.CreateOptions()` for the canonical JSON shape. Enums are serialized as camel-case strings and integer enum values are rejected. Polymorphic market/bar events carry explicit discriminators.
