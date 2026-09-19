using System.Runtime.CompilerServices;
using System.Text.Json;
using XauScalp.Domain;
using XauScalp.MarketData;
using XauScalp.Persistence;

namespace XauScalp.IntegrationTests;

public sealed class Mt5GatewayAndRecorderTests
{
    private static readonly DateTimeOffset ReceivedAtUtc =
        new(2026, 9, 19, 16, 30, 0, TimeSpan.Zero);

    private static readonly Mt5GatewayOptions GatewayOptions =
        new("mt5-demo", new BrokerSymbolMapping("XAUUSD", "XAUUSD.G"));

    [Fact]
    public async Task DuplicatePriceTicks_AreNotDeduplicated()
    {
        long brokerTime = DateTimeOffset.Parse("2026-09-19T16:30:00Z").ToUnixTimeMilliseconds();
        Mt5WireMessage[] messages =
        [
            Tick(1, brokerTime, 3680.10m, 3680.30m),
            Tick(2, brokerTime, 3680.10m, 3680.30m),
        ];

        List<MarketEvent> events = await ReadGatewayAsync(messages);
        TickEvent[] ticks = events.OfType<TickEvent>().ToArray();

        Assert.Equal(2, ticks.Length);
        Assert.Equal([1L, 2L], ticks.Select(static tick => tick.SequenceId).ToArray());
        Assert.All(ticks, static tick => Assert.Equal(3680.10m, tick.Bid));
    }

    [Fact]
    public async Task SameMillisecondTicks_PreserveSourceOrder()
    {
        long brokerTime = DateTimeOffset.Parse("2026-09-19T16:30:00.123Z").ToUnixTimeMilliseconds();
        Mt5WireMessage[] messages =
        [
            Tick(10, brokerTime, 3680.10m, 3680.20m),
            Tick(11, brokerTime, 3680.20m, 3680.30m),
            Tick(12, brokerTime, 3680.15m, 3680.25m),
        ];

        List<MarketEvent> events = await ReadGatewayAsync(messages);
        TickEvent[] ticks = events.OfType<TickEvent>().ToArray();

        Assert.Equal([10L, 11L, 12L], ticks.Select(static tick => tick.SequenceId).ToArray());
        Assert.All(
            ticks,
            tick => Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(brokerTime), tick.BrokerTimestamp));
    }

    [Fact]
    public async Task OutOfOrderArrival_IsNotReorderedOrInvented_AndEmitsAnomalyMarkers()
    {
        long brokerTime = DateTimeOffset.Parse("2026-09-19T16:30:00Z").ToUnixTimeMilliseconds();
        Mt5WireMessage[] messages =
        [
            Tick(20, brokerTime, 3680.10m, 3680.20m),
            Tick(22, brokerTime + 1, 3680.20m, 3680.30m),
            Tick(21, brokerTime + 2, 3680.15m, 3680.25m),
        ];

        List<MarketEvent> events = await ReadGatewayAsync(messages);

        Assert.Equal(5, events.Count);
        Assert.Equal(20, Assert.IsType<TickEvent>(events[0]).SequenceId);

        FeedGapEvent missing = Assert.IsType<FeedGapEvent>(events[1]);
        Assert.Equal(FeedSequenceAnomalyKind.MissingRange, missing.Kind);
        Assert.Equal(21, missing.ExpectedSequenceId);
        Assert.Equal(22, missing.ObservedSequenceId);

        Assert.Equal(22, Assert.IsType<TickEvent>(events[2]).SequenceId);

        FeedGapEvent late = Assert.IsType<FeedGapEvent>(events[3]);
        Assert.Equal(FeedSequenceAnomalyKind.DuplicateOrOutOfOrder, late.Kind);
        Assert.Equal(23, late.ExpectedSequenceId);
        Assert.Equal(21, late.ObservedSequenceId);

        Assert.Equal(21, Assert.IsType<TickEvent>(events[4]).SequenceId);
    }

    [Fact]
    public async Task DisconnectAndReconnect_ArePreservedAsMarketEvents()
    {
        Mt5WireMessage[] messages =
        [
            new Mt5WireConnection(1, "XAUUSD.G", Mt5WireConnectionState.Connected, "init"),
            new Mt5WireConnection(2, "XAUUSD.G", Mt5WireConnectionState.Disconnected, "terminal disconnected"),
            new Mt5WireConnection(3, "XAUUSD.G", Mt5WireConnectionState.Connected, "reconnect"),
        ];

        List<MarketEvent> events = await ReadGatewayAsync(messages);
        ConnectionStatusEvent[] connections = events.OfType<ConnectionStatusEvent>().ToArray();

        Assert.Equal(3, connections.Length);
        Assert.Equal(
            [
                MarketConnectionState.Connected,
                MarketConnectionState.Disconnected,
                MarketConnectionState.Connected,
            ],
            connections.Select(static connection => connection.State).ToArray());
        Assert.Equal("reconnect", connections[2].Reason);
    }

    [Fact]
    public async Task SymbolSuffixMapping_AndSpecificationMetadata_AreCaptured()
    {
        Mt5WireMessage[] messages =
        [
            new Mt5WireSymbolSpecification(
                1,
                "XAUUSD.G",
                Digits: 2,
                Point: 0.01m,
                TickSize: 0.01m,
                TickValue: 1m,
                ContractSize: 100m,
                MinVolume: 0.01m,
                MaxVolume: 100m,
                VolumeStep: 0.01m,
                MinStopDistance: 0.50m),
        ];

        List<MarketEvent> events = await ReadGatewayAsync(messages);
        SymbolSpecificationEvent specificationEvent = Assert.IsType<SymbolSpecificationEvent>(Assert.Single(events));

        Assert.Equal("XAUUSD", specificationEvent.Symbol);
        Assert.Equal("XAUUSD.G", specificationEvent.BrokerSymbol);
        Assert.Equal(2, specificationEvent.Specification.Digits);
        Assert.Equal(0.01m, specificationEvent.Specification.TickSize);
        Assert.Equal(1m, specificationEvent.Specification.TickValue);
        Assert.Equal(100m, specificationEvent.Specification.ContractSize);
        Assert.Equal(0.01m, specificationEvent.Specification.VolumeStep);
    }

    [Fact]
    public async Task RecorderRestart_AppendsWithoutRewritingHistory()
    {
        string directory = CreateTempDirectory();
        string datasetPath = Path.Combine(directory, "raw-events.jsonl");

        try
        {
            await using (var first = new AppendOnlyJsonlMarketEventStore(datasetPath))
            {
                await first.AppendAsync(CreateTickEvent(1, 3680.10m, 3680.20m));
                await first.AppendAsync(CreateTickEvent(2, 3680.20m, 3680.30m));
            }

            string beforeRestart = await File.ReadAllTextAsync(datasetPath);

            await using (var second = new AppendOnlyJsonlMarketEventStore(datasetPath))
            {
                await second.AppendAsync(CreateTickEvent(3, 3680.30m, 3680.40m));
            }

            string afterRestart = await File.ReadAllTextAsync(datasetPath);
            Assert.StartsWith(beforeRestart, afterRestart, StringComparison.Ordinal);

            await using var reader = new AppendOnlyJsonlMarketEventStore(datasetPath);
            List<MarketEvent> restored = await ReadStoreAsync(reader);
            Assert.Equal([1L, 2L, 3L], restored.Select(static item => item.SequenceId).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CaptureDataset_ReadsBackIdenticalOrderAndMetadata()
    {
        string directory = CreateTempDirectory();
        string datasetPath = Path.Combine(directory, "capture.jsonl");

        try
        {
            long brokerTime = DateTimeOffset.Parse("2026-09-19T16:31:00.456Z").ToUnixTimeMilliseconds();
            Mt5WireMessage[] messages =
            [
                new Mt5WireSymbolSpecification(
                    1,
                    "XAUUSD.G",
                    2,
                    0.01m,
                    0.01m,
                    1.25m,
                    100m,
                    0.01m,
                    50m,
                    0.01m,
                    0.20m),
                Tick(2, brokerTime, 3681.10m, 3681.30m, last: 3681.20m, volume: 12, flags: 2 | 4 | 8 | 16),
                new Mt5WireConnection(3, "XAUUSD.G", Mt5WireConnectionState.Disconnected, "test disconnect"),
            ];

            var source = new Mt5MarketDataSource(
                new EnumerableMt5Transport(messages),
                GatewayOptions,
                new FixedClock(ReceivedAtUtc));
            var recorder = new RawMarketEventRecorder();

            await using var store = new AppendOnlyJsonlMarketEventStore(datasetPath);
            long recorded = await recorder.RecordAsync(source, store);
            List<MarketEvent> restored = await ReadStoreAsync(store);

            Assert.Equal(3, recorded);
            Assert.Equal(3, restored.Count);

            JsonSerializerOptions options = XauJson.CreateOptions();
            List<MarketEvent> expected = await ReadGatewayAsync(messages);
            string[] expectedJson = expected
                .Select(item => JsonSerializer.Serialize<MarketEvent>(item, options))
                .ToArray();
            string[] restoredJson = restored
                .Select(item => JsonSerializer.Serialize<MarketEvent>(item, options))
                .ToArray();

            Assert.Equal(expectedJson, restoredJson);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BridgeSpoolResume_UsesLastRecordedSequenceWithoutDuplicates()
    {
        string directory = CreateTempDirectory();
        string bridgePath = Path.Combine(directory, "mt5-wire.ndjson");
        string datasetPath = Path.Combine(directory, "raw-events.jsonl");

        try
        {
            await File.WriteAllTextAsync(
                bridgePath,
                TickJson(1, 1_789_834_600_000, 3680.10m, 3680.20m)
                + Environment.NewLine
                + TickJson(2, 1_789_834_600_001, 3680.20m, 3680.30m)
                + Environment.NewLine);

            var recorder = new RawMarketEventRecorder();

            await using (var firstStore = new AppendOnlyJsonlMarketEventStore(datasetPath))
            {
                var firstSource = new Mt5MarketDataSource(
                    new Mt5NdjsonFileTransport(bridgePath, follow: false),
                    GatewayOptions,
                    new FixedClock(ReceivedAtUtc));
                Assert.Equal(2, await recorder.RecordAsync(firstSource, firstStore));
            }

            await File.AppendAllTextAsync(
                bridgePath,
                TickJson(3, 1_789_834_600_002, 3680.30m, 3680.40m) + Environment.NewLine);

            await using (var secondStore = new AppendOnlyJsonlMarketEventStore(datasetPath))
            {
                long? resumeSequence = await secondStore.GetLastSequenceIdAsync();
                Assert.Equal(2, resumeSequence);

                var resumedSource = new Mt5MarketDataSource(
                    new Mt5NdjsonFileTransport(
                        bridgePath,
                        follow: false,
                        startAfterSourceSequenceId: resumeSequence.Value),
                    GatewayOptions,
                    new FixedClock(ReceivedAtUtc.AddSeconds(1)));

                Assert.Equal(1, await recorder.RecordAsync(resumedSource, secondStore));
                List<MarketEvent> restored = await ReadStoreAsync(secondStore);
                Assert.Equal([1L, 2L, 3L], restored.Select(static item => item.SequenceId).ToArray());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HighRateSyntheticBurst_PreservesEverySequence()
    {
        const int count = 5_000;
        string directory = CreateTempDirectory();
        string datasetPath = Path.Combine(directory, "burst.jsonl");

        try
        {
            long brokerTime = DateTimeOffset.Parse("2026-09-19T16:32:00Z").ToUnixTimeMilliseconds();
            Mt5WireMessage[] messages = Enumerable.Range(1, count)
                .Select(
                    index => (Mt5WireMessage)Tick(
                        index,
                        brokerTime + index,
                        3680m + (index % 10) * 0.01m,
                        3680.20m + (index % 10) * 0.01m))
                .ToArray();

            var source = new Mt5MarketDataSource(
                new EnumerableMt5Transport(messages),
                GatewayOptions,
                new FixedClock(ReceivedAtUtc));
            var recorder = new RawMarketEventRecorder();

            await using var store = new AppendOnlyJsonlMarketEventStore(datasetPath, flushEveryRecords: 256);
            long recorded = await recorder.RecordAsync(source, store);
            List<MarketEvent> restored = await ReadStoreAsync(store);

            Assert.Equal(count, recorded);
            Assert.Equal(count, restored.Count);
            Assert.Equal(1, restored[0].SequenceId);
            Assert.Equal(count, restored[^1].SequenceId);
            Assert.Equal(
                Enumerable.Range(1, count).Select(static value => (long)value),
                restored.Select(static item => item.SequenceId));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Mt5WireTick Tick(
        long sequence,
        long brokerTime,
        decimal bid,
        decimal ask,
        decimal? last = null,
        double? volume = 0,
        int flags = 2 | 4)
    {
        return new Mt5WireTick(sequence, "XAUUSD.G", brokerTime, bid, ask, last, volume, flags);
    }

    private static TickEvent CreateTickEvent(long sequence, decimal bid, decimal ask)
    {
        return new TickEvent(
            ContractVersions.MarketEventV1,
            ReceivedAtUtc,
            ReceivedAtUtc,
            sequence,
            "mt5-demo",
            "XAUUSD",
            "XAUUSD.G",
            bid,
            ask,
            null,
            0,
            TickFlags.Bid | TickFlags.Ask);
    }

    private static string TickJson(long sequence, long brokerTimeMsc, decimal bid, decimal ask)
    {
        return $$"""
            {"type":"tick","sequence":{{sequence}},"brokerSymbol":"XAUUSD.G","brokerTimeMsc":{{brokerTimeMsc}},"bid":{{bid.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"ask":{{ask.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"last":0,"volume":0,"flags":6}
            """;
    }

    private static async Task<List<MarketEvent>> ReadGatewayAsync(IEnumerable<Mt5WireMessage> messages)
    {
        var source = new Mt5MarketDataSource(
            new EnumerableMt5Transport(messages),
            GatewayOptions,
            new FixedClock(ReceivedAtUtc));

        var result = new List<MarketEvent>();
        await foreach (MarketEvent marketEvent in source.ReadEventsAsync())
        {
            result.Add(marketEvent);
        }

        return result;
    }

    private static async Task<List<MarketEvent>> ReadStoreAsync(AppendOnlyJsonlMarketEventStore store)
    {
        var result = new List<MarketEvent>();
        await foreach (MarketEvent marketEvent in store.ReadAllAsync())
        {
            result.Add(marketEvent);
        }

        return result;
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "XauScalp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IUtcClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class EnumerableMt5Transport(IEnumerable<Mt5WireMessage> messages) : IMt5Transport
    {
        private readonly IReadOnlyList<Mt5WireMessage> _messages = messages.ToArray();

        public async IAsyncEnumerable<Mt5WireMessage> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            foreach (Mt5WireMessage message in _messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return message;
            }
        }
    }
}
