using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using XauScalp.Domain;
using XauScalp.Features;
using XauScalp.MarketData;

namespace XauScalp.Replay.Tests;

public sealed class ReplayEngineTests
{
    [Fact]
    public async Task RepeatingSameRun_ProducesSameDatasetAndOutputHashes()
    {
        MarketEvent[] events = BuildDataset();
        ReplayRunOptions options = CreateOptions();
        var firstSink = new InMemoryReplayRecordSink();
        var secondSink = new InMemoryReplayRecordSink();

        ReplayRunResult first = await CreateRunner()
            .RunAsync(
                AsAsync(events),
                options,
                firstSink,
                new ScenarioContextProvider());

        ReplayRunResult second = await CreateRunner()
            .RunAsync(
                AsAsync(events),
                options,
                secondSink,
                new ScenarioContextProvider());

        Assert.Equal(first.Manifest.DataSetSha256, second.Manifest.DataSetSha256);
        Assert.Equal(first.OutputSha256, second.OutputSha256);
        Assert.Equal(first.Manifest.RunId, second.Manifest.RunId);
        Assert.Equal(first.RecordsWritten, second.RecordsWritten);
        Assert.Equal(firstSink.Records.Count, secondSink.Records.Count);

        for (int index = 0; index < firstSink.Records.Count; index++)
        {
            Assert.Equal(
                Serialize(firstSink.Records[index]),
                Serialize(secondSink.Records[index]));
        }
    }

    [Fact]
    public async Task FutureSameSessionExtremes_CannotAlterPrefixFeatureState()
    {
        DateTimeOffset start = Utc(12, 0, 0);
        MarketEvent[] prefix =
        [
            SymbolSpec(start),
            Connected(start),
            Tick(1, start.AddSeconds(1), 100m),
        ];

        MarketEvent[] futureHigh = [.. prefix, Tick(2, start.AddSeconds(30), 150m)];
        MarketEvent[] futureLow = [.. prefix, Tick(2, start.AddSeconds(30), 50m)];

        var highSink = new InMemoryReplayRecordSink();
        var lowSink = new InMemoryReplayRecordSink();

        _ = await CreateRunner().RunAsync(
            AsAsync(futureHigh),
            CreateOptions(),
            highSink,
            new ScenarioContextProvider());

        _ = await CreateRunner().RunAsync(
            AsAsync(futureLow),
            CreateOptions(),
            lowSink,
            new ScenarioContextProvider());

        XauMarketState highPrefix = FirstFeatureState(highSink);
        XauMarketState lowPrefix = FirstFeatureState(lowSink);

        Assert.Equal(Serialize(highPrefix), Serialize(lowPrefix));
        Assert.Equal(100, FeatureValue(highPrefix, FeatureNames.M1LiveHigh), precision: 12);
        Assert.Equal(100, FeatureValue(highPrefix, FeatureNames.M1LiveLow), precision: 12);
    }

    [Fact]
    public async Task Manifest_RecordsRequiredReproducibilityMetadataAndCostScenario()
    {
        MarketEvent[] events = BuildDataset();
        ReplayRunOptions options = CreateOptions();
        var sink = new InMemoryReplayRecordSink();

        ReplayRunResult result = await CreateRunner()
            .RunAsync(
                AsAsync(events),
                options,
                sink,
                new ScenarioContextProvider());

        Assert.StartsWith("replay-", result.Manifest.RunId, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", result.Manifest.DataSetId, StringComparison.Ordinal);
        Assert.Equal(64, result.Manifest.DataSetSha256.Length);
        Assert.Equal(ContractVersions.FeatureSchemaV1, result.Manifest.FeatureSchemaVersion);
        Assert.Equal(XauFeatureEngine.EngineVersion, result.Manifest.FeatureEngineVersion);
        Assert.Equal("none", result.Manifest.Model.ModelId);
        Assert.Equal("settings-v1", result.Manifest.SettingsVersion);
        Assert.Equal("settings-hash-001", result.Manifest.SettingsHash);
        Assert.Equal("baseline", result.Manifest.CostScenario.Name);
        Assert.Equal(15, result.Manifest.CostScenario.EstimatedLatencyMs);
        Assert.Equal(4, result.Manifest.CostScenario.EstimatedSlippagePoints);
        Assert.Equal(7.5m, result.Manifest.CostScenario.CommissionPerLot);
        Assert.Equal("724cb29-test", result.Manifest.CodeCommit);
        Assert.Equal(events.LongLength, result.Manifest.EventCount);
        Assert.Equal(events.LongCount(static marketEvent => marketEvent is TickEvent), result.Manifest.TickCount);
        Assert.Equal(events[0].TimestampUtc, result.Manifest.StartTimestampUtc);
        Assert.Equal(events[^1].TimestampUtc, result.Manifest.EndTimestampUtc);
    }

    [Fact]
    public async Task CostScenario_IsInjectedCausallyThroughFeatureContextProvider()
    {
        MarketEvent[] events = BuildDataset();
        var sink = new InMemoryReplayRecordSink();

        _ = await CreateRunner()
            .RunAsync(
                AsAsync(events),
                CreateOptions(),
                sink,
                new ScenarioContextProvider());

        XauMarketState lastState = sink.Records
            .Where(static record => record.FeatureState is not null)
            .Select(static record => record.FeatureState!)
            .Last();

        Assert.Equal(
            15,
            FeatureValue(lastState, FeatureNames.EstimatedLatencyMs),
            precision: 12);
        Assert.Equal(
            4,
            FeatureValue(lastState, FeatureNames.EstimatedSlippagePoints),
            precision: 12);
    }

    [Fact]
    public async Task JsonlSink_ExportsEveryRecordedEventAndFeatureSnapshot()
    {
        MarketEvent[] events = BuildDataset();
        await using var stream = new MemoryStream();
        await using var sink = new JsonlReplayRecordSink(stream, leaveOpen: true);

        ReplayRunResult result = await CreateRunner()
            .RunAsync(
                AsAsync(events),
                CreateOptions(),
                sink,
                new ScenarioContextProvider());

        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        string exported = await reader.ReadToEndAsync();
        string[] lines = exported.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(result.RecordsWritten, lines.LongLength);

        ReplayOutputRecord? first = JsonSerializer.Deserialize<ReplayOutputRecord>(
            lines[0],
            XauJson.CreateOptions());

        Assert.NotNull(first);
        Assert.IsType<SymbolSpecificationEvent>(first!.MarketEvent);
        Assert.Null(first.FeatureState);
    }

    [Fact]
    public async Task TimestampRegression_FailsClosedWithoutReordering()
    {
        DateTimeOffset start = Utc(12, 0, 0);
        MarketEvent[] events =
        [
            Tick(1, start.AddSeconds(2), 100m),
            Tick(2, start.AddSeconds(1), 101m),
        ];

        var sink = new InMemoryReplayRecordSink();

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await CreateRunner().RunAsync(
                AsAsync(events),
                CreateOptions(),
                sink,
                new ScenarioContextProvider()));
    }

    [Fact]
    public async Task RealtimeAndAcceleratedTiming_UseRecordedTimeDeltaOnly()
    {
        DateTimeOffset start = Utc(12, 0, 0);
        MarketEvent[] events =
        [
            Tick(1, start, 100m),
            Tick(2, start.AddSeconds(2), 101m),
            Tick(3, start.AddSeconds(4), 102m),
        ];

        var realtimeDelay = new RecordingDelay();
        var realtimeScheduler = new ReplayEventScheduler(realtimeDelay);
        _ = await CollectAsync(
            realtimeScheduler.ScheduleAsync(
                AsAsync(events),
                ReplayTimingOptions.Realtime));

        Assert.Equal(
            [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)],
            realtimeDelay.Delays);

        var acceleratedDelay = new RecordingDelay();
        var acceleratedScheduler = new ReplayEventScheduler(acceleratedDelay);
        _ = await CollectAsync(
            acceleratedScheduler.ScheduleAsync(
                AsAsync(events),
                ReplayTimingOptions.Accelerated(4)));

        Assert.Equal(
            [TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500)],
            acceleratedDelay.Delays);
    }

    [Fact]
    public async Task StepTiming_RequiresGateAndWaitsOncePerRecordedEvent()
    {
        MarketEvent[] events =
        [
            Tick(1, Utc(12, 0, 0), 100m),
            Tick(2, Utc(12, 0, 1), 101m),
        ];

        var scheduler = new ReplayEventScheduler(new RecordingDelay());

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await CollectAsync(
                scheduler.ScheduleAsync(
                    AsAsync(events),
                    ReplayTimingOptions.Step)));

        var gate = new RecordingStepGate();
        IReadOnlyList<MarketEvent> replayed = await CollectAsync(
            scheduler.ScheduleAsync(
                AsAsync(events),
                ReplayTimingOptions.Step,
                gate));

        Assert.Equal(2, replayed.Count);
        Assert.Equal(2, gate.WaitCount);
    }

    [Fact]
    public async Task OutputRecord_HasNoFutureLabelDependency()
    {
        MarketEvent[] events = BuildDataset();
        var sink = new InMemoryReplayRecordSink();

        _ = await CreateRunner().RunAsync(
            AsAsync(events),
            CreateOptions(),
            sink,
            new ScenarioContextProvider());

        Assert.All(
            sink.Records,
            record =>
            {
                Assert.NotNull(record.MarketEvent);
                Assert.DoesNotContain(
                    record.GetType().GetProperties(),
                    property => property.Name.Contains("Label", StringComparison.OrdinalIgnoreCase));
            });
    }

    private static DeterministicReplayRunner CreateRunner()
    {
        return new DeterministicReplayRunner(
            CreateFeatureEngine,
            new ReplayEventScheduler(new RecordingDelay()));
    }

    private static IXauFeatureEngine CreateFeatureEngine()
    {
        var schedule = new MarketSessionSchedule(
        [
            new MarketSessionSegment(0, "A", TimeOnly.MinValue, new TimeOnly(12, 0)),
            new MarketSessionSegment(1, "B", new TimeOnly(12, 0), TimeOnly.MinValue),
        ]);

        return new XauFeatureEngine(
            new XauFeatureEngineOptions(
                BrokerClockConfiguration.UtcV1,
                schedule,
                externalContextMaxAge: TimeSpan.FromMinutes(1)));
    }

    private static ReplayRunOptions CreateOptions()
    {
        return new ReplayRunOptions(
            codeCommit: "724cb29-test",
            settingsVersion: "settings-v1",
            settingsHash: "settings-hash-001",
            ReplayModelIdentity.None,
            new ReplayCostScenario(
                "baseline",
                estimatedLatencyMs: 15,
                estimatedSlippagePoints: 4,
                commissionPerLot: 7.5m),
            ReplayTimingOptions.Accelerated(10_000),
            randomSeed: 123);
    }

    private static MarketEvent[] BuildDataset()
    {
        DateTimeOffset start = Utc(12, 0, 0);
        var events = new List<MarketEvent>
        {
            SymbolSpec(start),
            Connected(start),
        };

        for (int index = 0; index <= 40; index++)
        {
            events.Add(
                Tick(
                    index + 1,
                    start.AddMilliseconds(index * 500),
                    100m + (decimal)Math.Sin(index / 4.0) * 0.25m));
        }

        return events.ToArray();
    }

    private static SymbolSpecificationEvent SymbolSpec(DateTimeOffset timestampUtc)
    {
        return new SymbolSpecificationEvent(
            ContractVersions.MarketEventV1,
            timestampUtc,
            timestampUtc,
            sequenceId: 0,
            dataSourceId: "replay-test",
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
                minStopDistance: 0.50m));
    }

    private static ConnectionStatusEvent Connected(DateTimeOffset timestampUtc)
    {
        return new ConnectionStatusEvent(
            ContractVersions.MarketEventV1,
            timestampUtc,
            timestampUtc,
            sequenceId: 0,
            dataSourceId: "replay-test",
            symbol: "XAUUSD",
            brokerSymbol: "XAUUSD.G",
            MarketConnectionState.Connected,
            "dataset-start");
    }

    private static TickEvent Tick(long sequence, DateTimeOffset timestampUtc, decimal bid)
    {
        return new TickEvent(
            ContractVersions.MarketEventV1,
            timestampUtc,
            timestampUtc,
            sequence,
            "replay-test",
            "XAUUSD",
            "XAUUSD.G",
            bid,
            bid + 0.20m,
            null,
            1,
            TickFlags.Bid | TickFlags.Ask | TickFlags.Volume);
    }

    private static XauMarketState FirstFeatureState(InMemoryReplayRecordSink sink)
    {
        return sink.Records
            .Where(static record => record.FeatureState is not null)
            .Select(static record => record.FeatureState!)
            .First();
    }

    private static double FeatureValue(XauMarketState state, string name)
    {
        NumericFeatureValue feature = Assert.Single(
            state.Features,
            feature => string.Equals(feature.Name, name, StringComparison.Ordinal));

        Assert.True(
            feature.IsAvailable,
            $"{name} should be available but was {feature.UnavailableReason}");

        return Assert.IsType<double>(feature.Value);
    }

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, XauJson.CreateOptions());
    }

    private static async IAsyncEnumerable<MarketEvent> AsAsync(
        IEnumerable<MarketEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (MarketEvent marketEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return marketEvent;
            await Task.Yield();
        }
    }

    private static async Task<IReadOnlyList<MarketEvent>> CollectAsync(
        IAsyncEnumerable<MarketEvent> source)
    {
        var events = new List<MarketEvent>();
        await foreach (MarketEvent marketEvent in source.ConfigureAwait(false))
        {
            events.Add(marketEvent);
        }

        return events;
    }

    private static DateTimeOffset Utc(int hour, int minute, int second)
    {
        return new DateTimeOffset(2026, 9, 19, hour, minute, second, TimeSpan.Zero);
    }

    private sealed class RecordingDelay : IReplayDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingStepGate : IReplayStepGate
    {
        public int WaitCount { get; private set; }

        public ValueTask WaitForNextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScenarioContextProvider : IReplayFeatureContextProvider
    {
        public FeatureExternalContext? GetContext(
            MarketEvent marketEvent,
            ReplayCostScenario costScenario)
        {
            if (marketEvent is not TickEvent)
            {
                return null;
            }

            return new FeatureExternalContext(
                marketEvent.TimestampUtc,
                atrM1: 2,
                estimatedLatencyMs: costScenario.EstimatedLatencyMs,
                estimatedSlippagePoints: costScenario.EstimatedSlippagePoints,
                newsDistanceBeforeSec: 600,
                newsDistanceAfterSec: 120,
                isHighImpactNewsWindow: false);
        }
    }
}
