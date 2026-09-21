using XauScalp.Domain;

namespace XauScalp.Execution.Tests;

public sealed class TradePlannerTests
{
    [Fact]
    public void AuthorizedLong_BuildsDeterministicPlanFromHardRiskValues()
    {
        DateTimeOffset now = Utc();
        XauMarketState state = State(now);
        XauDecision decision = Decision(state, TradeAction.Long, now);
        RiskDecision risk = Authorized(
            state,
            decision,
            stop: 2495.20m,
            volume: 0.12m,
            now);

        var planner = new TradePlanner(
            new TradePlannerConfiguration(
                "settings-demo-v1",
                TakeProfitDistancePrice: 5m));

        TradePlan first = planner.Create(state, decision, risk);
        TradePlan second = planner.Create(state, decision, risk);

        Assert.Equal(first.TradeIntentId, second.TradeIntentId);
        Assert.Equal(state.Ask, first.PlannedEntryPrice);
        Assert.Equal(TradeSide.Long, first.Side);
        Assert.Equal(0.12m, first.VolumeLots);
        Assert.Equal(2495.20m, first.ProtectiveStopPrice);
        Assert.Equal(state.Ask + 5m, first.TakeProfitPrice);
        Assert.Equal(risk.RiskDecisionId, first.RiskDecisionId);
        Assert.Equal("settings-demo-v1", first.SettingsVersion);
    }

    [Fact]
    public void AuthorizedShort_UsesBidAndPlacesOptionalTargetBelowEntry()
    {
        DateTimeOffset now = Utc();
        XauMarketState state = State(now);
        XauDecision decision = Decision(state, TradeAction.Short, now);
        RiskDecision risk = Authorized(
            state,
            decision,
            stop: 2505m,
            volume: 0.08m,
            now);

        var planner = new TradePlanner(
            new TradePlannerConfiguration(
                "settings-demo-v1",
                TakeProfitDistancePrice: 10m));

        TradePlan plan = planner.Create(state, decision, risk);

        Assert.Equal(TradeSide.Short, plan.Side);
        Assert.Equal(state.Bid, plan.PlannedEntryPrice);
        Assert.Equal(state.Bid - 10m, plan.TakeProfitPrice);
        Assert.Equal(2505m, plan.ProtectiveStopPrice);
    }

    [Fact]
    public void RejectedRiskDecision_CannotBecomeTradePlan()
    {
        DateTimeOffset now = Utc();
        XauMarketState state = State(now);
        XauDecision decision = Decision(state, TradeAction.Long, now);
        var rejected = new RiskDecision(
            ContractVersions.RiskDecisionV1,
            Guid.NewGuid(),
            state.MarketStateId,
            decision.DecisionId,
            RiskDecisionOutcome.Rejected,
            "spread-limit",
            "blocked",
            null,
            null,
            null,
            "risk-v1",
            now);

        var planner = new TradePlanner(
            new TradePlannerConfiguration("settings-demo-v1"));

        Assert.Throws<InvalidOperationException>(
            () => planner.Create(state, decision, rejected));
    }

    [Fact]
    public void LinkageMismatch_FailsClosed()
    {
        DateTimeOffset now = Utc();
        XauMarketState state = State(now);
        XauDecision decision = Decision(state, TradeAction.Long, now);
        RiskDecision risk = Authorized(
            state,
            decision,
            stop: 2495m,
            volume: 0.1m,
            now);

        XauDecision other = Decision(
            state,
            TradeAction.Long,
            now,
            decisionId: Guid.Parse(
                "99999999-9999-4999-8999-999999999999"));

        var planner = new TradePlanner(
            new TradePlannerConfiguration("settings-demo-v1"));

        Assert.Throws<InvalidDataException>(
            () => planner.Create(state, other, risk));
    }

    [Fact]
    public void UnsafeStopSide_FailsClosed()
    {
        DateTimeOffset now = Utc();
        XauMarketState state = State(now);
        XauDecision decision = Decision(state, TradeAction.Long, now);
        RiskDecision risk = Authorized(
            state,
            decision,
            stop: state.Ask + 1m,
            volume: 0.1m,
            now);

        var planner = new TradePlanner(
            new TradePlannerConfiguration("settings-demo-v1"));

        Assert.Throws<InvalidDataException>(
            () => planner.Create(state, decision, risk));
    }

    private static XauMarketState State(DateTimeOffset now)
    {
        return new XauMarketState(
            ContractVersions.MarketStateV1,
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            now,
            now,
            100,
            "XAUUSD",
            "XAUUSD.G",
            2500.00m,
            2500.20m,
            2500.10m,
            ContractVersions.FeatureSchemaV1,
            "planner-test",
            LiquiditySource.None,
            new DataReadiness(
                true,
                true,
                true,
                true,
                []),
            []);
    }

    private static XauDecision Decision(
        XauMarketState state,
        TradeAction action,
        DateTimeOffset now,
        Guid? decisionId = null)
    {
        return new XauDecision(
            ContractVersions.DecisionV1,
            decisionId ?? Guid.Parse(
                "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
            state.MarketStateId,
            DecisionModelType.XauNative,
            action,
            0.8,
            0.8,
            0.2,
            0.7,
            0.3,
            0.1,
            0.7,
            0.2,
            0.1,
            0.8,
            null,
            null,
            null,
            null,
            "native",
            "v1",
            state.FeatureSchemaVersion,
            now,
            TimeSpan.FromMilliseconds(1));
    }

    private static RiskDecision Authorized(
        XauMarketState state,
        XauDecision decision,
        decimal stop,
        decimal volume,
        DateTimeOffset now)
    {
        return new RiskDecision(
            ContractVersions.RiskDecisionV1,
            Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"),
            state.MarketStateId,
            decision.DecisionId,
            RiskDecisionOutcome.Authorized,
            "authorized",
            null,
            volume,
            50m,
            stop,
            "risk-v1",
            now);
    }

    private static DateTimeOffset Utc()
    {
        return new DateTimeOffset(
            2026,
            9,
            21,
            12,
            0,
            0,
            TimeSpan.Zero);
    }
}
