using System.Text.Json;
using XauScalp.Domain;
using XauScalp.Execution;
using XauScalp.Persistence;
using XauScalp.Risk;

namespace XauScalp.DemoRunner.Tests;

public sealed class DemoRunnerTests
{
    private static readonly PositionOwnership Ownership =
        new(991188, "demo-test", "xauscalp");

    [Fact]
    public void Configuration_RejectsDirectSecretFields()
    {
        string directory = TempDirectory();
        string path = Path.Combine(directory, "config.json");

        try
        {
            string json = ValidConfigJson();
            json = json.Insert(
                1,
                "\"apiKey\":\"forbidden\",");

            File.WriteAllText(path, json);

            Assert.Throws<InvalidDataException>(
                () => DemoRunnerConfiguration.Load(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Configuration_BuildsExactlyOtherModelAsShadow()
    {
        string directory = TempDirectory();
        string path = Path.Combine(directory, "config.json");

        try
        {
            File.WriteAllText(path, ValidConfigJson());

            DemoRunnerConfiguration configuration =
                DemoRunnerConfiguration.Load(path);

            DecisionModelSettings settings =
                configuration.BuildDecisionSettings();

            Assert.Equal(
                DecisionModelType.XauNative,
                settings.PrimaryDecisionModel);
            Assert.True(settings.ShadowComparison.Enabled);
            Assert.Equal(
                DecisionModelType.Jev,
                settings.ShadowComparison.ShadowModel);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EvaluationTrigger_FirstReadyThenRespectsMinimumInterval()
    {
        var trigger = new DemoEvaluationTrigger(
            new DemoEvaluationConfiguration
            {
                MinimumIntervalMs = 500,
                MaximumIntervalMs = 10_000,
                DecelerationThreshold = 0.6,
                DirectionFlipMaxAgeMs = 100,
            });

        DateTimeOffset start = Utc(12, 0, 0);

        Assert.True(trigger.ShouldEvaluate(
            State(start, directionFlipAgeMs: null)));

        Assert.False(trigger.ShouldEvaluate(
            State(
                start.AddMilliseconds(100),
                directionFlipAgeMs: 50)));

        Assert.True(trigger.ShouldEvaluate(
            State(
                start.AddMilliseconds(600),
                directionFlipAgeMs: 600)));
    }

    [Fact]
    public async Task RiskSynchronizer_InitializesAndDeDuplicatesClosedTrade()
    {
        string directory = TempDirectory();
        string riskPath = Path.Combine(directory, "risk.jsonl");
        string observationsPath = Path.Combine(
            directory,
            "observations.jsonl");
        DateTimeOffset now = Utc(12, 0, 0);
        Guid intent = Guid.Parse(
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

        try
        {
            await using var ledger =
                new RiskLedgerJsonlStore(
                    riskPath,
                    Ownership);
            await using var observations =
                new DemoObservationJsonlStore(
                    observationsPath);

            var synchronizer = new DemoRiskLedgerSynchronizer(
                ledger,
                Ownership,
                observations,
                new FixedTimeProvider(now));

            Mt5DemoBrokerContextSnapshot initial =
                BrokerContext(now, closedTrades: []);

            await synchronizer.EnsureAndSyncAsync(
                initial,
                CancellationToken.None);

            Mt5DemoBrokerContextSnapshot withClose =
                BrokerContext(
                    now,
                    closedTrades:
                    [
                        new Mt5DemoClosedTradeWire(
                            intent,
                            RealizedPnlMoney: -100m,
                            CommissionCostMoney: 5m,
                            ClosedAtUnixMs: now
                                .AddMinutes(5)
                                .ToUnixTimeMilliseconds()),
                    ]);

            await synchronizer.EnsureAndSyncAsync(
                withClose,
                CancellationToken.None);
            await synchronizer.EnsureAndSyncAsync(
                withClose,
                CancellationToken.None);

            OwnedRiskLedgerSnapshot snapshot =
                ledger.GetSnapshot(
                    now.AddMinutes(10));

            Assert.True(snapshot.IsReady);
            Assert.Equal(-105m, snapshot.RealizedNetPnlMoney);
            Assert.Equal(1, snapshot.ClosedTrades);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RiskSynchronizer_FailsClosedWhenTodayAlreadyHasBrokerCloseButLedgerMissing()
    {
        string directory = TempDirectory();
        string riskPath = Path.Combine(directory, "risk.jsonl");
        string observationsPath = Path.Combine(
            directory,
            "observations.jsonl");
        DateTimeOffset now = Utc(12, 0, 0);

        try
        {
            await using var ledger =
                new RiskLedgerJsonlStore(
                    riskPath,
                    Ownership);
            await using var observations =
                new DemoObservationJsonlStore(
                    observationsPath);

            var synchronizer = new DemoRiskLedgerSynchronizer(
                ledger,
                Ownership,
                observations,
                new FixedTimeProvider(now));

            Mt5DemoBrokerContextSnapshot context =
                BrokerContext(
                    now,
                    closedTrades:
                    [
                        new Mt5DemoClosedTradeWire(
                            Guid.Parse(
                                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
                            RealizedPnlMoney: -10m,
                            CommissionCostMoney: 1m,
                            ClosedAtUnixMs: now
                                .AddMinutes(-5)
                                .ToUnixTimeMilliseconds()),
                    ]);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => synchronizer.EnsureAndSyncAsync(
                    context,
                    CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static XauMarketState State(
        DateTimeOffset timestamp,
        double? directionFlipAgeMs)
    {
        var features = new List<NumericFeatureValue>
        {
            Feature(
                "DecelerationRatio",
                0.1,
                timestamp),
            Feature(
                "SweepOccurred",
                0,
                timestamp),
            Feature(
                "MicroRetestOccurred",
                0,
                timestamp),
        };

        features.Add(
            directionFlipAgeMs is double age
                ? Feature(
                    "DirectionFlipAgeMs",
                    age,
                    timestamp)
                : new NumericFeatureValue(
                    "DirectionFlipAgeMs",
                    null,
                    "ms",
                    false,
                    null,
                    "none"));

        return new XauMarketState(
            ContractVersions.MarketStateV1,
            Guid.NewGuid(),
            timestamp,
            timestamp,
            1,
            "XAUUSD",
            "XAUUSD.G",
            2500m,
            2500.2m,
            2500.1m,
            ContractVersions.FeatureSchemaV1,
            "test",
            LiquiditySource.Estimated,
            new DataReadiness(
                true,
                true,
                true,
                true,
                []),
            features.ToArray());
    }

    private static NumericFeatureValue Feature(
        string name,
        double value,
        DateTimeOffset observedAtUtc)
    {
        return new NumericFeatureValue(
            name,
            value,
            "test",
            true,
            observedAtUtc,
            null);
    }

    private static Mt5DemoBrokerContextSnapshot BrokerContext(
        DateTimeOffset now,
        Mt5DemoClosedTradeWire[] closedTrades)
    {
        return new Mt5DemoBrokerContextSnapshot(
            "XAUUSD",
            "XAUUSD.G",
            new BrokerReconciliationSnapshot(
                [],
                [],
                new HashSet<Guid>()),
            new PortfolioState(
                ContractVersions.PortfolioStateV1,
                now,
                10_000m,
                10_000m,
                9_000m,
                0m,
                0,
                []),
            new Mt5DemoSymbolRiskWire(
                0.01m,
                0.01m,
                1m,
                0.01m,
                100m,
                0.01m,
                0.5m,
                500m),
            closedTrades);
    }

    private static string ValidConfigJson()
    {
        object value = new
        {
            codeCommit =
                "0123456789abcdef0123456789abcdef01234567",
            ciRunUrl =
                "https://github.com/HoangHung997/XauScalp/actions/runs/1",
            mt5CommonFilesPath = ".",
            dataDirectory = ".",
            canonicalSymbol = "XAUUSD",
            brokerSymbol = "XAUUSD.G",
            nativeArtifactPath = "artifact.json",
            nativeArtifactManifestPath = "manifest.json",
            jev = new
            {
                endpoint = "https://jev.example.test/evaluate",
                secretReference = "env:XAUSCALP_JEV_API_KEY",
                providerModelId = "jev",
                providerModelVersion = "v1",
            },
        };

        return JsonSerializer.Serialize(value);
    }

    private static DateTimeOffset Utc(
        int hour,
        int minute,
        int second)
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

    private static string TempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "xauscalp-demo-runner-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

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
