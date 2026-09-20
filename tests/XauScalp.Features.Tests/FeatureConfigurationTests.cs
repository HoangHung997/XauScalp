using XauScalp.Domain;
using XauScalp.MarketData;

namespace XauScalp.Features.Tests;

public sealed class FeatureConfigurationTests
{
    [Fact]
    public void SessionSchedule_RejectsOverlappingSegments()
    {
        Assert.Throws<ArgumentException>(
            () => new MarketSessionSchedule(
            [
                new MarketSessionSegment(0, "A", new TimeOnly(8, 0), new TimeOnly(12, 0)),
                new MarketSessionSegment(1, "B", new TimeOnly(11, 0), new TimeOnly(13, 0)),
            ]));
    }

    [Fact]
    public void SessionSchedule_SupportsMidnightWrappingSegment()
    {
        var schedule = new MarketSessionSchedule(
        [
            new MarketSessionSegment(0, "Day", new TimeOnly(6, 0), new TimeOnly(22, 0)),
            new MarketSessionSegment(1, "Night", new TimeOnly(22, 0), new TimeOnly(6, 0)),
        ]);

        Assert.Equal(0, schedule.ResolveCode(new DateTime(2026, 9, 19, 12, 0, 0)));
        Assert.Equal(1, schedule.ResolveCode(new DateTime(2026, 9, 19, 23, 0, 0)));
        Assert.Equal(1, schedule.ResolveCode(new DateTime(2026, 9, 19, 5, 59, 0)));
    }

    [Fact]
    public void UnknownConnectionState_BlocksP0Readiness()
    {
        DateTimeOffset start = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var schedule = new MarketSessionSchedule(
        [
            new MarketSessionSegment(0, "AllDay", TimeOnly.MinValue, new TimeOnly(23, 59, 59)),
        ]);

        var engine = new XauFeatureEngine(
            new XauFeatureEngineOptions(
                BrokerClockConfiguration.UtcV1,
                schedule,
                externalContextMaxAge: TimeSpan.FromMinutes(5)));

        engine.ObserveContext(
            new SymbolSpecificationEvent(
                ContractVersions.MarketEventV1,
                start,
                null,
                sequenceId: 0,
                dataSourceId: "source",
                symbol: "XAUUSD",
                brokerSymbol: "XAUUSD",
                new SymbolSpecification(
                    digits: 2,
                    point: 0.01m,
                    tickSize: 0.01m,
                    tickValue: 1.25m,
                    contractSize: 100m,
                    minVolume: 0.01m,
                    maxVolume: 100m,
                    volumeStep: 0.01m,
                    minStopDistance: 0.50m)));

        engine.SetExternalContext(
            new FeatureExternalContext(
                start,
                atrM1: 2,
                estimatedLatencyMs: 1,
                estimatedSlippagePoints: 1,
                newsDistanceBeforeSec: 600,
                newsDistanceAfterSec: 120,
                isHighImpactNewsWindow: false));

        XauMarketState? state = null;
        for (int second = 0; second <= 16; second++)
        {
            DateTimeOffset timestamp = start.AddSeconds(second);
            state = engine.Update(
                new TickEvent(
                    ContractVersions.MarketEventV1,
                    timestamp,
                    timestamp,
                    second + 1,
                    "source",
                    "XAUUSD",
                    "XAUUSD",
                    100m + second * 0.01m,
                    100.2m + second * 0.01m,
                    null,
                    1,
                    TickFlags.Bid | TickFlags.Ask));
        }

        Assert.NotNull(state);
        Assert.True(state!.Readiness.TickHistoryReady);
        Assert.False(state.Readiness.RequiredP0Ready);
        Assert.Contains("market data connection", state.Readiness.MissingRequirements);
    }

    [Fact]
    public void FutureExternalContext_IsNotConsumedByEarlierState()
    {
        DateTimeOffset now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var engine = new XauFeatureEngine(
            new XauFeatureEngineOptions(BrokerClockConfiguration.UtcV1));

        engine.SetExternalContext(
            new FeatureExternalContext(
                now.AddSeconds(1),
                atrM1: 2,
                estimatedLatencyMs: 1,
                estimatedSlippagePoints: 1,
                newsDistanceBeforeSec: 1,
                newsDistanceAfterSec: 1,
                isHighImpactNewsWindow: false));

        XauMarketState state = engine.Update(
            new TickEvent(
                ContractVersions.MarketEventV1,
                now,
                now,
                1,
                "source",
                "XAUUSD",
                "XAUUSD",
                100m,
                100.2m,
                null,
                1,
                TickFlags.Bid | TickFlags.Ask));

        NumericFeatureValue spreadAtr = Assert.Single(
            state.Features,
            feature => feature.Name == FeatureNames.SpreadAtrRatio);

        Assert.False(spreadAtr.IsAvailable);
    }
}
