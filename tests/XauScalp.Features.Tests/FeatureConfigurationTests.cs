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
