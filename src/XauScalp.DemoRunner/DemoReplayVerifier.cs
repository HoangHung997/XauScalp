using System.Security.Cryptography;
using System.Text.Json;
using XauScalp.DecisionModels;
using XauScalp.Features;
using XauScalp.Persistence;
using XauScalp.Replay;

namespace XauScalp.DemoRunner;

public sealed record DemoReplayEvidence(
    string EvidenceVersion,
    string CodeCommit,
    string DatasetId,
    string DatasetSha256,
    string ReplayRunId,
    string ReplayOutputSha256,
    long EventCount,
    long TickCount,
    long LiveFeatureStatesMatched,
    bool FeatureParityPassed,
    bool ReplayDeterminismPassed,
    DemoCostConfiguration CostAssumptions);

public static class DemoReplayVerifier
{
    public static async Task<DemoReplayEvidence> RunAsync(
        DemoRunnerConfiguration configuration,
        string configurationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            configurationPath);

        string dataDirectory = configuration.ResolvePath(
            configuration.DataDirectory);
        string rawDatasetPath = Path.Combine(
            dataDirectory,
            "raw-market-events.jsonl");
        string liveFeatureSnapshotPath = Path.Combine(
            dataDirectory,
            "live-feature-snapshots.jsonl");
        string outputOnePath = Path.Combine(
            dataDirectory,
            "replay-live-parity.jsonl");
        string outputTwoPath = Path.Combine(
            dataDirectory,
            "replay-determinism-2.jsonl");
        string evidencePath = Path.Combine(
            dataDirectory,
            "replay-evidence.json");

        if (!File.Exists(rawDatasetPath))
        {
            throw new FileNotFoundException(
                "Raw broker-demo dataset does not exist.",
                rawDatasetPath);
        }

        if (!File.Exists(liveFeatureSnapshotPath))
        {
            throw new FileNotFoundException(
                "Live feature parity evidence does not exist.",
                liveFeatureSnapshotPath);
        }

        LoadedNativeArtifact native =
            NativeArtifactLoader.Load(
                configuration.ResolvePath(
                    configuration.NativeArtifactPath),
                configuration.ResolvePath(
                    configuration.NativeArtifactManifestPath));

        var modelIdentity = new ReplayModelIdentity(
            native.Artifact.ModelId,
            native.Artifact.ModelVersion,
            native.ArtifactSha256);

        var costScenario = new ReplayCostScenario(
            "broker-demo-configured-costs",
            configuration.CostAssumptions.EstimatedLatencyMs,
            configuration.CostAssumptions.EstimatedSlippagePoints,
            configuration.CostAssumptions.CommissionPerLot);

        string settingsHash = ComputeSha256(
            File.ReadAllBytes(
                Path.GetFullPath(configurationPath)));

        var options = new ReplayRunOptions(
            configuration.CodeCommit,
            configuration.Trade.SettingsVersion,
            settingsHash,
            modelIdentity,
            costScenario,
            ReplayTimingOptions.Realtime,
            randomSeed: 0);

        ReplayRunResult first;
        long matched;

        await using (var rawStore =
            new AppendOnlyJsonlMarketEventStore(
                rawDatasetPath))
        await using (var liveSnapshots =
            new LiveFeatureSnapshotJsonlStore(
                liveFeatureSnapshotPath))
        await using (var outputStream = new FileStream(
            outputOnePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous
                | FileOptions.SequentialScan))
        {
            var innerSink = new JsonlReplayRecordSink(
                outputStream,
                leaveOpen: true);

            await using var paritySink =
                new ReplayLiveParitySink(
                    innerSink,
                    liveSnapshots.ReadAllAsync(
                        cancellationToken),
                    cancellationToken);

            var runner = CreateRunner(configuration);
            var context = new CausalAtrReplayContextProvider(
                configuration.BuildBrokerClock());

            first = await runner.RunAsync(
                rawStore.ReadAllAsync(
                    cancellationToken),
                options,
                paritySink,
                context,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            matched = paritySink.MatchedTickStates;
        }

        ReplayRunResult second;

        await using (var rawStore =
            new AppendOnlyJsonlMarketEventStore(
                rawDatasetPath))
        await using (var outputStream = new FileStream(
            outputTwoPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous
                | FileOptions.SequentialScan))
        await using (var sink = new JsonlReplayRecordSink(
            outputStream,
            leaveOpen: true))
        {
            var runner = CreateRunner(configuration);
            var context = new CausalAtrReplayContextProvider(
                configuration.BuildBrokerClock());

            second = await runner.RunAsync(
                rawStore.ReadAllAsync(
                    cancellationToken),
                options,
                sink,
                context,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        bool deterministic =
            string.Equals(
                first.Manifest.DataSetSha256,
                second.Manifest.DataSetSha256,
                StringComparison.Ordinal)
            && string.Equals(
                first.OutputSha256,
                second.OutputSha256,
                StringComparison.Ordinal)
            && string.Equals(
                first.Manifest.RunId,
                second.Manifest.RunId,
                StringComparison.Ordinal)
            && first.Manifest.EventCount
                == second.Manifest.EventCount
            && first.Manifest.TickCount
                == second.Manifest.TickCount;

        if (!deterministic)
        {
            throw new InvalidDataException(
                "Repeated replay did not produce identical dataset/output/run identity.");
        }

        if (matched != first.Manifest.TickCount)
        {
            throw new InvalidDataException(
                $"Live/replay parity matched {matched} states but dataset contains {first.Manifest.TickCount} ticks.");
        }

        var evidence = new DemoReplayEvidence(
            "xsp017-demo-replay-evidence-v1",
            configuration.CodeCommit,
            first.Manifest.DataSetId,
            first.Manifest.DataSetSha256,
            first.Manifest.RunId,
            first.OutputSha256,
            first.Manifest.EventCount,
            first.Manifest.TickCount,
            matched,
            FeatureParityPassed: true,
            ReplayDeterminismPassed: true,
            configuration.CostAssumptions);

        var jsonOptions = new JsonSerializerOptions(
            JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

        File.WriteAllText(
            evidencePath,
            JsonSerializer.Serialize(
                evidence,
                jsonOptions)
            + Environment.NewLine);

        return evidence;
    }

    private static DeterministicReplayRunner CreateRunner(
        DemoRunnerConfiguration configuration)
    {
        return new DeterministicReplayRunner(
            () => new XauFeatureEngine(
                new XauFeatureEngineOptions(
                    configuration.BuildBrokerClock(),
                    configuration.BuildSessionSchedule(),
                    TimeSpan.FromSeconds(
                        configuration.ExternalContextMaxAgeSec),
                    configuration.Risk.HighImpactNewsBlockBeforeSec,
                    configuration.Risk.HighImpactNewsBlockAfterSec,
                    TimeSpan.FromSeconds(
                        configuration.MaxBrokerTickAgeSec))),
            new ReplayEventScheduler(
                NoReplayDelay.Instance));
    }

    private static string ComputeSha256(
        byte[] bytes)
    {
        return Convert.ToHexString(
            SHA256.HashData(bytes))
            .ToLowerInvariant();
    }

    private sealed class NoReplayDelay : IReplayDelay
    {
        public static NoReplayDelay Instance { get; } =
            new();

        private NoReplayDelay()
        {
        }

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
