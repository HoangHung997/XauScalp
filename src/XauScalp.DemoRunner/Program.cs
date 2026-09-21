using System.Net.Http;
using System.Text.Json;
using XauScalp.DecisionModels;
using XauScalp.Domain;
using XauScalp.Execution;
using XauScalp.Features;
using XauScalp.MarketData;
using XauScalp.Persistence;
using XauScalp.Risk;
using XauScalp.Replay;
using XauScalp.Runtime;

namespace XauScalp.DemoRunner;

internal static class Program
{
    private const string MarketWireRelativePath =
        "XauScalp/mt5-wire-v1.ndjson";
    private const string ExecutionCommandsRelativePath =
        "XauScalp/mt5-demo-execution-commands-v1.ndjson";
    private const string ExecutionEventsRelativePath =
        "XauScalp/mt5-demo-execution-events-v1.ndjson";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2)
        {
            PrintUsage();
            return 64;
        }

        string command = args[0];
        string configurationPath = args[1];

        if (!string.Equals(
                command,
                "run",
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                command,
                "replay",
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                command,
                "drill",
                StringComparison.OrdinalIgnoreCase))
        {
            PrintUsage();
            return 64;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            DemoRunnerConfiguration configuration =
                DemoRunnerConfiguration.Load(
                    configurationPath);

            if (string.Equals(
                    command,
                    "replay",
                    StringComparison.OrdinalIgnoreCase))
            {
                DemoReplayEvidence evidence =
                    await DemoReplayVerifier.RunAsync(
                        configuration,
                        configurationPath,
                        cancellation.Token)
                    .ConfigureAwait(false);

                Console.WriteLine(
                    $"Replay PASS dataset={evidence.DatasetId} "
                    + $"output={evidence.ReplayOutputSha256} "
                    + $"ticks={evidence.TickCount}.");
                return 0;
            }

            if (string.Equals(
                    command,
                    "drill",
                    StringComparison.OrdinalIgnoreCase))
            {
                DemoDrillEvidence evidence =
                    await DemoDrillRunner.RunAsync(
                        configuration,
                        cancellation.Token)
                    .ConfigureAwait(false);

                Console.WriteLine(
                    $"Drills PASS outage={evidence.ModelOutagePassed} "
                    + $"stale={evidence.StaleDataPassed} "
                    + $"restart={evidence.RestartReconnectPassed}.");
                return 0;
            }

            await RunAsync(
                configuration,
                cancellation.Token).ConfigureAwait(false);

            return 0;
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested)
        {
            Console.WriteLine(
                "XauScalp demo runner stopped by user.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"XauScalp demo runner FAIL-CLOSED: {exception.GetType().Name}: {exception.Message}");
            return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: XauScalp.DemoRunner <run|replay|drill> <config.json>");
    }

    private static async Task RunAsync(
        DemoRunnerConfiguration configuration,
        CancellationToken cancellationToken)
    {
        string commonRoot = configuration.ResolvePath(
            configuration.Mt5CommonFilesPath);
        string dataDirectory = configuration.ResolvePath(
            configuration.DataDirectory);
        Directory.CreateDirectory(dataDirectory);

        string marketWirePath = Path.Combine(
            commonRoot,
            MarketWireRelativePath.Replace(
                '/',
                Path.DirectorySeparatorChar));
        string executionCommandPath = Path.Combine(
            commonRoot,
            ExecutionCommandsRelativePath.Replace(
                '/',
                Path.DirectorySeparatorChar));
        string executionEventPath = Path.Combine(
            commonRoot,
            ExecutionEventsRelativePath.Replace(
                '/',
                Path.DirectorySeparatorChar));

        string rawDatasetPath = Path.Combine(
            dataDirectory,
            "raw-market-events.jsonl");
        string decisionTelemetryPath = Path.Combine(
            dataDirectory,
            "decision-comparison.jsonl");
        string executionJournalPath = Path.Combine(
            dataDirectory,
            "execution-journal.jsonl");
        string riskLedgerPath = Path.Combine(
            dataDirectory,
            "risk-ledger.jsonl");
        string observationPath = Path.Combine(
            dataDirectory,
            "demo-observations.jsonl");
        string lastReadyStatePath = Path.Combine(
            dataDirectory,
            "last-ready-state.json");
        string liveFeatureSnapshotPath = Path.Combine(
            dataDirectory,
            "live-feature-snapshots.jsonl");

        PositionOwnership ownership =
            configuration.BuildOwnership();

        var gatewayOptions = new Mt5DemoExecutionGatewayOptions(
            executionCommandPath,
            executionEventPath,
            configuration.CanonicalSymbol,
            configuration.BrokerSymbol,
            ownership,
            configuration.Risk.MaxSlippagePoints);

        var brokerGateway = new Mt5DemoExecutionBrokerGateway(
            gatewayOptions);

        await using var executionJournal =
            new ExecutionJournalJsonlStore(
                executionJournalPath);
        var executionEngine = new ExecutionEngine(
            brokerGateway,
            executionJournal,
            ownership,
            timeProvider: TimeProvider.System);

        await using var riskLedger =
            new RiskLedgerJsonlStore(
                riskLedgerPath,
                ownership);

        await using var observations =
            new DemoObservationJsonlStore(
                observationPath);

        var riskSynchronizer = new DemoRiskLedgerSynchronizer(
            riskLedger,
            ownership,
            observations);

        ExecutionReconciliationReport startupReconciliation =
            await executionEngine.ReconcileAsync(
                cancellationToken).ConfigureAwait(false);

        await observations.AppendAsync(
            new DemoObservation(
                DemoObservationKind.StartupReconciliation,
                DateTimeOffset.UtcNow,
                MarketStateId: null,
                ComparisonId: null,
                DecisionId: null,
                RiskDecisionId: null,
                TradeIntentId: null,
                startupReconciliation.IsSafeForNewEntry
                    ? "safe"
                    : "blocked",
                ReasonCode: null,
                BrokerOrderId: null,
                BrokerDealId: null,
                BrokerPositionId: null,
                ModelLatencyMs: null,
                SlippagePoints: null,
                Message: startupReconciliation.Issues.Count == 0
                    ? "startup reconciliation clean"
                    : string.Join(
                        " | ",
                        startupReconciliation.Issues.Select(
                            static issue => issue.Message))),
            cancellationToken).ConfigureAwait(false);

        Mt5DemoBrokerContextSnapshot startupContext =
            await brokerGateway.QueryDemoContextAsync(
                cancellationToken).ConfigureAwait(false);

        await riskSynchronizer.EnsureAndSyncAsync(
            startupContext,
            cancellationToken).ConfigureAwait(false);

        LoadedNativeArtifact loadedNative =
            NativeArtifactLoader.Load(
                configuration.ResolvePath(
                    configuration.NativeArtifactPath),
                configuration.ResolvePath(
                    configuration.NativeArtifactManifestPath));

        var nativeModel = new XauNativeDecisionModel(
            loadedNative);

        using var httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var jevHttpOptions = new JevHttpProviderClientOptions(
            new Uri(
                configuration.Jev.Endpoint,
                UriKind.Absolute),
            configuration.Jev.AuthorizationScheme,
            allowInsecureHttp:
                configuration.Jev.AllowInsecureHttp);

        var jevProviderClient = new JevHttpProviderClient(
            httpClient,
            jevHttpOptions);

        var jevModel = new JevDecisionModel(
            jevProviderClient,
            new ReferenceJevSecretProvider(),
            new JevDecisionModelOptions(
                configuration.Jev.ProviderModelId,
                configuration.Jev.ProviderModelVersion,
                configuration.Jev.SecretReference,
                TimeSpan.FromMilliseconds(
                    configuration.Jev.TimeoutMs),
                TimeSpan.FromMilliseconds(
                    configuration.Jev.MaxResponseAgeMs),
                configuration.Jev.MaxRetries,
                configuration.Jev.CircuitBreakerFailureThreshold,
                TimeSpan.FromSeconds(
                    configuration.Jev.CircuitBreakerOpenSec)));

        await using var decisionStore =
            new DecisionComparisonJsonlStore(
                decisionTelemetryPath);

        var modelOrchestrator =
            new PrimaryShadowDecisionOrchestrator(
                jevModel,
                nativeModel,
                decisionStore);

        var settings = new XauScalpSettings(
            ContractVersions.SettingsV1,
            configuration.Trade.SettingsVersion,
            configuration.BuildDecisionSettings(),
            configuration.BuildRiskSettings());

        var riskPolicy = new RiskPolicyConfiguration(
            configuration.Trade.RiskPolicyVersion,
            configuration.Trade.ProtectiveStopDistancePrice,
            ownership);

        var tradePlanner = new TradePlanner(
            new TradePlannerConfiguration(
                settings.SettingsVersion,
                configuration.Trade.TakeProfitDistancePrice));

        var runtime = new DemoTradingRuntimeCoordinator(
            modelOrchestrator,
            brokerGateway,
            executionEngine,
            riskLedger,
            riskPolicy,
            settings,
            tradePlanner);

        var featureEngine = new XauFeatureEngine(
            new XauFeatureEngineOptions(
                configuration.BuildBrokerClock(),
                configuration.BuildSessionSchedule(),
                TimeSpan.FromSeconds(
                    configuration.ExternalContextMaxAgeSec),
                settings.Risk.HighImpactNewsBlockBeforeSec,
                settings.Risk.HighImpactNewsBlockAfterSec,
                TimeSpan.FromSeconds(
                    configuration.MaxBrokerTickAgeSec)));

        var costScenario = new ReplayCostScenario(
            "broker-demo-configured-costs",
            configuration.CostAssumptions.EstimatedLatencyMs,
            configuration.CostAssumptions.EstimatedSlippagePoints,
            configuration.CostAssumptions.CommissionPerLot);

        var runtimeFeatureContext =
            new CausalAtrReplayContextProvider(
                configuration.BuildBrokerClock());
        var trigger = new DemoEvaluationTrigger(
            configuration.Evaluation);

        await using var rawStore =
            new AppendOnlyJsonlMarketEventStore(
                rawDatasetPath,
                flushEveryRecords: 1);
        await using var liveFeatureSnapshots =
            new LiveFeatureSnapshotJsonlStore(
                liveFeatureSnapshotPath);

        long? lastRecordedSequence =
            await rawStore.GetHighestSourceSequenceIdAsync(
                cancellationToken).ConfigureAwait(false);

        long restoredEvents = await RestoreFeatureStateAsync(
            rawStore,
            featureEngine,
            runtimeFeatureContext,
            costScenario,
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine(
            $"Restored {restoredEvents} raw events; resume sequence = {lastRecordedSequence?.ToString() ?? "none"}.");
        Console.WriteLine(
            "Live money is not available in this runner. MT5 demo verification is mandatory.");

        var marketSource = new Mt5MarketDataSource(
            new Mt5NdjsonFileTransport(
                marketWirePath,
                follow: true,
                startAfterSourceSequenceId:
                    lastRecordedSequence ?? -1),
            new Mt5GatewayOptions(
                configuration.DataSourceId,
                new BrokerSymbolMapping(
                    configuration.CanonicalSymbol,
                    configuration.BrokerSymbol)),
            clock: null,
            initialHighestSourceSequenceId:
                lastRecordedSequence);

        await foreach (MarketEvent marketEvent in marketSource
            .ReadEventsAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            await rawStore.AppendAsync(
                marketEvent,
                cancellationToken).ConfigureAwait(false);

            if (marketEvent is not TickEvent tick)
            {
                featureEngine.ObserveContext(marketEvent);
                continue;
            }

            featureEngine.SetExternalContext(
                runtimeFeatureContext.GetContext(
                    tick,
                    costScenario));

            XauMarketState state =
                featureEngine.Update(tick);

            await liveFeatureSnapshots.AppendAsync(
                state,
                cancellationToken).ConfigureAwait(false);

            if (state.Readiness.RequiredP0Ready)
            {
                WriteLastReadyState(
                    lastReadyStatePath,
                    state);
            }

            if (!trigger.ShouldEvaluate(state))
            {
                continue;
            }

            Mt5DemoBrokerContextSnapshot brokerContext =
                await brokerGateway.QueryDemoContextAsync(
                    cancellationToken).ConfigureAwait(false);

            await riskSynchronizer.EnsureAndSyncAsync(
                brokerContext,
                cancellationToken).ConfigureAwait(false);

            DemoRuntimeEvaluation evaluation =
                await runtime.EvaluateStateAsync(
                    state,
                    cancellationToken).ConfigureAwait(false);

            await observations.AppendEvaluationAsync(
                state,
                evaluation,
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine(
                $"{DateTimeOffset.UtcNow:O} state={state.MarketStateId:D} "
                + $"outcome={evaluation.Outcome} "
                + $"risk={evaluation.RiskDecision?.ReasonCode ?? "n/a"} "
                + $"exec={evaluation.ExecutionResult?.State.ToString() ?? "n/a"}");

            await riskSynchronizer.RecordExecutionFailureAsync(
                state,
                evaluation,
                cancellationToken).ConfigureAwait(false);

            if (configuration.StopAfterFirstExecution
                && evaluation.Outcome
                    == DemoRuntimeOutcome.ExecutionAttempted)
            {
                Console.WriteLine(
                    "StopAfterFirstExecution reached.");
                return;
            }
        }
    }

    private static async Task<long> RestoreFeatureStateAsync(
        AppendOnlyJsonlMarketEventStore rawStore,
        XauFeatureEngine featureEngine,
        CausalAtrReplayContextProvider runtimeContext,
        ReplayCostScenario costScenario,
        CancellationToken cancellationToken)
    {
        long count = 0;

        await foreach (MarketEvent marketEvent in rawStore
            .ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (marketEvent is TickEvent tick)
            {
                featureEngine.SetExternalContext(
                    runtimeContext.GetContext(
                        tick,
                        costScenario));
                _ = featureEngine.Update(tick);
            }
            else
            {
                featureEngine.ObserveContext(marketEvent);
            }

            count = checked(count + 1);
        }

        return count;
    }

    private static void WriteLastReadyState(
        string path,
        XauMarketState state)
    {
        string temporary = path + ".tmp";
        string json = JsonSerializer.Serialize(
            state,
            XauJson.CreateOptions());

        File.WriteAllText(
            temporary,
            json + Environment.NewLine);
        File.Move(
            temporary,
            path,
            overwrite: true);
    }
}
