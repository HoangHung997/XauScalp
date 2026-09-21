using System.Text.Json;
using XauScalp.DecisionModels;
using XauScalp.Domain;
using XauScalp.Execution;
using XauScalp.Persistence;
using XauScalp.Risk;

namespace XauScalp.DemoRunner;

public sealed record DemoDrillEvidence(
    string EvidenceVersion,
    string CodeCommit,
    DateTimeOffset CapturedAtUtc,
    bool ModelOutagePassed,
    bool StaleDataPassed,
    bool RestartReconnectPassed,
    string ModelOutageDetail,
    string StaleDataDetail,
    string RestartReconnectDetail);

public static class DemoDrillRunner
{
    public static async Task<DemoDrillEvidence> RunAsync(
        DemoRunnerConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string dataDirectory = configuration.ResolvePath(
            configuration.DataDirectory);
        string lastReadyStatePath = Path.Combine(
            dataDirectory,
            "last-ready-state.json");
        string executionJournalPath = Path.Combine(
            dataDirectory,
            "execution-journal.jsonl");
        string riskLedgerPath = Path.Combine(
            dataDirectory,
            "risk-ledger.jsonl");
        string evidencePath = Path.Combine(
            dataDirectory,
            "demo-drills.json");

        if (!File.Exists(lastReadyStatePath))
        {
            throw new FileNotFoundException(
                "A real ready-state snapshot is required before drills can run.",
                lastReadyStatePath);
        }

        XauMarketState state = ReadState(
            lastReadyStatePath);

        PositionOwnership ownership =
            configuration.BuildOwnership();
        Mt5DemoExecutionGatewayOptions gatewayOptions =
            CreateGatewayOptions(
                configuration,
                ownership);
        var gateway = new Mt5DemoExecutionBrokerGateway(
            gatewayOptions);

        LoadedNativeArtifact nativeArtifact =
            NativeArtifactLoader.Load(
                configuration.ResolvePath(
                    configuration.NativeArtifactPath),
                configuration.ResolvePath(
                    configuration.NativeArtifactManifestPath));
        var nativeModel = new XauNativeDecisionModel(
            nativeArtifact);

        (bool outagePassed, string outageDetail) =
            await RunModelOutageDrillAsync(
                state,
                nativeModel,
                cancellationToken).ConfigureAwait(false);

        await using var riskLedger =
            new RiskLedgerJsonlStore(
                riskLedgerPath,
                ownership);

        (bool stalePassed, string staleDetail) =
            await RunStaleDataDrillAsync(
                configuration,
                state,
                gateway,
                riskLedger,
                ownership,
                cancellationToken).ConfigureAwait(false);

        await using var executionJournal =
            new ExecutionJournalJsonlStore(
                executionJournalPath);
        var execution = new ExecutionEngine(
            gateway,
            executionJournal,
            ownership);

        (bool restartPassed, string restartDetail) =
            await RunRestartReconnectDrillAsync(
                configuration,
                execution,
                executionJournal,
                cancellationToken).ConfigureAwait(false);

        if (!outagePassed
            || !stalePassed
            || !restartPassed)
        {
            throw new InvalidDataException(
                "One or more XSP-017 demo drills did not pass.");
        }

        var evidence = new DemoDrillEvidence(
            "xsp017-demo-drills-v1",
            configuration.CodeCommit,
            DateTimeOffset.UtcNow,
            outagePassed,
            stalePassed,
            restartPassed,
            outageDetail,
            staleDetail,
            restartDetail);

        WriteEvidence(evidencePath, evidence);
        return evidence;
    }

    private static async Task<(bool Passed, string Detail)>
        RunModelOutageDrillAsync(
            XauMarketState state,
            IXauDecisionModel nativeModel,
            CancellationToken cancellationToken)
    {
        var store = new InMemoryDecisionComparisonStore();
        var sink = new CountingAuthoritativeSink();
        var orchestrator =
            new PrimaryShadowDecisionOrchestrator(
                new AlwaysFailJevModel(),
                nativeModel,
                store,
                sink);

        var settings = new DecisionModelSettings(
            DecisionModelType.Jev,
            JevFailurePolicy.StopNewTrades,
            new ShadowComparisonSettings(
                enabled: true,
                DecisionModelType.XauNative));

        PrimaryShadowEvaluationResult result =
            await orchestrator.EvaluateAsync(
                state,
                settings,
                cancellationToken).ConfigureAwait(false);

        bool passed = result.StopNewTrades
            && result.AuthoritativeDecision is null
            && sink.AcceptCount == 0;

        return (
            passed,
            passed
                ? "Injected JEV outage produced StopNewTrades and zero authoritative trade-sink calls."
                : "Injected JEV outage did not fail closed.");
    }

    private static async Task<(bool Passed, string Detail)>
        RunStaleDataDrillAsync(
            DemoRunnerConfiguration configuration,
            XauMarketState state,
            IMt5DemoBrokerContextProvider gateway,
            IOwnedRiskLedger riskLedger,
            PositionOwnership ownership,
            CancellationToken cancellationToken)
    {
        Mt5DemoBrokerContextSnapshot context =
            await gateway.QueryDemoContextAsync(
                cancellationToken).ConfigureAwait(false);

        var profile = new RiskSymbolProfile(
            state.Symbol,
            state.BrokerSymbol,
            context.SymbolRisk.Point,
            context.SymbolRisk.TickSize,
            context.SymbolRisk.TickValue,
            context.SymbolRisk.MinVolume,
            context.SymbolRisk.MaxVolume,
            context.SymbolRisk.VolumeStep,
            context.SymbolRisk.MinStopDistance,
            context.SymbolRisk.EstimatedMarginPerLotMoney);

        DateTimeOffset staleEvaluationTime =
            state.TimestampUtc
                .AddMilliseconds(
                    configuration.Risk.MaxFeatureAgeMs + 1L);

        var decision = new XauDecision(
            ContractVersions.DecisionV1,
            Guid.NewGuid(),
            state.MarketStateId,
            DecisionModelType.XauNative,
            TradeAction.Long,
            actionProbability: 0.5,
            pUp5First: 0.5,
            pDown5First: 0.5,
            pUp10First: 0.5,
            pDown10First: 0.5,
            pAdverseBarrierFirst: 0.5,
            pContinuation: 0.5,
            pReversal: 0.5,
            pFalseBreak: 0.5,
            confidence: 0.5,
            pHold: null,
            pExitNow: null,
            pTp5FromHere: null,
            pTp10FromHere: null,
            modelId: "xau-native-stale-drill",
            modelVersion: "drill-v1",
            state.FeatureSchemaVersion,
            staleEvaluationTime,
            TimeSpan.Zero);

        var riskEngine = new HardRiskEngine(
            new FixedRiskRuntimeContextProvider(profile),
            riskLedger,
            new RiskPolicyConfiguration(
                configuration.Trade.RiskPolicyVersion,
                configuration.Trade.ProtectiveStopDistancePrice,
                ownership),
            new FixedTimeProvider(
                staleEvaluationTime));

        RiskDecision risk = riskEngine.Evaluate(
            state,
            decision,
            context.Portfolio,
            configuration.BuildRiskSettings());

        bool passed =
            risk.Outcome == RiskDecisionOutcome.Rejected
            && string.Equals(
                risk.ReasonCode,
                "state-stale",
                StringComparison.Ordinal);

        return (
            passed,
            passed
                ? "Real ready-state snapshot was advanced beyond MaxFeatureAgeMs and Hard Risk rejected state-stale before execution."
                : $"Expected state-stale rejection, got {risk.ReasonCode}.");
    }

    private static async Task<(bool Passed, string Detail)>
        RunRestartReconnectDrillAsync(
            DemoRunnerConfiguration configuration,
            ExecutionEngine execution,
            IExecutionJournal journal,
            CancellationToken cancellationToken)
    {
        IReadOnlyList<ExecutionLifecycleSnapshot> locals =
            await journal.GetAllAsync(
                cancellationToken).ConfigureAwait(false);

        ExecutionLifecycleSnapshot? active =
            locals.LastOrDefault(
                static snapshot =>
                    snapshot.EffectiveState is
                        OrderLifecycleState.Accepted
                        or OrderLifecycleState.PartiallyFilled
                        or OrderLifecycleState.Filled
                        or OrderLifecycleState.Open
                        or OrderLifecycleState.ModifyPending
                        or OrderLifecycleState.ClosePending
                        or OrderLifecycleState.UnknownNeedsReconciliation);

        if (active is null)
        {
            return (
                false,
                "Restart/reconnect drill requires at least one locally active/uncertain demo trade.");
        }

        string commandPath = Path.Combine(
            configuration.ResolvePath(
                configuration.Mt5CommonFilesPath),
            "XauScalp",
            "mt5-demo-execution-commands-v1.ndjson");

        int submitsBefore = CountSubmitCommands(
            commandPath,
            active.Plan.TradeIntentId);

        ExecutionReconciliationReport report =
            await execution.ReconcileAsync(
                cancellationToken).ConfigureAwait(false);

        int submitsAfter = CountSubmitCommands(
            commandPath,
            active.Plan.TradeIntentId);

        bool passed =
            report.IsSafeForNewEntry
            && report.MatchedLocalTradeIntents > 0
            && submitsAfter == submitsBefore;

        return (
            passed,
            passed
                ? $"Reconciled active trade {active.Plan.TradeIntentId:D} with no additional submit command."
                : $"Restart reconciliation unsafe or submit count changed ({submitsBefore} -> {submitsAfter}).");
    }

    private static int CountSubmitCommands(
        string path,
        Guid tradeIntentId)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        int count = 0;

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument document =
                JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty(
                    "operation",
                    out JsonElement operation)
                || !string.Equals(
                    operation.GetString(),
                    Mt5DemoExecutionProtocol.SubmitOperation,
                    StringComparison.Ordinal)
                || !root.TryGetProperty(
                    "tradeIntentId",
                    out JsonElement intent)
                || !Guid.TryParse(
                    intent.GetString(),
                    out Guid parsed)
                || parsed != tradeIntentId)
            {
                continue;
            }

            count++;
        }

        return count;
    }

    private static Mt5DemoExecutionGatewayOptions
        CreateGatewayOptions(
            DemoRunnerConfiguration configuration,
            PositionOwnership ownership)
    {
        string commonRoot = configuration.ResolvePath(
            configuration.Mt5CommonFilesPath);

        return new Mt5DemoExecutionGatewayOptions(
            Path.Combine(
                commonRoot,
                "XauScalp",
                "mt5-demo-execution-commands-v1.ndjson"),
            Path.Combine(
                commonRoot,
                "XauScalp",
                "mt5-demo-execution-events-v1.ndjson"),
            configuration.CanonicalSymbol,
            configuration.BrokerSymbol,
            ownership,
            configuration.Risk.MaxSlippagePoints);
    }

    private static XauMarketState ReadState(
        string path)
    {
        XauMarketState? state =
            JsonSerializer.Deserialize<XauMarketState>(
                File.ReadAllText(path),
                XauJson.CreateOptions());

        return state
            ?? throw new InvalidDataException(
                "Last ready-state snapshot is empty.");
    }

    private static void WriteEvidence(
        string path,
        DemoDrillEvidence evidence)
    {
        var options = new JsonSerializerOptions(
            JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                evidence,
                options)
            + Environment.NewLine);
    }

    private sealed class AlwaysFailJevModel
        : IXauDecisionModel
    {
        public string ModelId => "jev";

        public string ModelVersion => "outage-drill";

        public Task<XauDecision> EvaluateAsync(
            XauMarketState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new JevProviderUnavailableException(
                "Injected model-outage drill.");
        }
    }

    private sealed class CountingAuthoritativeSink
        : IAuthoritativeTradeDecisionSink
    {
        public int AcceptCount { get; private set; }

        public ValueTask AcceptAsync(
            AuthoritativeTradeDecisionEnvelope envelope,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcceptCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(
            DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow()
            => _now;
    }
}
