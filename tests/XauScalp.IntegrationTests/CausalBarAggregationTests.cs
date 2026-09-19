using System.Text.Json;
using XauScalp.Domain;
using XauScalp.MarketData;

namespace XauScalp.IntegrationTests;

public sealed class CausalBarAggregationTests
{
    [Fact]
    public void MinuteBoundary_ClosesOnlyElapsedM1Bucket()
    {
        var aggregator = CreateAggregator();

        _ = aggregator.Apply(Tick(1, Utc(12, 0, 59, 900), 100m));
        IReadOnlyList<BarEvent> boundary = aggregator.Apply(Tick(2, Utc(12, 1, 0, 0), 101m));

        BarEvent[] closed = boundary.Where(static item => item.UpdateKind == BarUpdateKind.Closed).ToArray();
        Assert.Single(closed);
        Assert.Equal(BarTimeframe.M1, closed[0].Bar.Timeframe);

        ClosedBarState closedM1 = Assert.IsType<ClosedBarState>(closed[0].Bar);
        Assert.Equal(Utc(12, 0, 0, 0), closedM1.OpenTimeUtc);
        Assert.Equal(Utc(12, 1, 0, 0), closedM1.CloseTimeUtc);

        BarEvent openedM1 = Assert.Single(
            boundary.Where(
                static item => item.UpdateKind == BarUpdateKind.Opened
                    && item.Bar.Timeframe == BarTimeframe.M1));
        Assert.IsType<FormingBarState>(openedM1.Bar);
    }

    [Fact]
    public void HourBoundary_ClosesAllFourRequiredTimeframes()
    {
        var aggregator = CreateAggregator();

        _ = aggregator.Apply(Tick(1, Utc(12, 59, 59, 900), 100m));
        IReadOnlyList<BarEvent> boundary = aggregator.Apply(Tick(2, Utc(13, 0, 0, 0), 101m));

        BarTimeframe[] closed = boundary
            .Where(static item => item.UpdateKind == BarUpdateKind.Closed)
            .Select(static item => item.Bar.Timeframe)
            .ToArray();

        Assert.Equal(
            [BarTimeframe.M1, BarTimeframe.M5, BarTimeframe.M15, BarTimeframe.H1],
            closed);
    }

    [Fact]
    public void DayBoundary_UsesUtcBucketsWithoutInventingDailyBars()
    {
        var aggregator = CreateAggregator();
        DateTimeOffset beforeMidnight = new(2026, 9, 19, 23, 59, 59, 900, TimeSpan.Zero);
        DateTimeOffset midnight = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

        _ = aggregator.Apply(Tick(1, beforeMidnight, 100m));
        IReadOnlyList<BarEvent> boundary = aggregator.Apply(Tick(2, midnight, 101m));

        BarEvent h1CloseEvent = Assert.Single(
            boundary.Where(
                static item => item.UpdateKind == BarUpdateKind.Closed
                    && item.Bar.Timeframe == BarTimeframe.H1));

        ClosedBarState h1Close = Assert.IsType<ClosedBarState>(h1CloseEvent.Bar);
        Assert.Equal(new DateTimeOffset(2026, 9, 19, 23, 0, 0, TimeSpan.Zero), h1Close.OpenTimeUtc);
        Assert.Equal(midnight, h1Close.CloseTimeUtc);

        BarEvent h1OpenEvent = Assert.Single(
            boundary.Where(
                static item => item.UpdateKind == BarUpdateKind.Opened
                    && item.Bar.Timeframe == BarTimeframe.H1));
        Assert.Equal(midnight, h1OpenEvent.Bar.OpenTimeUtc);
    }

    [Fact]
    public void MissingTickGap_ClosesPreviousBarButDoesNotCreateEmptyCandles()
    {
        var aggregator = CreateAggregator();

        IReadOnlyList<BarEvent> first = aggregator.Apply(Tick(1, Utc(12, 0, 10, 0), 100m));
        IReadOnlyList<BarEvent> later = aggregator.Apply(Tick(2, Utc(12, 3, 5, 0), 103m));

        DateTimeOffset[] openedM1 = first
            .Concat(later)
            .Where(
                static item => item.UpdateKind == BarUpdateKind.Opened
                    && item.Bar.Timeframe == BarTimeframe.M1)
            .Select(static item => item.Bar.OpenTimeUtc)
            .ToArray();

        Assert.Equal([Utc(12, 0, 0, 0), Utc(12, 3, 0, 0)], openedM1);
        Assert.DoesNotContain(Utc(12, 1, 0, 0), openedM1);
        Assert.DoesNotContain(Utc(12, 2, 0, 0), openedM1);

        ClosedBarState closed = Assert.IsType<ClosedBarState>(
            Assert.Single(
                later.Where(
                    static item => item.UpdateKind == BarUpdateKind.Closed
                        && item.Bar.Timeframe == BarTimeframe.M1))
                .Bar);

        Assert.Equal(Utc(12, 1, 0, 0), closed.CloseTimeUtc);
        Assert.Equal(1, closed.TickCount);
    }

    [Fact]
    public void DuplicateTimestamp_IsProcessedInArrivalOrderInsideFormingBar()
    {
        var aggregator = CreateAggregator();
        DateTimeOffset timestamp = Utc(12, 0, 15, 123);

        _ = aggregator.Apply(Tick(1, timestamp, 100m));
        _ = aggregator.Apply(Tick(2, timestamp, 105m));
        IReadOnlyList<BarEvent> third = aggregator.Apply(Tick(3, timestamp, 99m));

        BarEvent update = Assert.Single(
            third.Where(
                static item => item.UpdateKind == BarUpdateKind.Updated
                    && item.Bar.Timeframe == BarTimeframe.M1));
        FormingBarState forming = Assert.IsType<FormingBarState>(update.Bar);

        Assert.Equal(100m, forming.Open);
        Assert.Equal(105m, forming.High);
        Assert.Equal(99m, forming.Low);
        Assert.Equal(99m, forming.Close);
        Assert.Equal(3, forming.TickCount);
        Assert.Equal(timestamp, forming.LastUpdateUtc);
    }

    [Fact]
    public void CurrentM1_HasAgeAndProgress_AndFinalValuesExistOnlyAfterClose()
    {
        var aggregator = CreateAggregator();

        IReadOnlyList<BarEvent> first = aggregator.Apply(Tick(1, Utc(12, 0, 5, 0), 100m));
        IReadOnlyList<BarEvent> second = aggregator.Apply(Tick(2, Utc(12, 0, 30, 0), 105m));

        Assert.All(first.Concat(second), static item => Assert.IsType<FormingBarState>(item.Bar));

        FormingBarSnapshot snapshot = Assert.IsType<FormingBarSnapshot>(
            aggregator.GetFormingSnapshot(BarTimeframe.M1, Utc(12, 0, 40, 0)));

        Assert.Equal(TimeSpan.FromSeconds(40), snapshot.Age);
        Assert.Equal(2.0 / 3.0, snapshot.Progress01, precision: 6);
        Assert.False(snapshot.BoundaryElapsed);
        Assert.Equal(105m, snapshot.State.High);

        IReadOnlyList<BarEvent> crossed = aggregator.Apply(Tick(3, Utc(12, 1, 0, 0), 200m));
        ClosedBarState closed = Assert.IsType<ClosedBarState>(
            Assert.Single(
                crossed.Where(
                    static item => item.UpdateKind == BarUpdateKind.Closed
                        && item.Bar.Timeframe == BarTimeframe.M1))
                .Bar);

        Assert.Equal(100m, closed.Open);
        Assert.Equal(105m, closed.High);
        Assert.Equal(100m, closed.Low);
        Assert.Equal(105m, closed.Close);
        Assert.Equal(2, closed.TickCount);

        _ = aggregator.Apply(Tick(4, Utc(12, 1, 30, 0), 250m));

        Assert.Equal(105m, closed.High);
        Assert.Equal(105m, closed.Close);
    }

    [Fact]
    public void TimestampRegression_FailsClosedInsteadOfReordering()
    {
        var aggregator = CreateAggregator();

        _ = aggregator.Apply(Tick(1, Utc(12, 0, 10, 0), 100m));

        Assert.Throws<InvalidDataException>(
            () => aggregator.Apply(Tick(2, Utc(12, 0, 9, 999), 101m)));
    }

    [Fact]
    public void BrokerClockOffsetChange_IsEffectiveDated_WhileBarBucketsStayUtc()
    {
        DateTimeOffset transition = new(2026, 3, 29, 1, 0, 0, TimeSpan.Zero);
        var configuration = new BrokerClockConfiguration(
            "broker-clock-spring-2026",
            new TimeOnly(17, 0),
            [
                new BrokerClockSegment(DateTimeOffset.MinValue, TimeSpan.FromHours(2)),
                new BrokerClockSegment(transition, TimeSpan.FromHours(3)),
            ]);
        var normalizer = new MarketClockNormalizer(configuration);

        NormalizedMarketTime before = normalizer.Normalize(transition.AddMilliseconds(-1));
        NormalizedMarketTime after = normalizer.Normalize(transition);

        Assert.Equal(TimeSpan.FromHours(2), before.BrokerUtcOffset);
        Assert.Equal(new DateTime(2026, 3, 29, 2, 59, 59, 999, DateTimeKind.Unspecified), before.BrokerLocalDateTime);
        Assert.Equal(TimeSpan.FromHours(3), after.BrokerUtcOffset);
        Assert.Equal(new DateTime(2026, 3, 29, 4, 0, 0, DateTimeKind.Unspecified), after.BrokerLocalDateTime);

        var aggregator = new CausalBarAggregator(normalizer, "bars-test");
        _ = aggregator.Apply(Tick(1, transition.AddMilliseconds(-1), 100m));
        IReadOnlyList<BarEvent> boundary = aggregator.Apply(Tick(2, transition, 101m));

        ClosedBarState h1Closed = Assert.IsType<ClosedBarState>(
            Assert.Single(
                boundary.Where(
                    static item => item.UpdateKind == BarUpdateKind.Closed
                        && item.Bar.Timeframe == BarTimeframe.H1))
                .Bar);

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero), h1Closed.OpenTimeUtc);
        Assert.Equal(transition, h1Closed.CloseTimeUtc);
    }

    [Fact]
    public void SessionDate_UsesConfiguredBrokerWallClockBoundary()
    {
        var normalizer = new MarketClockNormalizer(
            new BrokerClockConfiguration(
                "session-v1",
                new TimeOnly(17, 0),
                [new BrokerClockSegment(DateTimeOffset.MinValue, TimeSpan.FromHours(2))]));

        DateTimeOffset day = new(2026, 9, 19, 14, 59, 0, TimeSpan.Zero);
        NormalizedMarketTime before = normalizer.Normalize(day);
        NormalizedMarketTime atStart = normalizer.Normalize(day.AddMinutes(1));

        Assert.Equal(new DateOnly(2026, 9, 19), before.BrokerDate);
        Assert.Equal(new DateOnly(2026, 9, 18), before.SessionDate);
        Assert.Equal(new DateOnly(2026, 9, 19), atStart.SessionDate);
    }

    [Fact]
    public void SameTickStream_ProducesIdenticalBarEvents()
    {
        TickEvent[] ticks =
        [
            Tick(1, Utc(12, 0, 0, 0), 100m),
            Tick(2, Utc(12, 0, 0, 0), 101m),
            Tick(3, Utc(12, 0, 45, 0), 99m),
            Tick(4, Utc(12, 1, 0, 0), 102m),
            Tick(5, Utc(12, 6, 15, 0), 103m),
        ];

        List<BarEvent> first = ApplyAll(CreateAggregator(), ticks);
        List<BarEvent> second = ApplyAll(CreateAggregator(), ticks);

        JsonSerializerOptions options = XauJson.CreateOptions();
        string[] firstJson = first
            .Select(item => JsonSerializer.Serialize(item, options))
            .ToArray();
        string[] secondJson = second
            .Select(item => JsonSerializer.Serialize(item, options))
            .ToArray();

        Assert.Equal(firstJson, secondJson);
    }

    private static CausalBarAggregator CreateAggregator()
    {
        return new CausalBarAggregator(
            new MarketClockNormalizer(BrokerClockConfiguration.UtcV1),
            "bars-test");
    }

    private static List<BarEvent> ApplyAll(CausalBarAggregator aggregator, IEnumerable<TickEvent> ticks)
    {
        var result = new List<BarEvent>();
        foreach (TickEvent tick in ticks)
        {
            result.AddRange(aggregator.Apply(tick));
        }

        return result;
    }

    private static TickEvent Tick(long sequence, DateTimeOffset timestampUtc, decimal bid)
    {
        return new TickEvent(
            ContractVersions.MarketEventV1,
            timestampUtc,
            timestampUtc.ToOffset(TimeSpan.FromHours(2)),
            sequence,
            "mt5-test",
            "XAUUSD",
            "XAUUSD.G",
            bid,
            bid + 0.20m,
            null,
            0,
            TickFlags.Bid | TickFlags.Ask);
    }

    private static DateTimeOffset Utc(int hour, int minute, int second, int millisecond)
    {
        return new DateTimeOffset(2026, 9, 19, hour, minute, second, millisecond, TimeSpan.Zero);
    }
}
