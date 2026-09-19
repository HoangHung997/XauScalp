using System.Text.Json;

namespace XauScalp.Domain.Tests;

public sealed class ContractSerializationTests
{
    [Fact]
    public void MarketState_RoundTripsWithProvenanceAndFeatureVersion()
    {
        var state = new XauMarketState(
            ContractVersions.MarketStateV1,
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            ContractTestFactory.UtcNow,
            ContractTestFactory.UtcNow.AddHours(2),
            123,
            "XAUUSD",
            "XAUUSD.G",
            3680.10m,
            3680.30m,
            3680.20m,
            ContractVersions.FeatureSchemaV1,
            "mt5-demo",
            LiquiditySource.Estimated,
            new DataReadiness(
                requiredP0Ready: false,
                tickHistoryReady: true,
                barHistoryReady: false,
                newsDataAvailable: false,
                missingRequirements: ["M5 ATR history", "news feed"]),
            [
                new NumericFeatureValue(
                    "SpreadPrice",
                    0.20,
                    "price",
                    true,
                    ContractTestFactory.UtcNow,
                    null),
                new NumericFeatureValue(
                    "NewsDistanceBeforeSec",
                    null,
                    "seconds",
                    false,
                    null,
                    "news feed unavailable"),
            ]);

        XauMarketState restored = RoundTrip(state);

        Assert.Equal(state.MarketStateId, restored.MarketStateId);
        Assert.Equal(ContractVersions.FeatureSchemaV1, restored.FeatureSchemaVersion);
        Assert.Equal(LiquiditySource.Estimated, restored.LiquiditySource);
        Assert.False(restored.Readiness.RequiredP0Ready);
        Assert.Equal(2, restored.Features.Length);
        Assert.Null(restored.Features[1].Value);
        Assert.False(restored.Features[1].IsAvailable);
    }

    [Fact]
    public void Decision_RoundTripsWithModelAndSchemaVersions()
    {
        XauDecision original = ContractTestFactory.CreateDecision();

        XauDecision restored = RoundTrip(original);

        Assert.Equal(original.DecisionId, restored.DecisionId);
        Assert.Equal(DecisionModelType.XauNative, restored.ModelType);
        Assert.Equal("0.1.0", restored.ModelVersion);
        Assert.Equal(ContractVersions.FeatureSchemaV1, restored.FeatureSchemaVersion);
    }

    [Fact]
    public void Settings_RoundTripPreservesTwoModelConfiguration()
    {
        var original = new XauScalpSettings(
            ContractVersions.SettingsV1,
            "settings-001",
            new DecisionModelSettings(
                DecisionModelType.Jev,
                JevFailurePolicy.FallbackToXauNative,
                new ShadowComparisonSettings(true, DecisionModelType.XauNative)),
            ContractTestFactory.CreateRiskSettings());

        XauScalpSettings restored = RoundTrip(original);

        Assert.Equal(DecisionModelType.Jev, restored.DecisionModels.PrimaryDecisionModel);
        Assert.Equal(DecisionModelType.XauNative, restored.DecisionModels.ShadowComparison.ShadowModel);
        Assert.Equal(JevFailurePolicy.FallbackToXauNative, restored.DecisionModels.JevFailurePolicy);
        Assert.Equal("settings-001", restored.SettingsVersion);
    }

    [Fact]
    public void RiskTradeExecutionAndPortfolioContracts_RoundTrip()
    {
        Guid marketStateId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        Guid decisionId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        Guid riskDecisionId = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
        Guid tradeIntentId = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");

        var riskDecision = new RiskDecision(
            ContractVersions.RiskDecisionV1,
            riskDecisionId,
            marketStateId,
            decisionId,
            RiskDecisionOutcome.Authorized,
            "OK",
            null,
            0.10m,
            50m,
            3678m,
            "risk-v1",
            ContractTestFactory.UtcNow);

        var tradePlan = new TradePlan(
            ContractVersions.TradePlanV1,
            tradeIntentId,
            marketStateId,
            decisionId,
            riskDecisionId,
            "XAUUSD",
            "XAUUSD.G",
            TradeSide.Long,
            0.10m,
            3680m,
            3678m,
            3685m,
            "risk-v1",
            "settings-001",
            ContractTestFactory.UtcNow);

        var execution = new ExecutionResult(
            ContractVersions.ExecutionResultV1,
            Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"),
            tradeIntentId,
            OrderLifecycleState.Filled,
            "order-1",
            "deal-1",
            3680m,
            3680.10m,
            0.10m,
            0.10m,
            10,
            TimeSpan.FromMilliseconds(45),
            "DONE",
            null,
            ContractTestFactory.UtcNow);

        var position = new PositionState(
            Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff"),
            tradeIntentId,
            "position-1",
            "XAUUSD",
            "XAUUSD.G",
            TradeSide.Long,
            0.10m,
            3680.10m,
            3681m,
            3678m,
            3685m,
            9m,
            1.2m,
            0.4m,
            ContractTestFactory.UtcNow,
            new PositionOwnership(991188, "runtime-1", "xauscalp"));

        var portfolio = new PortfolioState(
            ContractVersions.PortfolioStateV1,
            ContractTestFactory.UtcNow,
            10_000m,
            10_009m,
            9_000m,
            0m,
            1,
            [position]);

        Assert.Equal(RiskDecisionOutcome.Authorized, RoundTrip(riskDecision).Outcome);
        Assert.Equal(TradeSide.Long, RoundTrip(tradePlan).Side);
        Assert.Equal(OrderLifecycleState.Filled, RoundTrip(execution).State);
        Assert.Single(RoundTrip(portfolio).Positions);
    }

    [Fact]
    public void UnsupportedContractVersion_FailsClosed()
    {
        XauDecision valid = ContractTestFactory.CreateDecision();
        JsonSerializerOptions options = XauJson.CreateOptions();
        string json = JsonSerializer.Serialize(valid, options);
        string incompatible = json.Replace(
            ContractVersions.DecisionV1,
            "xau-decision-v999",
            StringComparison.Ordinal);

        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<XauDecision>(incompatible, options));
    }

    private static T RoundTrip<T>(T original)
    {
        JsonSerializerOptions options = XauJson.CreateOptions();
        string json = JsonSerializer.Serialize(original, options);
        T? restored = JsonSerializer.Deserialize<T>(json, options);

        return Assert.IsType<T>(restored);
    }
}
