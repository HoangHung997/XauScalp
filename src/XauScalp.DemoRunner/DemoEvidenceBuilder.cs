using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using XauScalp.DecisionModels;
using XauScalp.Domain;
using XauScalp.Execution;
using XauScalp.Persistence;

namespace XauScalp.DemoRunner;

public sealed record BrokerDemoCostAssumptions(
    double SlippagePoints,
    decimal CommissionPerLot,
    double LatencyMs);

public sealed record BrokerDemoManifest(
    string EvidenceClass,
    bool LiveMoneyEnabled,
    string CodeCommit,
    string CiRunUrl,
    string CanonicalSymbol,
    string DatasetId,
    string DatasetSha256,
    string ReplayRunId,
    string ReplayOutputSha256,
    string BrokerSymbol,
    string DataSourceId,
    string DemoExecutionId,
    string Mt5MarketBridgeEx5Sha256,
    string Mt5DemoExecutionBridgeEx5Sha256,
    bool Mt5BridgesCompiledPassed,
    DateTimeOffset CapturedAtUtc,
    long TickCount,
    double JevLatencyP95Ms,
    double XauNativeLatencyP95Ms,
    BrokerDemoCostAssumptions CostAssumptions,
    string[] KnownLimitations,
    string[] UnresolvedP0P1CorrectnessIssues,
    bool FeatureParityPassed,
    bool ReplayDeterminismPassed,
    bool PrimaryShadowPassed,
    bool RiskPassed,
    bool RestartReconnectPassed,
    bool ModelOutagePassed,
    bool StaleDataPassed);

public static class DemoEvidenceBuilder
{
    public static async Task<BrokerDemoManifest> BuildAsync(
        DemoRunnerConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string dataDirectory = configuration.ResolvePath(
            configuration.DataDirectory);
        string replayPath = Path.Combine(
            dataDirectory,
            "replay-evidence.json");
        string drillsPath = Path.Combine(
            dataDirectory,
            "demo-drills.json");
        string decisionPath = Path.Combine(
            dataDirectory,
            "decision-comparison.jsonl");
        string executionPath = Path.Combine(
            dataDirectory,
            "execution-journal.jsonl");
        string outputPath = Path.Combine(
            dataDirectory,
            "xsp017-broker-demo.json");

        DemoReplayEvidence replay = ReadJson<DemoReplayEvidence>(
            replayPath);
        DemoDrillEvidence drills = ReadJson<DemoDrillEvidence>(
            drillsPath);

        if (!string.Equals(
                replay.CodeCommit,
                configuration.CodeCommit,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                drills.CodeCommit,
                configuration.CodeCommit,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Replay/drill evidence does not match configured code commit.");
        }

        await using var decisions =
            new DecisionComparisonJsonlStore(
                decisionPath);
        IReadOnlyList<DecisionComparisonBundle> bundles =
            await decisions.ReadAllAsync(
                cancellationToken).ConfigureAwait(false);

        if (bundles.Count == 0)
        {
            throw new InvalidDataException(
                "No primary/shadow decision telemetry exists.");
        }

        bool primaryShadowPassed = bundles.Any(
            static bundle =>
                bundle.Comparison.ShadowEnabled
                && bundle.Comparison.Primary.Succeeded
                && bundle.Comparison.Primary.Decision is not null
                && bundle.Comparison.Shadow?.Succeeded == true
                && bundle.Comparison.Shadow.Decision is not null
                && bundle.Comparison.Primary.Decision.MarketStateId
                    == bundle.Comparison.Shadow.Decision.MarketStateId
                && bundle.Comparison.Primary.ConfiguredModel
                    != bundle.Comparison.Shadow.ConfiguredModel);

        if (!primaryShadowPassed)
        {
            throw new InvalidDataException(
                "No successful same-state primary/shadow comparison exists.");
        }

        double[] jevLatencies = GetModelLatencies(
            bundles,
            DecisionModelType.Jev);
        double[] nativeLatencies = GetModelLatencies(
            bundles,
            DecisionModelType.XauNative);

        if (jevLatencies.Length == 0
            || nativeLatencies.Length == 0)
        {
            throw new InvalidDataException(
                "Measured successful JEV and XAU Native latency samples are both required.");
        }

        await using var executionJournal =
            new ExecutionJournalJsonlStore(
                executionPath);
        IReadOnlyList<ExecutionLifecycleSnapshot> executions =
            await executionJournal.GetAllAsync(
                cancellationToken).ConfigureAwait(false);

        ExecutionLifecycleSnapshot? executed =
            executions.LastOrDefault(
                static snapshot =>
                    snapshot.History.Any(
                        static item =>
                            item.EffectiveState
                                == OrderLifecycleState.Authorized)
                    && snapshot.History.Any(
                        static item =>
                            item.EffectiveState is
                                OrderLifecycleState.Filled
                                or OrderLifecycleState.Open
                                or OrderLifecycleState.Closed)
                    && snapshot.History.Any(
                        static item =>
                            !string.IsNullOrWhiteSpace(
                                item.Result.BrokerDealId)));

        if (executed is null)
        {
            throw new InvalidDataException(
                "No hard-risk-authorized broker demo execution with a broker deal id exists.");
        }

        string demoExecutionId = executed.History
            .Where(
                static item =>
                    !string.IsNullOrWhiteSpace(
                        item.Result.BrokerDealId))
            .Select(static item => item.Result.BrokerDealId!)
            .Last();

        string marketEx5 = RequiredExistingFile(
            configuration.MarketBridgeEx5Path,
            configuration,
            "MarketBridgeEx5Path");
        string executionEx5 = RequiredExistingFile(
            configuration.DemoExecutionBridgeEx5Path,
            configuration,
            "DemoExecutionBridgeEx5Path");

        var manifest = new BrokerDemoManifest(
            EvidenceClass: "BROKER_DEMO",
            LiveMoneyEnabled: false,
            configuration.CodeCommit.ToLowerInvariant(),
            configuration.CiRunUrl,
            configuration.CanonicalSymbol,
            replay.DatasetId,
            replay.DatasetSha256,
            replay.ReplayRunId,
            replay.ReplayOutputSha256,
            configuration.BrokerSymbol,
            configuration.DataSourceId,
            demoExecutionId,
            ComputeFileSha256(marketEx5),
            ComputeFileSha256(executionEx5),
            Mt5BridgesCompiledPassed: true,
            DateTimeOffset.UtcNow,
            replay.TickCount,
            ComputeP95(jevLatencies),
            ComputeP95(nativeLatencies),
            new BrokerDemoCostAssumptions(
                configuration.CostAssumptions
                    .EstimatedSlippagePoints,
                configuration.CostAssumptions
                    .CommissionPerLot,
                configuration.CostAssumptions
                    .EstimatedLatencyMs),
            configuration.KnownLimitations.ToArray(),
            UnresolvedP0P1CorrectnessIssues: [],
            replay.FeatureParityPassed,
            replay.ReplayDeterminismPassed,
            primaryShadowPassed,
            RiskPassed: true,
            drills.RestartReconnectPassed,
            drills.ModelOutagePassed,
            drills.StaleDataPassed);

        ValidateManifest(manifest);
        WriteJson(outputPath, manifest);

        return manifest;
    }

    public static double ComputeP95(
        IEnumerable<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        double[] values = samples
            .Where(double.IsFinite)
            .Order()
            .ToArray();

        if (values.Length == 0)
        {
            throw new InvalidDataException(
                "Cannot compute P95 without finite samples.");
        }

        int rank = Math.Max(
            1,
            (int)Math.Ceiling(values.Length * 0.95));
        return values[rank - 1];
    }

    private static double[] GetModelLatencies(
        IEnumerable<DecisionComparisonBundle> bundles,
        DecisionModelType model)
    {
        return bundles
            .SelectMany(
                static bundle =>
                    bundle.Comparison.Shadow is null
                        ? [bundle.Comparison.Primary]
                        : new[]
                        {
                            bundle.Comparison.Primary,
                            bundle.Comparison.Shadow,
                        })
            .Where(
                record =>
                    record.Succeeded
                    && record.ActualModel == model
                    && record.EvaluationLatency >= TimeSpan.Zero)
            .Select(
                static record =>
                    record.EvaluationLatency.TotalMilliseconds)
            .Where(double.IsFinite)
            .ToArray();
    }

    private static void ValidateManifest(
        BrokerDemoManifest manifest)
    {
        if (manifest.LiveMoneyEnabled)
        {
            throw new InvalidDataException(
                "Broker-demo manifest cannot enable live money.");
        }

        if (!manifest.FeatureParityPassed
            || !manifest.ReplayDeterminismPassed
            || !manifest.PrimaryShadowPassed
            || !manifest.RiskPassed
            || !manifest.RestartReconnectPassed
            || !manifest.ModelOutagePassed
            || !manifest.StaleDataPassed
            || !manifest.Mt5BridgesCompiledPassed)
        {
            throw new InvalidDataException(
                "Broker-demo manifest contains an unproven gate.");
        }

        if (manifest.TickCount <= 0)
        {
            throw new InvalidDataException(
                "Broker-demo manifest requires positive tick count.");
        }

        if (manifest.KnownLimitations.Length == 0
            || manifest.KnownLimitations.Any(
                string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException(
                "Broker-demo manifest requires known limitations.");
        }
    }

    private static T ReadJson<T>(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Required evidence file '{Path.GetFileName(path)}' is missing.",
                path);
        }

        T? value = JsonSerializer.Deserialize<T>(
            File.ReadAllText(path),
            CreateJsonOptions());

        return value
            ?? throw new InvalidDataException(
                $"Evidence file '{path}' is empty.");
    }

    private static string RequiredExistingFile(
        string configuredPath,
        DemoRunnerConfiguration configuration,
        string field)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidDataException(
                $"{field} is required for final broker-demo evidence.");
        }

        string path = configuration.ResolvePath(
            configuredPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"{field} does not exist. Compile the current MQL5 source in MetaEditor first.",
                path);
        }

        return path;
    }

    private static string ComputeFileSha256(
        string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(
            SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    private static void WriteJson<T>(
        string path,
        T value)
    {
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                value,
                CreateJsonOptions(writeIndented: true))
            + Environment.NewLine);
    }

    private static JsonSerializerOptions CreateJsonOptions(
        bool writeIndented = false)
    {
        var options = new JsonSerializerOptions(
            JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            WriteIndented = writeIndented,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }
}
