using System.Runtime.CompilerServices;
using System.Text.Json;
using XauScalp.DecisionModels;
using XauScalp.Domain;
using XauScalp.Execution;
using XauScalp.Features;
using XauScalp.MarketData;
using XauScalp.Persistence;
using XauScalp.Replay;
using XauScalp.Risk;

namespace XauScalp.IntegrationTests;

public sealed class DemoReadinessCiRehearsalTests
{
    private static readonly PositionOwnership Ownership =
        new(991188, "xsp017-ci", "xauscalp");

    [Fact]
    public async Task RecordedFixture_ReplaysDeterministicallyWithFeatureParity()
    {
        MarketEvent[] dataset = BuildDataset();
        ReplayRunOptions options = ReplayOptions();

        var firstSink = new InMemoryReplayRecordSink();
        var secondSink = new InMemoryReplayRecordSink();

        ReplayRunResult first = await Runner().RunAsync(
            AsAsync(dataset),
            options,
            firstSink,
            new RehearsalFeatureContextProvider());

        ReplayRunResult second = await Runner().RunAsync(
            AsAsync(dataset),
            options,
            secondSink,
            new RehearsalFeatureContextProvider());

        Assert.Equal(first.Manifest.DataSetSha256, second.Manifest.DataSetSha256);
        Assert.Equal(first.OutputSha256, second.OutputSha256);
        Assert.Equal(first.Manifest.RunId, second.Manifest.RunId);

        XauMarketState firstState = LastReadyState(firstSink);
        XauMarketState secondState = LastReadyState(secondSink);

        Assert.True(firstState.Readiness.RequiredP0Ready);
        Assert.Equal(firstState.MarketStateId, secondState.MarketStateId);
        Assert.Equal(
            JsonSerializer.Serialize(firstState, XauJson.CreateOptions()),
            JsonSerializer.Serialize(secondState, XauJson.CreateOptions()));
    }

    [Fact]
    public async Task PrimaryShadow_Risk_Execution_RehearsalRunsEndToEnd()
    {
        XauMarketState state = await BuildReadyStateAsync();

        var jev = new FixedDecisionModel(
            DecisionModelType.Jev,
            TradeAction.Long);
        var native = new FixedDecisionModel(
            DecisionModelType.XauNative,
            TradeAction.Short);
        var store = new InMemoryDecisionComparisonStore();
        var tradeSink = new RecordingTradeSink();

        var orchestrator = new PrimaryShadowDecisionOrchestrator(
            jev,
            native,
            store,
            tradeSink);

        PrimaryShadowEvaluationResult evaluation =
            await orchestrator.EvaluateAsync(
                state,
                new DecisionModelSettings(
                    DecisionModelType.Jev,
                    JevFailurePolicy.StopNewTrades,
                    new ShadowComparisonSettings(
                        enabled: true,
                        shadowModel: DecisionModelType.XauNative)),
                CancellationToken.None);

        Assert.False(evaluation.StopNewTrades);
        Assert.Equal(
            TradeAction.Long,
            evaluation.AuthoritativeDecision!.Action);
        Assert.Same(state, jev.LastState);
        Assert.Same(state, native.LastState);

        AuthoritativeTradeDecisionEnvelope envelope =
            Assert.Single(tradeSink.Envelopes);
        Assert.Equal(
            evaluation.AuthoritativeDecision.DecisionId,
            envelope.Decision.DecisionId);

        DecisionComparisonBundle comparison = Assert.Single(
            await store.ReadAllAsync(CancellationToken.None));
        Assert.True(comparison.Comparison.Primary.IsAuthoritative);
        Assert.False(comparison.Comparison.Shadow!.IsAuthoritative);

        DateTimeOffset now = state.TimestampUtc;
        HardRiskEngine risk = RiskEngine(now);
        PortfolioState portfolio = Portfolio(now);
        RiskSettings settings = RiskSettingsForRehearsal();

        RiskDecision riskDecision = risk.Evaluate(
            state,
            evaluation.AuthoritativeDecision,
            portfolio,
            settings);

        Assert.Equal(
            RiskDecisionOutcome.Authorized,
            riskDecision.Outcome);

        TradePlan plan = PlanFrom(
            state,
            evaluation.AuthoritativeDecision,
            riskDecision,
            now);

        string path = TempJournalPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            var broker = new RehearsalBroker();

            await using (var journal = new ExecutionJournalJsonlStore(path))
            {
                var execution = new ExecutionEngine(
                    broker,
                    journal,
                    Ownership,
                    new ExecutionEngineOptions(MaxSafeSubmitRetries: 1),
                    new FixedTimeProvider(now));

                ExecutionResult open = await execution.SubmitAsync(
                    plan,
                    CancellationToken.None);

                Assert.Equal(OrderLifecycleState.Open, open.State);
                Assert.Equal(1, broker.SubmitCalls);
            }

            await using var restartedJournal =
                new ExecutionJournalJsonlStore(path);

            var restarted = new ExecutionEngine(
                broker,
                restartedJournal,
                Ownership,
                new ExecutionEngineOptions(MaxSafeSubmitRetries: 1),
                new FixedTimeProvider(now.AddSeconds(1)));

            ExecutionReconciliationReport reconciliation =
                await restarted.ReconcileAsync(CancellationToken.None);

            Assert.True(reconciliation.IsSafeForNewEntry);

            ExecutionResult duplicate = await restarted.SubmitAsync(
                plan,
                CancellationToken.None);

            Assert.Equal(OrderLifecycleState.Open, duplicate.State);
            Assert.Equal(
                1,
                broker.SubmitCalls);
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Fact]
    public async Task ModelOutage_StopsOrUsesExplicitNativeFallbackOnly()
    {
        XauMarketState state = await BuildReadyStateAsync();

        var offlineJev = new FixedDecisionModel(
            DecisionModelType.Jev,
            TradeAction.Long,
            new JevProviderUnavailableException("ci-rehearsal-offline"));
        var native = new FixedDecisionModel(
            DecisionModelType.XauNative,
            TradeAction.Long);

        var stopSink = new RecordingTradeSink();
        var stop = new PrimaryShadowDecisionOrchestrator(
            offlineJev,
            native,
            new InMemoryDecisionComparisonStore(),
            stopSink);

        PrimaryShadowEvaluationResult stopped = await stop.EvaluateAsync(
            state,
            new DecisionModelSettings(
                DecisionModelType.Jev,
                JevFailurePolicy.StopNewTrades,
                new ShadowComparisonSettings(false, null)),
            CancellationToken.None);

        Assert.True(stopped.StopNewTrades);
        Assert.Null(stopped.AuthoritativeDecision);
        Assert.Empty(stopSink.Envelopes);

        var fallbackSink = new RecordingTradeSink();
        var fallback = new PrimaryShadowDecisionOrchestrator(
            offlineJev,
            native,
            new InMemoryDecisionComparisonStore(),
            fallbackSink);

        PrimaryShadowEvaluationResult recovered =
            await fallback.EvaluateAsync(
                state,
                new DecisionModelSettings(
                    DecisionModelType.Jev,
                    JevFailurePolicy.FallbackToXauNative,
                    new ShadowComparisonSettings(false, null)),
                CancellationToken.None);

        Assert.False(recovered.StopNewTrades);
        Assert.True(recovered.UsedFallback);
        Assert.Equal(
            DecisionModelType.XauNative,
            recovered.AuthoritativeModel);

        AuthoritativeTradeDecisionEnvelope envelope =
            Assert.Single(fallbackSink.Envelopes);
        Assert.True(envelope.IsFallback);
    }

    [Fact]
    public async Task HardRisk_RejectsStaleDecisionFailClosed()
    {
        XauMarketState state = await BuildReadyStateAsync();
        DateTimeOffset now = state.TimestampUtc.AddSeconds(5);

        XauDecision staleDecision = Decision(
            state,
            DecisionModelType.XauNative,
            TradeAction.Long,
            evaluatedAtUtc: state.TimestampUtc);

        RiskDecision result = RiskEngine(now).Evaluate(
            state,
            staleDecision,
            Portfolio(now),
            RiskSettingsForRehearsal(
                maxDecisionAgeMs: 1_000,
                maxFeatureAgeMs: 10_000));

        Assert.Equal(
            RiskDecisionOutcome.Rejected,
            result.Outcome);
        Assert.Equal(
            "decision-stale",
            result.ReasonCode);
    }

    [Fact]
    public async Task HardRisk_RejectsStaleMarketStateFailClosed()
    {
        XauMarketState state = await BuildReadyStateAsync();
        DateTimeOffset now = state.TimestampUtc.AddSeconds(5);

        XauDecision freshDecision = Decision(
            state,
            DecisionModelType.XauNative,
            TradeAction.Long,
            evaluatedAtUtc: now);

        RiskDecision result = RiskEngine(now).Evaluate(
            state,
            freshDecision,
            Portfolio(now),
            RiskSettingsForRehearsal(
                maxDecisionAgeMs: 10_000,
                maxFeatureAgeMs: 1_000));

        Assert.Equal(
            RiskDecisionOutcome.Rejected,
            result.Outcome);
        Assert.Equal(
            "market-state-stale",
            result.ReasonCode);
    }

    [Fact]
    public void LiveMoneyAuthorization_RemainsDisabledAtDemoGate()
    {
        var settings = new XauScalp.App.Core.DesktopSettingsViewModel();

        Assert.False(settings.LiveTradingAuthorized);
        Assert.Equal(
            "Research / Demo only",
            settings.OperatingModeStatus);
    }

    private static async Task<XauMarketState> BuildReadyStateAsync()
    {
        var sink = new InMemoryReplayRecordSink();

        _ = await Runner().RunAsync(
            AsAsync(BuildDataset()),
            ReplayOptions(),
            sink,
            new RehearsalFeatureContextProvider());

        return LastReadyState(sink);
    }

    private static DeterministicReplayRunner Runner()
    {
        return new DeterministicReplayRunner(
            CreateFeatureEngine,
            new ReplayEventScheduler(new NoDelay()));
    }

    private static IXauFeatureEngine CreateFeatureEngine()
    {
        var schedule = new MarketSessionSchedule(
        [
            new MarketSessionSegment(
                0,
                "A",
                TimeOnly.MinValue,
                new TimeOnly(12, 0)),
            new MarketSessionSegment(
                1,
                "B",
                new TimeOnly(12, 0),
                TimeOnly.MinValue),
        ]);

        return new XauFeatureEngine(
            new XauFeatureEngineOptions(
                BrokerClockConfiguration.UtcV1,
                schedule,
                externalContextMaxAge: TimeSpan.FromMinutes(5)));
    }

    private static ReplayRunOptions ReplayOptions()
    {
        return new ReplayRunOptions(
            codeCommit: "xsp017-ci-rehearsal",
            settingsVersion: "settings-ci-v1",
            settingsHash: "settings-hash-ci",
            ReplayModelIdentity.None,
            new ReplayCostScenario(
                "ci-baseline",
                estimatedLatencyMs: 12,
                estimatedSlippagePoints: 3,
                commissionPerLot: 7.5m),
            ReplayTimingOptions.Accelerated(10_000),
            randomSeed: 17);
    }

    private static MarketEvent[] BuildDataset()
    {
        DateTimeOffset start = new(
            2026,
            9,
            21,
            0,
            0,
            0,
            TimeSpan.Zero);

        var events = new List<MarketEvent>
        {
            new SymbolSpecificationEvent(
                ContractVersions.MarketEventV1,
                start,
                start,
                0,
                "xsp017-ci-fixture",
                "XAUUSD",
                "XAUUSD.G",
                new SymbolSpecification(
                    digits: 2,
                    point: 0.01m,
                    tickSize: 0.01m,
                    tickValue: 1m,
                    contractSize: 100m,
                    minVolume: 0.01m,
                    maxVolume: 100m,
                    volumeStep: 0.01m,
                    minStopDistance: 0.50m)),
            new ConnectionStatusEvent(
                ContractVersions.MarketEventV1,
                start,
                start,
                0,
                "xsp017-ci-fixture",
                "XAUUSD",
                "XAUUSD.G",
                MarketConnectionState.Connected,
                "ci-rehearsal"),
        };

        for (int index = 0; index <= 50; index++)
        {
            DateTimeOffset timestamp =
                start.AddMilliseconds(index * 500);
            decimal bid =
                100m
                + (decimal)Math.Sin(index / 5.0) * 0.20m
                + (index * 0.002m);

            events.Add(
                new TickEvent(
                    ContractVersions.MarketEventV1,
                    timestamp,
                    timestamp,
                    index + 1,
                    "xsp017-ci-fixture",
                    "XAUUSD",
                    "XAUUSD.G",
                    bid,
                    bid + 0.20m,
                    null,
                    1 + (index % 3),
                    TickFlags.Bid
                        | TickFlags.Ask
                        | TickFlags.Volume));
        }

        return events.ToArray();
    }

    private static XauMarketState LastReadyState(
        InMemoryReplayRecordSink sink)
    {
        XauMarketState state = sink.Records
            .Where(static record => record.FeatureState is not null)
            .Select(static record => record.FeatureState!)
            .Last();

        Assert.True(
            state.Readiness.RequiredP0Ready,
            string.Join(
                ", ",
                state.Readiness.MissingRequirements));

        return state;
    }

    private static HardRiskEngine RiskEngine(DateTimeOffset now)
    {
        var profile = new RiskSymbolProfile(
            "XAUUSD",
            "XAUUSD.G",
            Point: 0.01m,
            TickSize: 0.01m,
            TickValue: 1m,
            MinVolume: 0.01m,
            MaxVolume: 100m,
            VolumeStep: 0.01m,
            MinStopDistance: 0.50m,
            EstimatedMarginPerLotMoney: 100m);

        var ledger = new OwnedRiskLedgerSnapshot(
            DateOnly.FromDateTime(now.UtcDateTime),
            IsReady: true,
            DayStartEquity: 10_000m,
            RealizedNetPnlMoney: 0m,
            ClosedTrades: 0,
            LastLossAtUtc: null,
            LastExecutionFailureAtUtc: null);

        return new HardRiskEngine(
            new FixedRiskRuntimeContextProvider(profile),
            new FixedOwnedRiskLedger(ledger),
            new RiskPolicyConfiguration(
                "risk-xsp017-ci-v1",
                ProtectiveStopDistancePrice: 2m,
                Ownership),
            new FixedTimeProvider(now));
    }

    private static RiskSettings RiskSettingsForRehearsal(
        int maxDecisionAgeMs = 1_000,
        int maxFeatureAgeMs = 1_000)
    {
        return new RiskSettings(
            maxRiskPerTradePct: 0.50,
            maxDailyLossPct: 5,
            maxDailyLossMoney: 500m,
            maxTradesPerDay: 20,
            maxConcurrentPositions: 1,
            maxSpreadPrice: 1m,
            maxSpreadAtrRatio: 0.5,
            maxSlippagePoints: 30,
            maxDecisionAgeMs,
            maxFeatureAgeMs,
            minFreeMarginPct: 20,
            cooldownAfterLossSec: 0,
            cooldownAfterExecutionFailureSec: 0,
            highImpactNewsBlockBeforeSec: 900,
            highImpactNewsBlockAfterSec: 900,
            allowLong: true,
            allowShort: true);
    }

    private static PortfolioState Portfolio(DateTimeOffset now)
    {
        return new PortfolioState(
            ContractVersions.PortfolioStateV1,
            now,
            balance: 10_000m,
            equity: 10_000m,
            freeMargin: 9_000m,
            realizedPnlToday: 0m,
            tradesToday: 0,
            positions: []);
    }

    private static TradePlan PlanFrom(
        XauMarketState state,
        XauDecision decision,
        RiskDecision risk,
        DateTimeOffset now)
    {
        return new TradePlan(
            ContractVersions.TradePlanV1,
            Guid.Parse("11111111-1111-4111-8111-111111111117"),
            state.MarketStateId,
            decision.DecisionId,
            risk.RiskDecisionId,
            state.Symbol,
            state.BrokerSymbol,
            TradeSide.Long,
            risk.AuthorizedVolumeLots!.Value,
            state.Ask,
            risk.ProtectiveStopPrice!.Value,
            state.Ask + 5m,
            risk.RiskPolicyVersion,
            "settings-ci-v1",
            now);
    }

    private static XauDecision Decision(
        XauMarketState state,
        DecisionModelType model,
        TradeAction action,
        DateTimeOffset evaluatedAtUtc)
    {
        double up = action == TradeAction.Long ? 0.80 : 0.30;
        double down = action == TradeAction.Short ? 0.80 : 0.30;

        return new XauDecision(
            ContractVersions.DecisionV1,
            Guid.NewGuid(),
            state.MarketStateId,
            model,
            action,
            actionProbability: 0.80,
            pUp5First: up,
            pDown5First: down,
            pUp10First: Math.Max(0.2, up - 0.15),
            pDown10First: Math.Max(0.2, down - 0.15),
            pAdverseBarrierFirst: 0.20,
            pContinuation: 0.60,
            pReversal: 0.40,
            pFalseBreak: 0.10,
            confidence: 0.75,
            pHold: null,
            pExitNow: null,
            pTp5FromHere: null,
            pTp10FromHere: null,
            modelId: model == DecisionModelType.Jev
                ? "jev-ci"
                : "xau-native-ci",
            modelVersion: "ci-v1",
            featureSchemaVersion: state.FeatureSchemaVersion,
            evaluatedAtUtc,
            evaluationLatency: TimeSpan.FromMilliseconds(2));
    }

    private static string TempJournalPath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "xauscalp-xsp017",
            Guid.NewGuid().ToString("N"),
            "execution.jsonl");
    }

    private static void DeleteJournal(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        string? directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async IAsyncEnumerable<MarketEvent> AsAsync(
        IEnumerable<MarketEvent> events,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        foreach (MarketEvent marketEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return marketEvent;
            await Task.Yield();
        }
    }

    private sealed class RehearsalFeatureContextProvider :
        IReplayFeatureContextProvider
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
                estimatedLatencyMs:
                    costScenario.EstimatedLatencyMs,
                estimatedSlippagePoints:
                    costScenario.EstimatedSlippagePoints,
                newsDistanceBeforeSec: 600,
                newsDistanceAfterSec: 120,
                isHighImpactNewsWindow: false);
        }
    }

    private sealed class FixedDecisionModel : IXauDecisionModel
    {
        private readonly DecisionModelType _model;
        private readonly TradeAction _action;
        private readonly Exception? _failure;

        public FixedDecisionModel(
            DecisionModelType model,
            TradeAction action,
            Exception? failure = null)
        {
            _model = model;
            _action = action;
            _failure = failure;
        }

        public string ModelId =>
            _model == DecisionModelType.Jev
                ? "jev-ci"
                : "xau-native-ci";

        public string ModelVersion => "ci-v1";

        public XauMarketState? LastState { get; private set; }

        public Task<XauDecision> EvaluateAsync(
            XauMarketState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastState = state;

            if (_failure is not null)
            {
                throw _failure;
            }

            return Task.FromResult(
                DemoReadinessCiRehearsalTests.Decision(
                    state,
                    _model,
                    _action,
                    state.TimestampUtc));
        }
    }

    private sealed class RecordingTradeSink :
        IAuthoritativeTradeDecisionSink
    {
        public List<AuthoritativeTradeDecisionEnvelope> Envelopes
        {
            get;
        } = [];

        public ValueTask AcceptAsync(
            AuthoritativeTradeDecisionEnvelope envelope,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Envelopes.Add(envelope);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RehearsalBroker :
        IExecutionBrokerGateway
    {
        private BrokerPositionSnapshot? _position;

        public int SubmitCalls { get; private set; }

        public Task<BrokerExecutionResponse> SubmitAsync(
            TradePlan plan,
            PositionOwnership ownership,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubmitCalls++;

            _position = new BrokerPositionSnapshot(
                plan.TradeIntentId,
                "ci-position-1",
                plan.Symbol,
                plan.BrokerSymbol,
                plan.Side,
                plan.VolumeLots,
                plan.PlannedEntryPrice,
                plan.ProtectiveStopPrice,
                plan.TakeProfitPrice,
                ownership);

            return Task.FromResult(
                new BrokerExecutionResponse(
                    Outcome: BrokerExecutionOutcome.Filled,
                    BrokerOrderId: "ci-order-1",
                    BrokerDealId: "ci-deal-1",
                    BrokerPositionId: "ci-position-1",
                    RequestedPrice: plan.PlannedEntryPrice,
                    FillPrice: plan.PlannedEntryPrice,
                    RequestedVolumeLots: plan.VolumeLots,
                    FilledVolumeLots: plan.VolumeLots,
                    SlippagePoints: 0,
                    Latency: TimeSpan.FromMilliseconds(10),
                    BrokerRetcode: "CI_FILLED",
                    Message: null,
                    SafeToRetry: false));
        }

        public Task<BrokerExecutionResponse> ModifyAsync(
            PositionCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException(
                "CI rehearsal does not require modification.");
        }

        public Task<BrokerExecutionResponse> CloseAsync(
            PositionCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException(
                "CI rehearsal does not require close.");
        }

        public Task<BrokerReconciliationSnapshot> QueryStateAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<BrokerPositionSnapshot> positions =
                _position is null ? [] : [_position];

            return Task.FromResult(
                new BrokerReconciliationSnapshot(
                    positions,
                    Orders: [],
                    ClosedTradeIntentIds: new HashSet<Guid>()));
        }
    }

    private sealed class NoDelay : IReplayDelay
    {
        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
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
