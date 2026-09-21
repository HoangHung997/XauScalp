using System.Text.Json;
using XauScalp.Domain;

namespace XauScalp.Features.Tests;

public sealed class CausalStructureRegimeTrackerTests
{
    [Fact]
    public void ConfirmedSwingBreakThenOppositeBreak_ProducesBosMssAndChochCausally()
    {
        var tracker = new CausalStructureRegimeTracker();
        DateTimeOffset start = Utc(12, 0, 0);

        decimal[] highs = [101m, 103m, 105m, 103m, 101m];
        decimal[] lows = [99m, 97m, 95m, 97m, 99m];

        for (int index = 0; index < highs.Length; index++)
        {
            tracker.ObserveClosedBar(Bar(BarTimeframe.M1, start.AddMinutes(index), 100m, highs[index], lows[index], 100m));
        }

        for (int index = 5; index < 20; index++)
        {
            tracker.ObserveClosedBar(Bar(BarTimeframe.M1, start.AddMinutes(index), 100m, 101m, 99m, 100m));
        }

        tracker.ObserveClosedBar(
            Bar(BarTimeframe.M1, start.AddMinutes(20), 100m, 106m, 99m, 106m));

        IReadOnlyList<StructureFeatureMetric> afterUp = tracker.Snapshot(
            start.AddMinutes(21),
            106m,
            null);

        Assert.Equal(1, Value(afterUp, FeatureNames.BosDirection));

        tracker.ObserveClosedBar(
            Bar(BarTimeframe.M1, start.AddMinutes(21), 106m, 106m, 94m, 94m));

        IReadOnlyList<StructureFeatureMetric> afterDown = tracker.Snapshot(
            start.AddMinutes(22),
            94m,
            null);

        Assert.Equal(-1, Value(afterDown, FeatureNames.BosDirection));
        Assert.Equal(-1, Value(afterDown, FeatureNames.MssDirection));
        Assert.Equal(-1, Value(afterDown, FeatureNames.ChochDirection));
    }

    [Fact]
    public void BullFvg_IsUnavailableUntilThirdClosedBarThenTracksFillCausally()
    {
        var tracker = new CausalStructureRegimeTracker();
        DateTimeOffset start = Utc(12, 0, 0);

        for (int index = 0; index < 13; index++)
        {
            tracker.ObserveClosedBar(
                Bar(BarTimeframe.M1, start.AddMinutes(index), 100m, 100.5m, 99.5m, 100m));
        }

        tracker.ObserveClosedBar(
            Bar(BarTimeframe.M1, start.AddMinutes(13), 99.8m, 100m, 99.5m, 99.9m));
        tracker.ObserveClosedBar(
            Bar(BarTimeframe.M1, start.AddMinutes(14), 100.4m, 100.8m, 100.2m, 100.6m));

        IReadOnlyList<StructureFeatureMetric> beforeThird = tracker.Snapshot(
            start.AddMinutes(15),
            100.6m,
            null);
        Assert.False(Metric(beforeThird, FeatureNames.BullFvgDistanceAtr).IsAvailable);

        tracker.ObserveClosedBar(
            Bar(BarTimeframe.M1, start.AddMinutes(15), 101.2m, 101.5m, 101m, 101.3m));

        tracker.UpdateTick(Tick(1, start.AddMinutes(16), 100.4m));

        IReadOnlyList<StructureFeatureMetric> afterThird = tracker.Snapshot(
            start.AddMinutes(16),
            100.5m,
            null);

        Assert.True(Metric(afterThird, FeatureNames.BullFvgDistanceAtr).IsAvailable);
        Assert.True(Value(afterThird, FeatureNames.FvgSizeAtr) > 0);
        Assert.True(Value(afterThird, FeatureNames.FvgFillPct) > 0);
    }

    [Fact]
    public void HtfIndicators_RequireClosedBarWarmupAndCanAlignUp()
    {
        var tracker = new CausalStructureRegimeTracker();
        DateTimeOffset start = Utc(8, 0, 0);

        IReadOnlyList<StructureFeatureMetric> cold = tracker.Snapshot(start, 100m, null);
        Assert.False(Metric(cold, FeatureNames.EmaSlopeH1).IsAvailable);

        for (int index = 0; index < 40; index++)
        {
            decimal basePrice = 100m + index * 0.5m;

            tracker.ObserveClosedBar(
                Bar(BarTimeframe.M5, start.AddMinutes(index * 5), basePrice, basePrice + 1m, basePrice - 1m, basePrice + 0.4m));
            tracker.ObserveClosedBar(
                Bar(BarTimeframe.M15, start.AddMinutes(index * 15), basePrice, basePrice + 1m, basePrice - 1m, basePrice + 0.4m));
            tracker.ObserveClosedBar(
                Bar(BarTimeframe.H1, start.AddHours(index), basePrice, basePrice + 1m, basePrice - 1m, basePrice + 0.4m));
        }

        IReadOnlyList<StructureFeatureMetric> warm = tracker.Snapshot(
            start.AddHours(40),
            120m,
            null);

        Assert.True(Metric(warm, FeatureNames.AdxM5).IsAvailable);
        Assert.True(Metric(warm, FeatureNames.AtrRatioM5).IsAvailable);
        Assert.Equal(1, Value(warm, FeatureNames.M5DirectionState));
        Assert.Equal(1, Value(warm, FeatureNames.M15DirectionState));
        Assert.Equal(1, Value(warm, FeatureNames.H1DirectionState));
        Assert.Equal(1, Value(warm, FeatureNames.HtfAlignedUp));
        Assert.Equal(0, Value(warm, FeatureNames.HtfConflict));
    }

    [Fact]
    public void SqueezeReleaseTimestamp_IsOnlyCreatedAfterClosedBarRelease()
    {
        var tracker = new CausalStructureRegimeTracker();
        DateTimeOffset start = Utc(12, 0, 0);

        for (int index = 0; index < 14; index++)
        {
            tracker.ObserveClosedBar(
                Bar(BarTimeframe.M1, start.AddMinutes(index), 100m, 102m, 98m, 100m));
        }

        for (int index = 14; index < 28; index++)
        {
            tracker.ObserveClosedBar(
                Bar(BarTimeframe.M1, start.AddMinutes(index), 100m, 100.5m, 99.5m, 100m));
        }

        IReadOnlyList<StructureFeatureMetric> squeezed = tracker.Snapshot(
            start.AddMinutes(28),
            100m,
            null);
        Assert.Equal(1, Value(squeezed, FeatureNames.M1Squeeze));
        Assert.False(Metric(squeezed, FeatureNames.SecondsSinceM1SqueezeRelease).IsAvailable);

        for (int index = 28; index < 42; index++)
        {
            tracker.ObserveClosedBar(
                Bar(BarTimeframe.M1, start.AddMinutes(index), 100m, 103m, 97m, 100m));
        }

        IReadOnlyList<StructureFeatureMetric> released = tracker.Snapshot(
            start.AddMinutes(42),
            100m,
            null);
        Assert.Equal(0, Value(released, FeatureNames.M1Squeeze));
        Assert.True(Metric(released, FeatureNames.SecondsSinceM1SqueezeRelease).IsAvailable);
    }

    [Fact]
    public void FutureClosedBar_CannotMutatePreviouslyProducedPrefixSnapshot()
    {
        var live = new CausalStructureRegimeTracker();
        var prefixOnly = new CausalStructureRegimeTracker();
        DateTimeOffset start = Utc(12, 0, 0);

        for (int index = 0; index < 30; index++)
        {
            ClosedBarState bar = Bar(
                BarTimeframe.M5,
                start.AddMinutes(index * 5),
                100m + index * 0.1m,
                101m + index * 0.1m,
                99m + index * 0.1m,
                100.2m + index * 0.1m);

            live.ObserveClosedBar(bar);
            prefixOnly.ObserveClosedBar(bar);
        }

        IReadOnlyList<StructureFeatureMetric> captured = live.Snapshot(
            start.AddMinutes(150),
            103m,
            null);
        IReadOnlyList<StructureFeatureMetric> expected = prefixOnly.Snapshot(
            start.AddMinutes(150),
            103m,
            null);

        string before = JsonSerializer.Serialize(captured);
        Assert.Equal(JsonSerializer.Serialize(expected), before);

        live.ObserveClosedBar(
            Bar(BarTimeframe.M5, start.AddMinutes(150), 200m, 220m, 180m, 210m));

        Assert.Equal(before, JsonSerializer.Serialize(captured));
    }

    private static ClosedBarState Bar(
        BarTimeframe timeframe,
        DateTimeOffset open,
        decimal openPrice,
        decimal high,
        decimal low,
        decimal close)
    {
        return new ClosedBarState(
            timeframe,
            open,
            openPrice,
            high,
            low,
            close,
            10,
            open + Duration(timeframe));
    }

    private static TimeSpan Duration(BarTimeframe timeframe)
    {
        return timeframe switch
        {
            BarTimeframe.M1 => TimeSpan.FromMinutes(1),
            BarTimeframe.M5 => TimeSpan.FromMinutes(5),
            BarTimeframe.M15 => TimeSpan.FromMinutes(15),
            BarTimeframe.H1 => TimeSpan.FromHours(1),
            _ => throw new ArgumentOutOfRangeException(nameof(timeframe)),
        };
    }

    private static TickEvent Tick(long sequence, DateTimeOffset timestamp, decimal bid)
    {
        return new TickEvent(
            ContractVersions.MarketEventV1,
            timestamp,
            timestamp,
            sequence,
            "structure-test",
            "XAUUSD",
            "XAUUSD",
            bid,
            bid + 0.20m,
            null,
            1,
            TickFlags.Bid | TickFlags.Ask);
    }

    private static StructureFeatureMetric Metric(
        IReadOnlyList<StructureFeatureMetric> metrics,
        string name)
    {
        return Assert.Single(metrics, metric => metric.Name == name);
    }

    private static double Value(
        IReadOnlyList<StructureFeatureMetric> metrics,
        string name)
    {
        StructureFeatureMetric metric = Metric(metrics, name);
        Assert.True(metric.IsAvailable, metric.UnavailableReason);
        return Assert.IsType<double>(metric.Value);
    }

    private static DateTimeOffset Utc(int hour, int minute, int second)
    {
        return new DateTimeOffset(2026, 9, 19, hour, minute, second, TimeSpan.Zero);
    }
}
