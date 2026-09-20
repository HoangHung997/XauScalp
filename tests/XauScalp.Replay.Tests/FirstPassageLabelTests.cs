using XauScalp.Domain;

namespace XauScalp.Replay.Tests;

public sealed class FirstPassageLabelTests
{
    [Fact]
    public void IdenticalM1Ohlc_OppositeTickOrder_ChangesFirstPassageOutcome()
    {
        DateTimeOffset start = Utc(12, 0, 0);

        ReplayOutputRecord[] highFirst =
        [
            Record(0, Tick(1, start, 100m), State(1, start, 100.1m)),
            Record(1, Tick(2, start.AddSeconds(10), 110m), null),
            Record(2, Tick(3, start.AddSeconds(20), 90m), null),
            Record(3, Tick(4, start.AddSeconds(50), 100m), null),
        ];

        ReplayOutputRecord[] lowFirst =
        [
            Record(0, Tick(1, start, 100m), State(1, start, 100.1m)),
            Record(1, Tick(2, start.AddSeconds(10), 90m), null),
            Record(2, Tick(3, start.AddSeconds(20), 110m), null),
            Record(3, Tick(4, start.AddSeconds(50), 100m), null),
        ];

        var settings = new FirstPassageLabelSettings(
            targetDistancesPrice: [5m, 10m],
            adverseBarriersPrice: [5m],
            holdingHorizons: [TimeSpan.FromMinutes(1)]);

        var engine = new FirstPassageLabelEngine();

        FirstPassageHorizonLabel highFirstLabel = Assert.Single(
            Assert.Single(engine.Generate(highFirst, settings)).Horizons);

        FirstPassageHorizonLabel lowFirstLabel = Assert.Single(
            Assert.Single(engine.Generate(lowFirst, settings)).Horizons);

        BarrierFirstLabel highFirstLong = Assert.Single(
            highFirstLabel.BarrierFirst,
            label => label.Side == TradeSide.Long
                && label.TargetDistancePrice == 5m
                && label.AdverseBarrierPrice == 5m);

        BarrierFirstLabel lowFirstLong = Assert.Single(
            lowFirstLabel.BarrierFirst,
            label => label.Side == TradeSide.Long
                && label.TargetDistancePrice == 5m
                && label.AdverseBarrierPrice == 5m);

        Assert.Equal(FirstPassageOutcome.TargetFirst, highFirstLong.Outcome);
        Assert.Equal(FirstPassageOutcome.AdverseFirst, lowFirstLong.Outcome);

        // Both ordered paths have the same O/H/L/final value: 100 / 110 / 90 / 100.
        Assert.Equal(110.1m, highFirst.Max(record => Mid(record.MarketEvent)));
        Assert.Equal(110.1m, lowFirst.Max(record => Mid(record.MarketEvent)));
        Assert.Equal(90.1m, highFirst.Min(record => Mid(record.MarketEvent)));
        Assert.Equal(90.1m, lowFirst.Min(record => Mid(record.MarketEvent)));
        Assert.Equal(Mid(highFirst[^1].MarketEvent), Mid(lowFirst[^1].MarketEvent));
    }

    [Fact]
    public void Labels_CaptureHitTimesExcursionsAndCensoredHorizon()
    {
        DateTimeOffset start = Utc(12, 0, 0);
        ReplayOutputRecord[] records =
        [
            Record(0, Tick(1, start, 100m), State(1, start, 100.1m)),
            Record(1, Tick(2, start.AddSeconds(5), 103m), null),
            Record(2, Tick(3, start.AddSeconds(10), 106m), null),
            Record(3, Tick(4, start.AddSeconds(20), 96m), null),
        ];

        var settings = new FirstPassageLabelSettings(
            targetDistancesPrice: [5m, 10m],
            adverseBarriersPrice: [4m],
            holdingHorizons:
            [
                TimeSpan.FromSeconds(15),
                TimeSpan.FromMinutes(1),
            ]);

        FirstPassageStateLabels labels = Assert.Single(
            new FirstPassageLabelEngine().Generate(records, settings));

        FirstPassageHorizonLabel shortHorizon = labels.Horizons[0];
        DistanceHitPair hit5 = Assert.Single(
            shortHorizon.DistanceHits,
            hit => hit.DistancePrice == 5m);

        Assert.NotNull(hit5.UpHit);
        Assert.Equal(TimeSpan.FromSeconds(10), hit5.UpHit!.Elapsed);
        Assert.Equal(6m, shortHorizon.LongExcursion.MfePrice);
        Assert.Equal(0m, shortHorizon.LongExcursion.MaePrice);
        Assert.False(shortHorizon.IsCensoredByDatasetEnd);

        FirstPassageHorizonLabel longHorizon = labels.Horizons[1];
        Assert.True(longHorizon.IsCensoredByDatasetEnd);
        Assert.Equal(6m, longHorizon.LongExcursion.MfePrice);
        Assert.Equal(4m, longHorizon.LongExcursion.MaePrice);

        BarrierFirstLabel longFiveFour = Assert.Single(
            longHorizon.BarrierFirst,
            label => label.Side == TradeSide.Long
                && label.TargetDistancePrice == 5m
                && label.AdverseBarrierPrice == 4m);

        Assert.Equal(FirstPassageOutcome.TargetFirst, longFiveFour.Outcome);
    }

    [Fact]
    public void Labels_AreSeparateFromMarketStateContract()
    {
        Assert.DoesNotContain(
            typeof(XauMarketState).GetProperties(),
            property => property.Name.Contains("Label", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            typeof(XauMarketState).GetProperties(),
            property => property.Name.Contains("Future", StringComparison.OrdinalIgnoreCase));
    }

    private static ReplayOutputRecord Record(
        long ordinal,
        TickEvent tick,
        XauMarketState? state)
    {
        return new ReplayOutputRecord(
            ordinal,
            tick,
            state,
            ReplayCostScenario.Ideal);
    }

    private static TickEvent Tick(
        long sequence,
        DateTimeOffset timestamp,
        decimal bid)
    {
        return new TickEvent(
            ContractVersions.MarketEventV1,
            timestamp,
            timestamp,
            sequence,
            "label-test",
            "XAUUSD",
            "XAUUSD",
            bid,
            bid + 0.2m,
            null,
            1,
            TickFlags.Bid | TickFlags.Ask);
    }

    private static XauMarketState State(
        long sequence,
        DateTimeOffset timestamp,
        decimal mid)
    {
        return new XauMarketState(
            ContractVersions.MarketStateV1,
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            timestamp,
            timestamp,
            sequence,
            "XAUUSD",
            "XAUUSD",
            mid - 0.1m,
            mid + 0.1m,
            mid,
            ContractVersions.FeatureSchemaV1,
            "label-test",
            LiquiditySource.None,
            new DataReadiness(
                requiredP0Ready: true,
                tickHistoryReady: true,
                barHistoryReady: true,
                newsDataAvailable: true,
                missingRequirements: []),
            []);
    }

    private static decimal Mid(MarketEvent marketEvent)
    {
        TickEvent tick = Assert.IsType<TickEvent>(marketEvent);
        return (tick.Bid + tick.Ask) / 2m;
    }

    private static DateTimeOffset Utc(int hour, int minute, int second)
    {
        return new DateTimeOffset(2026, 9, 19, hour, minute, second, TimeSpan.Zero);
    }
}
