using XauScalp.Domain;

namespace XauScalp.Features.Tests;

public sealed class CausalLiquidityTrackerTests
{
    [Fact]
    public void CleanSweepReject_RecordsDepthCloseBackAndAbsorption()
    {
        var tracker = TrackerWithUpperAndLowerSwing();
        DateTimeOffset t = Utc(12, 10, 0);

        tracker.UpdateTick(Tick(1, t, 104.80m, 1), Inputs(1, velocity: 1));
        tracker.UpdateTick(Tick(2, t.AddMilliseconds(500), 105.40m, 4), Inputs(1, velocity: 1.2, deceleration: 0.2));
        tracker.UpdateTick(Tick(3, t.AddSeconds(1), 104.60m, 1), Inputs(1, velocity: -0.8, deceleration: 0.95, flipAgeMs: 100));

        IReadOnlyList<LiquidityFeatureMetric> snapshot = tracker.Snapshot(t.AddSeconds(1), 104.70m, 1);

        Assert.True(Value(snapshot, FeatureNames.BuySideSweepDepthAtr) >= 0.4);
        Assert.True(Value(snapshot, FeatureNames.CloseBackInsideAtr) > 0);
        Assert.Equal(1, Value(snapshot, FeatureNames.SweepOccurred));
        Assert.Equal(1, Value(snapshot, FeatureNames.SweepDirection));
        Assert.True(Value(snapshot, FeatureNames.AbsorptionUpScore) > 0.4);
        Assert.Equal(1, Value(snapshot, FeatureNames.DirectionFlipAfterTouch));
    }

    [Fact]
    public void BreakoutWithoutCloseBack_KeepsCloseBackAtZero()
    {
        var tracker = TrackerWithUpperAndLowerSwing();
        DateTimeOffset t = Utc(12, 10, 0);

        tracker.UpdateTick(Tick(1, t, 104.80m), Inputs(1, velocity: 1));
        tracker.UpdateTick(Tick(2, t.AddMilliseconds(500), 105.30m), Inputs(1, velocity: 1.2));
        tracker.UpdateTick(Tick(3, t.AddSeconds(1), 105.80m), Inputs(1, velocity: 1));

        IReadOnlyList<LiquidityFeatureMetric> snapshot = tracker.Snapshot(t.AddSeconds(1), 105.90m, 1);

        Assert.Equal(1, Value(snapshot, FeatureNames.SweepOccurred));
        Assert.True(Value(snapshot, FeatureNames.BuySideSweepDepthAtr) > 0);
        Assert.Equal(0, Value(snapshot, FeatureNames.CloseBackInsideAtr));
    }

    [Fact]
    public void EqualHighCluster_ProducesNumericStrength()
    {
        var tracker = new CausalLiquidityTracker();
        decimal[] highs = [100m, 102m, 105m, 102m, 100m, 102m, 105.05m, 102m, 100m];
        decimal[] lows = [96m, 97m, 98m, 97m, 96m, 97m, 98m, 97m, 96m];

        for (int index = 0; index < highs.Length; index++)
        {
            tracker.ObserveClosedM1(Bar(index, highs[index], lows[index]));
        }

        IReadOnlyList<LiquidityFeatureMetric> snapshot = tracker.Snapshot(Utc(12, 20, 0), 100m, 1);

        Assert.True(Value(snapshot, FeatureNames.EqualHighStrength) >= 0.5);
    }

    [Fact]
    public void WideTwoSidedLiquidityGap_IsMarkedAsVacuum()
    {
        var tracker = TrackerWithUpperAndLowerSwing();

        IReadOnlyList<LiquidityFeatureMetric> snapshot = tracker.Snapshot(Utc(12, 10, 0), 100m, 2);

        Assert.True(Value(snapshot, FeatureNames.VacuumUpWidthAtr) >= 2);
        Assert.True(Value(snapshot, FeatureNames.VacuumDownWidthAtr) >= 2);
        Assert.Equal(1, Value(snapshot, FeatureNames.InsideVacuum));
    }

    [Fact]
    public void SweepRejectMicroRetest_RequiresOrderedAwayRetestResumePath()
    {
        var tracker = TrackerWithUpperAndLowerSwing();
        DateTimeOffset t = Utc(12, 10, 0);

        // Tick helper uses a 0.20 spread, so midpoint = bid + 0.10.
        tracker.UpdateTick(Tick(1, t, 104.70m), Inputs(1, velocity: 1));
        tracker.UpdateTick(Tick(2, t.AddMilliseconds(500), 105.20m), Inputs(1, velocity: 1.2));
        tracker.UpdateTick(Tick(3, t.AddSeconds(1), 104.70m), Inputs(1, velocity: -0.8, deceleration: 0.9));
        tracker.UpdateTick(Tick(4, t.AddSeconds(2), 104.50m), Inputs(1, velocity: -0.6, deceleration: 0.9));
        tracker.UpdateTick(Tick(5, t.AddSeconds(3), 104.87m), Inputs(1, velocity: 0.4, deceleration: 0.8));
        tracker.UpdateTick(Tick(6, t.AddSeconds(4), 104.60m), Inputs(1, velocity: -0.7, deceleration: 0.8));

        IReadOnlyList<LiquidityFeatureMetric> snapshot = tracker.Snapshot(t.AddSeconds(4), 104.80m, 1);

        Assert.Equal(1, Value(snapshot, FeatureNames.MicroRetestOccurred));
        Assert.True(Value(snapshot, FeatureNames.MicroRetestDepthAtr) <= 0.05);
        Assert.True(Value(snapshot, FeatureNames.ResumeVelocity) > 0);
    }

    [Fact]
    public void PreviousDayAndWeekLevels_AppearOnlyAfterCausalRollover()
    {
        var tracker = new CausalLiquidityTracker();
        DateTimeOffset friday = new(2026, 9, 18, 23, 59, 0, TimeSpan.Zero);
        DateTimeOffset monday = new(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);

        tracker.UpdateTick(Tick(1, friday, 100m), Inputs(2));
        tracker.UpdateTick(Tick(2, friday.AddSeconds(30), 104m), Inputs(2));

        IReadOnlyList<LiquidityFeatureMetric> before = tracker.Snapshot(friday.AddSeconds(30), 104.1m, 2);
        Assert.False(Metric(before, FeatureNames.PdhDistanceAtr).IsAvailable);
        Assert.False(Metric(before, FeatureNames.PwhDistanceAtr).IsAvailable);

        tracker.UpdateTick(Tick(3, monday, 102m), Inputs(2));
        IReadOnlyList<LiquidityFeatureMetric> after = tracker.Snapshot(monday, 102.1m, 2);

        Assert.True(Metric(after, FeatureNames.PdhDistanceAtr).IsAvailable);
        Assert.True(Metric(after, FeatureNames.PwhDistanceAtr).IsAvailable);
    }

    private static CausalLiquidityTracker TrackerWithUpperAndLowerSwing()
    {
        var tracker = new CausalLiquidityTracker();
        decimal[] highs = [101m, 103m, 105m, 103m, 101m];
        decimal[] lows = [99m, 97m, 95m, 97m, 99m];

        for (int index = 0; index < highs.Length; index++)
        {
            tracker.ObserveClosedM1(Bar(index, highs[index], lows[index]));
        }

        return tracker;
    }

    private static ClosedBarState Bar(int index, decimal high, decimal low)
    {
        DateTimeOffset open = Utc(12, 0, 0).AddMinutes(index);
        decimal openPrice = (high + low) / 2m;
        return new ClosedBarState(
            BarTimeframe.M1,
            open,
            openPrice,
            high,
            low,
            openPrice,
            10,
            open.AddMinutes(1));
    }

    private static TickEvent Tick(long sequence, DateTimeOffset timestamp, decimal bid, double volume = 1)
    {
        return new TickEvent(
            ContractVersions.MarketEventV1,
            timestamp,
            timestamp,
            sequence,
            "liquidity-test",
            "XAUUSD",
            "XAUUSD",
            bid,
            bid + 0.20m,
            null,
            volume,
            TickFlags.Bid | TickFlags.Ask | TickFlags.Volume);
    }

    private static LiquidityTickInputs Inputs(
        double atr,
        double? velocity = null,
        double? deceleration = null,
        double? flipAgeMs = null)
    {
        return new LiquidityTickInputs(
            atr,
            velocity,
            velocity is > 0 ? velocity : null,
            velocity is < 0 ? -velocity : null,
            deceleration,
            flipAgeMs,
            WickBodyRatio: 1);
    }

    private static LiquidityFeatureMetric Metric(
        IReadOnlyList<LiquidityFeatureMetric> snapshot,
        string name)
    {
        return Assert.Single(snapshot, feature => feature.Name == name);
    }

    private static double Value(
        IReadOnlyList<LiquidityFeatureMetric> snapshot,
        string name)
    {
        LiquidityFeatureMetric metric = Metric(snapshot, name);
        Assert.True(metric.IsAvailable, metric.UnavailableReason);
        return Assert.IsType<double>(metric.Value);
    }

    private static DateTimeOffset Utc(int hour, int minute, int second)
    {
        return new DateTimeOffset(2026, 9, 19, hour, minute, second, TimeSpan.Zero);
    }
}
