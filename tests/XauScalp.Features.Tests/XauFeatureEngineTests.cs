using System.Text.Json;
using XauScalp.Domain;
using XauScalp.MarketData;

namespace XauScalp.Features.Tests;

public sealed class XauFeatureEngineTests
{
    [Fact]
    public void StartupWindows_AreUnavailableRatherThanSilentlyZero()
    {
        DateTimeOffset start = Utc(12, 0, 0, 0);
        XauFeatureEngine engine = CreateReadyEngine(start);

        XauMarketState state = engine.Update(Tick(1, start, 100m));

        NumericFeatureValue return10s = Feature(state, FeatureNames.Return10s);
        NumericFeatureValue burst = Feature(state, FeatureNames.BurstZScore);

        Assert.False(return10s.IsAvailable);
        Assert.Null(return10s.Value);
        Assert.False(burst.IsAvailable);
        Assert.Null(burst.Value);
        Assert.False(state.Readiness.TickHistoryReady);
    }

    [Fact]
    public void ExecutionAndLiveM1Features_UseSymbolAndExternalContext()
    {
        DateTimeOffset start = Utc(12, 0, 0, 0);
        XauFeatureEngine engine = CreateReadyEngine(start);

        _ = engine.Update(Tick(1, start, 100.00m, spread: 0.20m));
        XauMarketState state = engine.Update(Tick(2, start.AddSeconds(30), 101.00m, spread: 0.20m));

        AssertFeature(state, FeatureNames.SpreadPrice, 0.20, 8);
        AssertFeature(state, FeatureNames.SpreadPoints, 20, 8);
        AssertFeature(state, FeatureNames.SpreadAtrRatio, 0.10, 8);
        AssertFeature(state, FeatureNames.TickSize, 0.01, 8);
        AssertFeature(state, FeatureNames.TickValue, 1.25, 8);
        AssertFeature(state, FeatureNames.MinStopDistance, 0.50, 8);
        AssertFeature(state, FeatureNames.EstimatedLatencyMs, 12, 8);
        AssertFeature(state, FeatureNames.EstimatedSlippagePoints, 3, 8);
        AssertFeature(state, FeatureNames.NewsDistanceBeforeSec, 600, 8);
        AssertFeature(state, FeatureNames.NewsDistanceAfterSec, 120, 8);
        AssertFeature(state, FeatureNames.IsHighImpactNewsWindow, 0, 8);

        AssertFeature(state, FeatureNames.M1BarAgeMs, 30_000, 8);
        AssertFeature(state, FeatureNames.M1BarProgressPct, 50, 8);
        AssertFeature(state, FeatureNames.M1Open, 100, 8);
        AssertFeature(state, FeatureNames.M1LiveHigh, 101, 8);
        AssertFeature(state, FeatureNames.M1LiveLow, 100, 8);
        AssertFeature(state, FeatureNames.M1LivePrice, 101, 8);
        AssertFeature(state, FeatureNames.M1LiveRangePrice, 1, 8);
        AssertFeature(state, FeatureNames.M1LiveBodyPrice, 1, 8);
        AssertFeature(state, FeatureNames.M1LiveBodyRatio, 1, 8);
        AssertFeature(state, FeatureNames.M1UpperWickRatio, 0, 8);
        AssertFeature(state, FeatureNames.M1LowerWickRatio, 0, 8);
        AssertFeature(state, FeatureNames.M1DistanceFromOpen, 1, 8);
        AssertFeature(state, FeatureNames.M1DistanceFromHigh, 0, 8);
        AssertFeature(state, FeatureNames.M1DistanceFromLow, 1, 8);
        AssertFeature(state, FeatureNames.M1RangeAtrRatio, 0.5, 8);

        Assert.Equal(ContractVersions.FeatureSchemaV1, state.FeatureSchemaVersion);
        Assert.Equal(XauFeatureEngine.EngineVersion, engine.FeatureEngineVersion);
    }

    [Fact]
    public void Acceleration_UsesOnlyTwoCausalHalfWindows()
    {
        DateTimeOffset start = Utc(12, 0, 0, 0);
        XauFeatureEngine engine = CreateReadyEngine(start);

        _ = engine.Update(Tick(1, start, 100m));
        _ = engine.Update(Tick(2, start.AddMilliseconds(500), 101m));
        XauMarketState state = engine.Update(Tick(3, start.AddSeconds(1), 103m));

        AssertFeature(state, FeatureNames.Velocity500ms, 4, 8);
        AssertFeature(state, FeatureNames.Acceleration1s, 4, 8);
    }

    [Fact]
    public void PeakVelocityAndDeceleration_CapturePeakThenSlowdown()
    {
        DateTimeOffset start = Utc(12, 0, 0, 0);
        XauFeatureEngine engine = CreateReadyEngine(start);

        decimal[] bids =
        [
            100m,
            100.10m,
            100.20m,
            100.30m,
            102.30m,
            102.40m,
            102.45m,
            102.50m,
            102.55m,
            102.60m,
            102.65m,
            102.70m,
            102.75m,
        ];

        XauMarketState? state = null;
        for (int index = 0; index < bids.Length; index++)
        {
            state = engine.Update(Tick(index + 1, start.AddMilliseconds(index * 500), bids[index]));
        }

        Assert.NotNull(state);
        double peakUp = AvailableValue(state!, FeatureNames.PeakVelocityUp);
        double deceleration = AvailableValue(state!, FeatureNames.DecelerationRatio);
        double currentVelocity = AvailableValue(state!, FeatureNames.Velocity500ms);

        Assert.True(peakUp >= 4);
        Assert.True(currentVelocity < 0.2);
        Assert.True(deceleration > 0.90);
    }

    [Fact]
    public void DirectionFlipAge_StartsWhen500msVelocityChangesSign()
    {
        DateTimeOffset start = Utc(12, 0, 0, 0);
        XauFeatureEngine engine = CreateReadyEngine(start);

        _ = engine.Update(Tick(1, start, 100m));
        _ = engine.Update(Tick(2, start.AddMilliseconds(500), 101m));
        _ = engine.Update(Tick(3, start.AddMilliseconds(1000), 102m));
        _ = engine.Update(Tick(4, start.AddMilliseconds(1500), 101m));
        XauMarketState state = engine.Update(Tick(5, start.AddMilliseconds(2000), 100.5m));

        AssertFeature(state, FeatureNames.DirectionFlipAgeMs, 500, 8);
        Assert.True(AvailableValue(state, FeatureNames.Velocity500ms) < 0);
    }

    [Fact]
    public void IrregularTickSpacing_ProducesFiniteIntervalStatisticsAndRates()
    {
        DateTimeOffset start = Utc(12, 0, 0, 0);
        XauFeatureEngine engine = CreateReadyEngine(start);
        int[] offsetsMs = [0, 100, 350, 900, 1500, 2400, 3600, 5000, 6000];

        XauMarketState? state = null;
        for (int index = 0; index < offsetsMs.Length; index++)
        {
            state = engine.Update(
                Tick(index + 1, start.AddMilliseconds(offsetsMs[index]), 100m + index * 0.1m));
        }

        Assert.NotNull(state);
        double mean = AvailableValue(state!, FeatureNames.MeanTickIntervalMs);
        double std = AvailableValue(state!, FeatureNames.TickIntervalStdMs);
        double rate1s = AvailableValue(state!, FeatureNames.TickRate1s);

        Assert.True(mean > 0);
        Assert.True(std > 0);
        Assert.True(double.IsFinite(mean));
        Assert.True(double.IsFinite(std));
        Assert.Equal(1, rate1s, precision: 8);
    }

    [Fact]
    public void FutureTick_DoesNotMutatePreviouslyProducedPrefixSnapshot()
    {
        DateTimeOffset start = Utc(12, 0, 0, 0);
        XauFeatureEngine live = CreateReadyEngine(start);
        XauFeatureEngine prefixOnly = CreateReadyEngine(start);

        TickEvent[] prefix =
        [
            Tick(1, start, 100m),
            Tick(2, start.AddSeconds(1), 101m),
            Tick(3, start.AddSeconds(2), 100.5m),
            Tick(4, start.AddSeconds(3), 102m),
            Tick(5, start.AddSeconds(5), 101.5m),
        ];

        XauMarketState? captured = null;
        XauMarketState? expected = null;

        foreach (TickEvent tick in prefix)
        {
            captured = live.Update(tick);
            expected = prefixOnly.Update(tick);
        }

        Assert.NotNull(captured);
        Assert.NotNull(expected);

        string beforeFuture = SerializeState(captured!);
        Assert.Equal(SerializeState(expected!), beforeFuture);

        _ = live.Update(Tick(6, start.AddSeconds(6), 150m));

        Assert.Equal(beforeFuture, SerializeState(captured!));
    }

    [Fact]
    public void SerializedReplayPrefix_ProducesEquivalentFeatureSnapshotsAndStateIds()
    {
        DateTimeOffset start = Utc(12, 0, 0, 0);
        XauFeatureEngine live = CreateReadyEngine(start);
        XauFeatureEngine replay = CreateReadyEngine(start);
        JsonSerializerOptions jsonOptions = XauJson.CreateOptions();

        TickEvent[] ticks = Enumerable.Range(0, 41)
            .Select(
                index => Tick(
                    index + 1,
                    start.AddMilliseconds(index * 400),
                    100m + (decimal)Math.Sin(index / 3.0) * 0.25m))
            .ToArray();

        foreach (TickEvent tick in ticks)
        {
            XauMarketState liveState = live.Update(tick);

            string raw = JsonSerializer.Serialize<MarketEvent>(tick, jsonOptions);
            MarketEvent replayEvent = JsonSerializer.Deserialize<MarketEvent>(raw, jsonOptions)
                ?? throw new InvalidOperationException("Serialized replay event unexpectedly deserialized to null.");
            XauMarketState replayState = replay.Update(replayEvent);

            AssertEquivalentState(liveState, replayState);
        }
    }

    [Fact]
    public void BurstZScore_UsesOnlyPreviousCompletedUtcSeconds()
    {
        DateTimeOffset start = Utc(12, 0, 0, 0);
        XauFeatureEngine engine = CreateReadyEngine(start);
        long sequence = 1;

        for (int second = 0; second < 16; second++)
        {
            int ticksThisSecond = second % 2 == 0 ? 1 : 2;
            for (int tickIndex = 0; tickIndex < ticksThisSecond; tickIndex++)
            {
                _ = engine.Update(
                    Tick(
                        sequence++,
                        start.AddSeconds(second).AddMilliseconds(100 + tickIndex * 300),
                        100m + second * 0.01m + tickIndex * 0.001m));
            }
        }

        XauMarketState? burstState = null;
        for (int index = 0; index < 5; index++)
        {
            burstState = engine.Update(
                Tick(
                    sequence++,
                    start.AddSeconds(16).AddMilliseconds(100 + index * 100),
                    101m + index * 0.001m));
        }

        Assert.NotNull(burstState);
        double zScore = AvailableValue(burstState!, FeatureNames.BurstZScore);
        Assert.True(zScore > 1);
    }

    [Fact]
    public void FullWarmupWithAllExternalSources_SetsP0Readiness()
    {
        DateTimeOffset start = Utc(12, 0, 0, 0);
        XauFeatureEngine engine = CreateReadyEngine(start);

        XauMarketState? state = null;
        for (int index = 0; index <= 40; index++)
        {
            state = engine.Update(Tick(index + 1, start.AddMilliseconds(index * 500), 100m + index * 0.01m));
        }

        Assert.NotNull(state);
        Assert.True(state!.Readiness.TickHistoryReady);
        Assert.True(state.Readiness.BarHistoryReady);
        Assert.True(state.Readiness.NewsDataAvailable);
        Assert.True(state.Readiness.RequiredP0Ready);
        Assert.Empty(state.Readiness.MissingRequirements);
    }

    private static XauFeatureEngine CreateReadyEngine(DateTimeOffset observedAtUtc)
    {
        var schedule = new MarketSessionSchedule(
        [
            new MarketSessionSegment(0, "SessionA", TimeOnly.MinValue, new TimeOnly(12, 0)),
            new MarketSessionSegment(1, "SessionB", new TimeOnly(12, 0), TimeOnly.MinValue),
        ]);

        var engine = new XauFeatureEngine(
            new XauFeatureEngineOptions(
                BrokerClockConfiguration.UtcV1,
                schedule,
                externalContextMaxAge: TimeSpan.FromMinutes(5)));

        engine.ObserveContext(
            new SymbolSpecificationEvent(
                ContractVersions.MarketEventV1,
                observedAtUtc,
                null,
                sequenceId: 0,
                dataSourceId: "mt5-test",
                symbol: "XAUUSD",
                brokerSymbol: "XAUUSD.G",
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

        engine.ObserveContext(
            new ConnectionStatusEvent(
                ContractVersions.MarketEventV1,
                observedAtUtc,
                null,
                sequenceId: 0,
                dataSourceId: "mt5-test",
                symbol: "XAUUSD",
                brokerSymbol: "XAUUSD.G",
                MarketConnectionState.Connected,
                "test"));

        engine.SetExternalContext(
            new FeatureExternalContext(
                observedAtUtc,
                atrM1: 2,
                estimatedLatencyMs: 12,
                estimatedSlippagePoints: 3,
                newsDistanceBeforeSec: 600,
                newsDistanceAfterSec: 120,
                isHighImpactNewsWindow: false));

        return engine;
    }

    private static TickEvent Tick(
        long sequence,
        DateTimeOffset timestampUtc,
        decimal bid,
        decimal spread = 0.20m)
    {
        return new TickEvent(
            ContractVersions.MarketEventV1,
            timestampUtc,
            timestampUtc,
            sequence,
            "mt5-test",
            "XAUUSD",
            "XAUUSD.G",
            bid,
            bid + spread,
            null,
            1,
            TickFlags.Bid | TickFlags.Ask | TickFlags.Volume);
    }

    private static NumericFeatureValue Feature(XauMarketState state, string name)
    {
        return Assert.Single(state.Features, feature => string.Equals(feature.Name, name, StringComparison.Ordinal));
    }

    private static double AvailableValue(XauMarketState state, string name)
    {
        NumericFeatureValue feature = Feature(state, name);
        Assert.True(feature.IsAvailable, $"{name} should be available but was: {feature.UnavailableReason}");
        return Assert.IsType<double>(feature.Value);
    }

    private static void AssertFeature(XauMarketState state, string name, double expected, int precision)
    {
        Assert.Equal(expected, AvailableValue(state, name), precision);
    }

    private static string SerializeState(XauMarketState state)
    {
        return JsonSerializer.Serialize(state, XauJson.CreateOptions());
    }

    private static void AssertEquivalentState(XauMarketState expected, XauMarketState actual)
    {
        Assert.Equal(expected.MarketStateId, actual.MarketStateId);
        Assert.Equal(expected.TimestampUtc, actual.TimestampUtc);
        Assert.Equal(expected.SequenceId, actual.SequenceId);
        Assert.Equal(expected.FeatureSchemaVersion, actual.FeatureSchemaVersion);
        Assert.Equal(expected.Features.Length, actual.Features.Length);

        foreach (NumericFeatureValue expectedFeature in expected.Features)
        {
            NumericFeatureValue actualFeature = Assert.Single(
                actual.Features,
                feature => string.Equals(feature.Name, expectedFeature.Name, StringComparison.Ordinal));

            Assert.Equal(expectedFeature.IsAvailable, actualFeature.IsAvailable);
            Assert.Equal(expectedFeature.Unit, actualFeature.Unit);
            Assert.Equal(expectedFeature.UnavailableReason, actualFeature.UnavailableReason);
            Assert.Equal(expectedFeature.ObservedAtUtc, actualFeature.ObservedAtUtc);

            if (expectedFeature.Value is double expectedValue)
            {
                double actualValue = Assert.IsType<double>(actualFeature.Value);
                Assert.Equal(expectedValue, actualValue, precision: 12);
            }
            else
            {
                Assert.Null(actualFeature.Value);
            }
        }
    }

    private static DateTimeOffset Utc(int hour, int minute, int second, int millisecond)
    {
        return new DateTimeOffset(2026, 9, 19, hour, minute, second, millisecond, TimeSpan.Zero);
    }
}
