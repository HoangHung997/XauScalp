using XauScalp.Domain;
using XauScalp.Persistence;

namespace XauScalp.Risk.Tests;

public sealed class HardRiskEngineTests
{
    private static readonly PositionOwnership Ownership =
        new(991188, "runtime-1", "xauscalp");

    [Fact]
    public void MonetarySizing_UsesTickSizeTickValueAndFloorsVolumeStep()
    {
        TestContext context = MakeContext(
            profile: Profile(
                tickSize: 0.10m,
                tickValue: 2m,
                minVolume: 0.01m,
                maxVolume: 100m,
                volumeStep: 0.01m),
            stopDistance: 2m,
            riskPct: 1);

        RiskDecision result = context.Engine.Evaluate(
            context.State,
            context.Decision,
            context.Portfolio,
            context.Settings);

        // 10,000 * 1% = 100 risk.
        // 2 / .1 = 20 ticks; 20 * 2 = 40 loss/lot.
        // 100 / 40 = 2.5 lots.
        Assert.Equal(RiskDecisionOutcome.Authorized, result.Outcome);
        Assert.Equal(2.50m, result.AuthorizedVolumeLots);
        Assert.Equal(100m, result.AuthorizedRiskMoney);
        Assert.Equal(98.2m, result.ProtectiveStopPrice);
    }

    [Fact]
    public void DifferentTickSizeValue_ChangesLotSizeWithoutChangingRiskMoney()
    {
        TestContext first = MakeContext(
            profile: Profile(
                tickSize: 0.01m,
                tickValue: 1m),
            stopDistance: 2m,
            riskPct: 1);

        TestContext second = MakeContext(
            profile: Profile(
                tickSize: 0.10m,
                tickValue: 1m),
            stopDistance: 2m,
            riskPct: 1);

        RiskDecision one = first.Engine.Evaluate(
            first.State,
            first.Decision,
            first.Portfolio,
            first.Settings);
        RiskDecision two = second.Engine.Evaluate(
            second.State,
            second.Decision,
            second.Portfolio,
            second.Settings);

        Assert.Equal(0.50m, one.AuthorizedVolumeLots);
        Assert.Equal(5.00m, two.AuthorizedVolumeLots);
        Assert.Equal(100m, one.AuthorizedRiskMoney);
        Assert.Equal(100m, two.AuthorizedRiskMoney);
    }

    [Fact]
    public void InvalidTickValue_FailsClosed()
    {
        TestContext context = MakeContext(
            profile: Profile(tickValue: 0));

        RiskDecision result = context.Engine.Evaluate(
            context.State,
            context.Decision,
            context.Portfolio,
            context.Settings);

        Assert.Equal(RiskDecisionOutcome.Rejected, result.Outcome);
        Assert.Equal("invalid-symbol-profile", result.ReasonCode);
    }

    [Fact]
    public void BelowMinimumLot_FailsClosed()
    {
        TestContext context = MakeContext(
            profile: Profile(minVolume: 1m),
            stopDistance: 20m,
            riskPct: 0.01);

        RiskDecision result = context.Engine.Evaluate(
            context.State,
            context.Decision,
            context.Portfolio,
            context.Settings);

        Assert.Equal("below-min-volume", result.ReasonCode);
    }

    [Fact]
    public void MinimumStopDistance_IsEnforced()
    {
        TestContext context = MakeContext(
            profile: Profile(minStopDistance: 3m),
            stopDistance: 2m);

        RiskDecision result = context.Engine.Evaluate(
            context.State,
            context.Decision,
            context.Portfolio,
            context.Settings);

        Assert.Equal("min-stop-distance", result.ReasonCode);
    }

    [Fact]
    public void SpreadAndStaleDecisionGuardsFailClosed()
    {
        TestContext spreadContext = MakeContext(spread: 2m);

        RiskDecision spread = spreadContext.Engine.Evaluate(
            spreadContext.State,
            spreadContext.Decision,
            spreadContext.Portfolio,
            spreadContext.Settings);

        Assert.Equal("spread-price-limit", spread.ReasonCode);

        DateTimeOffset now = Utc(12, 0, 10);
        TestContext staleContext = MakeContext(
            now: now,
            stateAt: now,
            decisionAt: now.AddSeconds(-5),
            maxDecisionAgeMs: 1000);

        RiskDecision stale = staleContext.Engine.Evaluate(
            staleContext.State,
            staleContext.Decision,
            staleContext.Portfolio,
            staleContext.Settings);

        Assert.Equal("decision-stale", stale.ReasonCode);
    }

    [Fact]
    public void DailyLossUsesOwnedLedger_NotAccountWideOrForeignEaTrades()
    {
        DateTimeOffset now = Utc(12, 0, 10);

        TestContext context = MakeContext(
            now: now,
            portfolioRealized: -9_000m,
            ledger: new OwnedRiskLedgerSnapshot(
                DateOnly.FromDateTime(now.UtcDateTime),
                IsReady: true,
                DayStartEquity: 10_000m,
                RealizedNetPnlMoney: -50m,
                ClosedTrades: 1,
                LastLossAtUtc: now.AddMinutes(-10),
                LastExecutionFailureAtUtc: null),
            maxDailyLossMoney: 500m,
            maxDailyLossPct: 5);

        RiskDecision result = context.Engine.Evaluate(
            context.State,
            context.Decision,
            context.Portfolio,
            context.Settings);

        Assert.Equal(RiskDecisionOutcome.Authorized, result.Outcome);
    }

    [Fact]
    public void OwnedDailyLossAndCooldownLockEntries()
    {
        DateTimeOffset now = Utc(12, 0, 10);

        TestContext lossContext = MakeContext(
            now: now,
            ledger: new OwnedRiskLedgerSnapshot(
                DateOnly.FromDateTime(now.UtcDateTime),
                true,
                10_000m,
                -600m,
                2,
                now.AddMinutes(-10),
                null),
            maxDailyLossMoney: 500m);

        RiskDecision loss = lossContext.Engine.Evaluate(
            lossContext.State,
            lossContext.Decision,
            lossContext.Portfolio,
            lossContext.Settings);

        Assert.Equal("daily-loss-money-lock", loss.ReasonCode);

        TestContext cooldownContext = MakeContext(
            now: now,
            ledger: new OwnedRiskLedgerSnapshot(
                DateOnly.FromDateTime(now.UtcDateTime),
                true,
                10_000m,
                -10m,
                1,
                now.AddSeconds(-30),
                null),
            cooldownAfterLossSec: 60);

        RiskDecision cooldown = cooldownContext.Engine.Evaluate(
            cooldownContext.State,
            cooldownContext.Decision,
            cooldownContext.Portfolio,
            cooldownContext.Settings);

        Assert.Equal("loss-cooldown", cooldown.ReasonCode);
    }

    [Fact]
    public void ForeignPositionDoesNotConsumeOwnedConcurrentLimit()
    {
        PositionState foreign = Position(
            new PositionOwnership(
                777,
                "other-runtime",
                "other-strategy"));

        TestContext context = MakeContext(
            positions: [foreign],
            maxConcurrentPositions: 1);

        RiskDecision result = context.Engine.Evaluate(
            context.State,
            context.Decision,
            context.Portfolio,
            context.Settings);

        Assert.Equal(RiskDecisionOutcome.Authorized, result.Outcome);
    }

    [Fact]
    public void OwnedPositionConsumesConcurrentLimit()
    {
        TestContext context = MakeContext(
            positions: [Position(Ownership)],
            maxConcurrentPositions: 1);

        RiskDecision result = context.Engine.Evaluate(
            context.State,
            context.Decision,
            context.Portfolio,
            context.Settings);

        Assert.Equal("concurrent-position-limit", result.ReasonCode);
    }

    [Fact]
    public void UnavailableNewsFeedFailsClosedWhenBlockingEnabled()
    {
        TestContext context = MakeContext(newsAvailable: false);

        RiskDecision result = context.Engine.Evaluate(
            context.State,
            context.Decision,
            context.Portfolio,
            context.Settings);

        Assert.Equal("news-unavailable", result.ReasonCode);
    }

    [Fact]
    public void HighImpactNewsWindowBlocksEntry()
    {
        TestContext context = MakeContext(highImpactNews: true);

        RiskDecision result = context.Engine.Evaluate(
            context.State,
            context.Decision,
            context.Portfolio,
            context.Settings);

        Assert.Equal("high-impact-news", result.ReasonCode);
    }

    [Fact]
    public void InsufficientPostTradeFreeMarginFailsClosed()
    {
        TestContext context = MakeContext(
            freeMargin: 100m,
            estimatedMarginPerLot: 1_000m);

        RiskDecision result = context.Engine.Evaluate(
            context.State,
            context.Decision,
            context.Portfolio,
            context.Settings);

        Assert.Equal("free-margin-limit", result.ReasonCode);
    }

    [Fact]
    public async Task LedgerReconstructsOwnedDailyStateAcrossRestartAndIgnoresForeignTrade()
    {
        DateTimeOffset now = Utc(12, 0, 0);
        string path = Path.Combine(
            Path.GetTempPath(),
            "xauscalp-risk-tests",
            Guid.NewGuid().ToString("N"),
            "risk-ledger.jsonl");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            await using (var first = new RiskLedgerJsonlStore(path, Ownership))
            {
                await first.StartTradingDayAsync(
                    now.AddHours(-1),
                    10_000m);

                await first.RecordClosedTradeAsync(
                    now.AddMinutes(-30),
                    realizedPnlMoney: -100m,
                    commissionCostMoney: 5m,
                    Ownership);

                await first.RecordClosedTradeAsync(
                    now.AddMinutes(-20),
                    realizedPnlMoney: -9_000m,
                    commissionCostMoney: 0m,
                    new PositionOwnership(
                        777,
                        "other",
                        "other-strategy"));
            }

            await using var reopened = new RiskLedgerJsonlStore(
                path,
                Ownership);

            OwnedRiskLedgerSnapshot snapshot = reopened.GetSnapshot(now);

            Assert.True(snapshot.IsReady);
            Assert.Equal(10_000m, snapshot.DayStartEquity);
            Assert.Equal(-105m, snapshot.RealizedNetPnlMoney);
            Assert.Equal(1, snapshot.ClosedTrades);
            Assert.Equal(now.AddMinutes(-30), snapshot.LastLossAtUtc);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            string? directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LedgerNewTradingDayFailsClosedUntilExplicitlyInitialized()
    {
        DateTimeOffset dayOne = Utc(12, 0, 0);
        string path = Path.Combine(
            Path.GetTempPath(),
            "xauscalp-risk-tests",
            Guid.NewGuid().ToString("N"),
            "risk-ledger.jsonl");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            await using var store = new RiskLedgerJsonlStore(path, Ownership);
            await store.StartTradingDayAsync(dayOne, 10_000m);

            OwnedRiskLedgerSnapshot nextDay = store.GetSnapshot(
                dayOne.AddDays(1));

            Assert.False(nextDay.IsReady);
            Assert.Equal(0m, nextDay.DayStartEquity);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            string? directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static TestContext MakeContext(
        DateTimeOffset? now = null,
        DateTimeOffset? stateAt = null,
        DateTimeOffset? decisionAt = null,
        RiskSymbolProfile? profile = null,
        OwnedRiskLedgerSnapshot? ledger = null,
        decimal stopDistance = 2m,
        double riskPct = 0.5,
        decimal spread = 0.2m,
        bool newsAvailable = true,
        bool highImpactNews = false,
        decimal freeMargin = 8_000m,
        decimal estimatedMarginPerLot = 100m,
        decimal portfolioRealized = 0m,
        PositionState[]? positions = null,
        int maxConcurrentPositions = 1,
        decimal? maxDailyLossMoney = 500m,
        double maxDailyLossPct = 5,
        int maxDecisionAgeMs = 1_000,
        int cooldownAfterLossSec = 0)
    {
        DateTimeOffset clock = now ?? Utc(12, 0, 0);
        DateTimeOffset stateTime = stateAt ?? clock;
        DateTimeOffset decisionTime = decisionAt ?? clock;

        RiskSymbolProfile actualProfile = profile ?? Profile(
            estimatedMarginPerLot: estimatedMarginPerLot);

        OwnedRiskLedgerSnapshot actualLedger = ledger
            ?? new(
                DateOnly.FromDateTime(clock.UtcDateTime),
                true,
                10_000m,
                0m,
                0,
                null,
                null);

        NumericFeatureValue[] features =
        [
            Feature("SpreadAtrRatio", 0.1, stateTime),
            newsAvailable
                ? Feature(
                    "IsHighImpactNewsWindow",
                    highImpactNews ? 1 : 0,
                    stateTime)
                : new NumericFeatureValue(
                    "IsHighImpactNewsWindow",
                    null,
                    "boolean",
                    isAvailable: false,
                    observedAtUtc: null,
                    unavailableReason: "news unavailable"),
        ];

        var state = new XauMarketState(
            ContractVersions.MarketStateV1,
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            stateTime,
            stateTime,
            42,
            "XAUUSD",
            "XAUUSD.G",
            100m,
            100m + spread,
            100m + (spread / 2m),
            ContractVersions.FeatureSchemaV1,
            "risk-test",
            LiquiditySource.None,
            new DataReadiness(
                requiredP0Ready: true,
                tickHistoryReady: true,
                barHistoryReady: true,
                newsDataAvailable: newsAvailable,
                missingRequirements: []),
            features);

        var decision = new XauDecision(
            ContractVersions.DecisionV1,
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
            state.MarketStateId,
            DecisionModelType.XauNative,
            TradeAction.Long,
            0.7,
            0.7,
            0.3,
            0.6,
            0.4,
            0.2,
            0.6,
            0.4,
            0.1,
            0.7,
            null,
            null,
            null,
            null,
            "xau-native",
            "test-v1",
            state.FeatureSchemaVersion,
            decisionTime,
            TimeSpan.Zero);

        var portfolio = new PortfolioState(
            ContractVersions.PortfolioStateV1,
            clock,
            10_000m,
            10_000m,
            freeMargin,
            portfolioRealized,
            0,
            positions ?? []);

        var settings = new RiskSettings(
            maxRiskPerTradePct: riskPct,
            maxDailyLossPct,
            maxDailyLossMoney,
            maxTradesPerDay: 20,
            maxConcurrentPositions,
            maxSpreadPrice: 1m,
            maxSpreadAtrRatio: 0.5,
            maxSlippagePoints: 30,
            maxDecisionAgeMs,
            maxFeatureAgeMs: 1_000,
            minFreeMarginPct: 20,
            cooldownAfterLossSec,
            cooldownAfterExecutionFailureSec: 60,
            highImpactNewsBlockBeforeSec: 900,
            highImpactNewsBlockAfterSec: 900,
            allowLong: true,
            allowShort: true);

        var engine = new HardRiskEngine(
            new FixedRiskRuntimeContextProvider(actualProfile),
            new FixedOwnedRiskLedger(actualLedger),
            new RiskPolicyConfiguration(
                "risk-xsp010-v1",
                stopDistance,
                Ownership),
            new FixedTimeProvider(clock));

        return new TestContext(
            engine,
            state,
            decision,
            portfolio,
            settings);
    }

    private static RiskSymbolProfile Profile(
        decimal point = 0.01m,
        decimal tickSize = 0.01m,
        decimal tickValue = 1m,
        decimal minVolume = 0.01m,
        decimal maxVolume = 100m,
        decimal volumeStep = 0.01m,
        decimal minStopDistance = 0.5m,
        decimal estimatedMarginPerLot = 100m)
    {
        return new RiskSymbolProfile(
            "XAUUSD",
            "XAUUSD.G",
            point,
            tickSize,
            tickValue,
            minVolume,
            maxVolume,
            volumeStep,
            minStopDistance,
            estimatedMarginPerLot);
    }

    private static NumericFeatureValue Feature(
        string name,
        double value,
        DateTimeOffset timestamp)
    {
        return new NumericFeatureValue(
            name,
            value,
            "test",
            true,
            timestamp,
            null);
    }

    private static PositionState Position(PositionOwnership ownership)
    {
        return new PositionState(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid().ToString("N"),
            "XAUUSD",
            "XAUUSD.G",
            TradeSide.Long,
            0.1m,
            100m,
            100m,
            98m,
            105m,
            0m,
            0m,
            0m,
            Utc(11, 0, 0),
            ownership);
    }

    private static DateTimeOffset Utc(int hour, int minute, int second)
    {
        return new DateTimeOffset(
            2026,
            9,
            21,
            hour,
            minute,
            second,
            TimeSpan.Zero);
    }

    private sealed record TestContext(
        HardRiskEngine Engine,
        XauMarketState State,
        XauDecision Decision,
        PortfolioState Portfolio,
        RiskSettings Settings);

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
