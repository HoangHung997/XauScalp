using XauScalp.DecisionModels;
using XauScalp.Domain;
using XauScalp.Execution;
using XauScalp.Risk;
using XauScalp.Runtime;

namespace XauScalp.IntegrationTests;

public sealed class DemoTradingRuntimeCoordinatorTests
{
    private static readonly PositionOwnership Ownership =
        new(991188, "demo-runtime-1", "xauscalp");

    [Fact]
    public async Task ActionablePrimary_TraversesHardRiskPlannerAndExecution()
    {
        DateTimeOffset now = Utc();
        XauMarketState state = State(now, spread: 0.20m);

        var broker = new FakeBrokerGateway(Ownership);
        var contextProvider = new FakeBrokerContextProvider(
            BrokerContext(now));
        var execution = new ExecutionEngine(
            broker,
            new InMemoryExecutionJournal(),
            Ownership,
            timeProvider: new FixedTimeProvider(now));

        DemoTradingRuntimeCoordinator runtime = Runtime(
            state,
            now,
            primaryAction: TradeAction.Long,
            broker,
            contextProvider,
            execution);

        DemoRuntimeEvaluation result = await runtime.EvaluateStateAsync(
            state,
            CancellationToken.None);

        Assert.Equal(
            DemoRuntimeOutcome.ExecutionAttempted,
            result.Outcome);
        Assert.NotNull(result.RiskDecision);
        Assert.Equal(
            RiskDecisionOutcome.Authorized,
            result.RiskDecision!.Outcome);
        Assert.NotNull(result.TradePlan);
        Assert.Equal(
            result.RiskDecision.RiskDecisionId,
            result.TradePlan!.RiskDecisionId);
        Assert.NotNull(result.ExecutionResult);
        Assert.Equal(
            OrderLifecycleState.Open,
            result.ExecutionResult!.State);
        Assert.Equal(1, broker.SubmitCount);
        Assert.Equal(1, contextProvider.QueryCount);
        Assert.Equal(1, broker.QueryCount);
    }

    [Fact]
    public async Task PrimaryWait_NeverTouchesBrokerOrRiskExecutionPath()
    {
        DateTimeOffset now = Utc();
        XauMarketState state = State(now, spread: 0.20m);

        var broker = new FakeBrokerGateway(Ownership);
        var contextProvider = new FakeBrokerContextProvider(
            BrokerContext(now));
        var execution = new ExecutionEngine(
            broker,
            new InMemoryExecutionJournal(),
            Ownership,
            timeProvider: new FixedTimeProvider(now));

        DemoTradingRuntimeCoordinator runtime = Runtime(
            state,
            now,
            primaryAction: TradeAction.Wait,
            broker,
            contextProvider,
            execution);

        DemoRuntimeEvaluation result = await runtime.EvaluateStateAsync(
            state,
            CancellationToken.None);

        Assert.Equal(
            DemoRuntimeOutcome.NoActionablePrimaryDecision,
            result.Outcome);
        Assert.Equal(0, broker.QueryCount);
        Assert.Equal(0, broker.SubmitCount);
        Assert.Equal(0, contextProvider.QueryCount);
    }

    [Fact]
    public async Task ShadowAction_CannotCreateTradeWhenPrimaryWaits()
    {
        DateTimeOffset now = Utc();
        XauMarketState state = State(now, spread: 0.20m);

        var broker = new FakeBrokerGateway(Ownership);
        var contextProvider = new FakeBrokerContextProvider(
            BrokerContext(now));
        var execution = new ExecutionEngine(
            broker,
            new InMemoryExecutionJournal(),
            Ownership,
            timeProvider: new FixedTimeProvider(now));

        DemoTradingRuntimeCoordinator runtime = Runtime(
            state,
            now,
            primaryAction: TradeAction.Wait,
            broker,
            contextProvider,
            execution,
            shadowAction: TradeAction.Long);

        DemoRuntimeEvaluation result = await runtime.EvaluateStateAsync(
            state,
            CancellationToken.None);

        Assert.Equal(
            DemoRuntimeOutcome.NoActionablePrimaryDecision,
            result.Outcome);
        Assert.Equal(0, broker.SubmitCount);
        Assert.Equal(0, broker.QueryCount);
    }

    [Fact]
    public async Task BrokerOrphanBlocksNewEntryBeforeRiskContextOrSubmit()
    {
        DateTimeOffset now = Utc();
        XauMarketState state = State(now, spread: 0.20m);

        var broker = new FakeBrokerGateway(
            Ownership,
            reconciliation: new BrokerReconciliationSnapshot(
                Positions:
                [
                    new BrokerPositionSnapshot(
                        Guid.Parse(
                            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
                        "orphan-position",
                        "XAUUSD",
                        "XAUUSD.G",
                        TradeSide.Long,
                        0.10m,
                        2500m,
                        2495m,
                        2510m,
                        Ownership),
                ],
                Orders: [],
                ClosedTradeIntentIds: new HashSet<Guid>()));

        var contextProvider = new FakeBrokerContextProvider(
            BrokerContext(now));
        var execution = new ExecutionEngine(
            broker,
            new InMemoryExecutionJournal(),
            Ownership,
            timeProvider: new FixedTimeProvider(now));

        DemoTradingRuntimeCoordinator runtime = Runtime(
            state,
            now,
            primaryAction: TradeAction.Long,
            broker,
            contextProvider,
            execution);

        DemoRuntimeEvaluation result = await runtime.EvaluateStateAsync(
            state,
            CancellationToken.None);

        Assert.Equal(
            DemoRuntimeOutcome.ReconciliationBlocked,
            result.Outcome);
        Assert.True(result.StopNewTrades);
        Assert.NotNull(result.Reconciliation);
        Assert.False(result.Reconciliation!.IsSafeForNewEntry);
        Assert.Equal(0, contextProvider.QueryCount);
        Assert.Equal(0, broker.SubmitCount);
    }

    [Fact]
    public async Task HardRiskRejection_NeverSubmitsTradePlan()
    {
        DateTimeOffset now = Utc();
        XauMarketState state = State(now, spread: 2.00m);

        var broker = new FakeBrokerGateway(Ownership);
        var contextProvider = new FakeBrokerContextProvider(
            BrokerContext(now));
        var execution = new ExecutionEngine(
            broker,
            new InMemoryExecutionJournal(),
            Ownership,
            timeProvider: new FixedTimeProvider(now));

        DemoTradingRuntimeCoordinator runtime = Runtime(
            state,
            now,
            primaryAction: TradeAction.Long,
            broker,
            contextProvider,
            execution);

        DemoRuntimeEvaluation result = await runtime.EvaluateStateAsync(
            state,
            CancellationToken.None);

        Assert.Equal(
            DemoRuntimeOutcome.RiskRejected,
            result.Outcome);
        Assert.Equal(
            "spread-price-limit",
            result.RiskDecision!.ReasonCode);
        Assert.Null(result.TradePlan);
        Assert.Null(result.ExecutionResult);
        Assert.Equal(0, broker.SubmitCount);
    }

    private static DemoTradingRuntimeCoordinator Runtime(
        XauMarketState state,
        DateTimeOffset now,
        TradeAction primaryAction,
        FakeBrokerGateway broker,
        FakeBrokerContextProvider contextProvider,
        ExecutionEngine execution,
        TradeAction shadowAction = TradeAction.Short)
    {
        var clock = new FixedTimeProvider(now);
        var store = new InMemoryDecisionComparisonStore();

        IXauDecisionModel native = new FixedDecisionModel(
            DecisionModelType.XauNative,
            primaryAction,
            now);
        IXauDecisionModel jev = new FixedDecisionModel(
            DecisionModelType.Jev,
            shadowAction,
            now);

        var models = new PrimaryShadowDecisionOrchestrator(
            jev,
            native,
            store,
            timeProvider: clock);

        var settings = new XauScalpSettings(
            ContractVersions.SettingsV1,
            "settings-demo-v1",
            new DecisionModelSettings(
                DecisionModelType.XauNative,
                JevFailurePolicy.StopNewTrades,
                new ShadowComparisonSettings(
                    enabled: true,
                    DecisionModelType.Jev)),
            new RiskSettings(
                maxRiskPerTradePct: 1,
                maxDailyLossPct: 5,
                maxDailyLossMoney: 500m,
                maxTradesPerDay: 20,
                maxConcurrentPositions: 1,
                maxSpreadPrice: 1m,
                maxSpreadAtrRatio: 0.5,
                maxSlippagePoints: 30,
                maxDecisionAgeMs: 2_000,
                maxFeatureAgeMs: 2_000,
                minFreeMarginPct: 20,
                cooldownAfterLossSec: 0,
                cooldownAfterExecutionFailureSec: 0,
                highImpactNewsBlockBeforeSec: 0,
                highImpactNewsBlockAfterSec: 0,
                allowLong: true,
                allowShort: true));

        var ledger = new FixedOwnedRiskLedger(
            new OwnedRiskLedgerSnapshot(
                DateOnly.FromDateTime(now.UtcDateTime),
                IsReady: true,
                DayStartEquity: 10_000m,
                RealizedNetPnlMoney: 0m,
                ClosedTrades: 0,
                LastLossAtUtc: null,
                LastExecutionFailureAtUtc: null));

        var policy = new RiskPolicyConfiguration(
            "risk-demo-v1",
            ProtectiveStopDistancePrice: 2m,
            Ownership);

        var planner = new TradePlanner(
            new TradePlannerConfiguration(
                settings.SettingsVersion,
                TakeProfitDistancePrice: 5m));

        return new DemoTradingRuntimeCoordinator(
            models,
            contextProvider,
            execution,
            ledger,
            policy,
            settings,
            planner,
            clock);
    }

    private static Mt5DemoBrokerContextSnapshot BrokerContext(
        DateTimeOffset now)
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
                balance: 10_000m,
                equity: 10_000m,
                freeMargin: 9_000m,
                realizedPnlToday: 0m,
                tradesToday: 0,
                positions: []),
            new Mt5DemoSymbolRiskWire(
                Point: 0.01m,
                TickSize: 0.10m,
                TickValue: 1m,
                MinVolume: 0.01m,
                MaxVolume: 100m,
                VolumeStep: 0.01m,
                MinStopDistance: 0.50m,
                EstimatedMarginPerLotMoney: 100m));
    }

    private static XauMarketState State(
        DateTimeOffset now,
        decimal spread)
    {
        return new XauMarketState(
            ContractVersions.MarketStateV1,
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            now,
            now,
            42,
            "XAUUSD",
            "XAUUSD.G",
            2500m,
            2500m + spread,
            2500m + (spread / 2m),
            ContractVersions.FeatureSchemaV1,
            "runtime-test",
            LiquiditySource.None,
            new DataReadiness(
                requiredP0Ready: true,
                tickHistoryReady: true,
                barHistoryReady: true,
                newsDataAvailable: false,
                missingRequirements: []),
            [
                new NumericFeatureValue(
                    "SpreadAtrRatio",
                    0.1,
                    "ratio",
                    true,
                    now,
                    null),
            ]);
    }

    private static DateTimeOffset Utc()
    {
        return new DateTimeOffset(
            2026,
            9,
            21,
            12,
            0,
            0,
            TimeSpan.Zero);
    }

    private sealed class FixedDecisionModel : IXauDecisionModel
    {
        private readonly DecisionModelType _modelType;
        private readonly TradeAction _action;
        private readonly DateTimeOffset _evaluatedAtUtc;

        public FixedDecisionModel(
            DecisionModelType modelType,
            TradeAction action,
            DateTimeOffset evaluatedAtUtc)
        {
            _modelType = modelType;
            _action = action;
            _evaluatedAtUtc = evaluatedAtUtc;
        }

        public string ModelId => _modelType == DecisionModelType.Jev
            ? "jev"
            : "xau-native";

        public string ModelVersion => "test-v1";

        public Task<XauDecision> EvaluateAsync(
            XauMarketState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var decision = new XauDecision(
                ContractVersions.DecisionV1,
                Guid.NewGuid(),
                state.MarketStateId,
                _modelType,
                _action,
                0.8,
                0.8,
                0.2,
                0.7,
                0.3,
                0.1,
                0.7,
                0.2,
                0.1,
                0.8,
                null,
                null,
                null,
                null,
                ModelId,
                ModelVersion,
                state.FeatureSchemaVersion,
                _evaluatedAtUtc,
                TimeSpan.FromMilliseconds(1));

            return Task.FromResult(decision);
        }
    }

    private sealed class FakeBrokerContextProvider
        : IMt5DemoBrokerContextProvider
    {
        private readonly Mt5DemoBrokerContextSnapshot _snapshot;

        public FakeBrokerContextProvider(
            Mt5DemoBrokerContextSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public int QueryCount { get; private set; }

        public Task<Mt5DemoBrokerContextSnapshot> QueryDemoContextAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueryCount++;
            return Task.FromResult(_snapshot);
        }
    }

    private sealed class FakeBrokerGateway : IExecutionBrokerGateway
    {
        private readonly PositionOwnership _ownership;
        private readonly BrokerReconciliationSnapshot _reconciliation;

        public FakeBrokerGateway(
            PositionOwnership ownership,
            BrokerReconciliationSnapshot? reconciliation = null)
        {
            _ownership = ownership;
            _reconciliation = reconciliation
                ?? new BrokerReconciliationSnapshot(
                    [],
                    [],
                    new HashSet<Guid>());
        }

        public int QueryCount { get; private set; }

        public int SubmitCount { get; private set; }

        public Task<BrokerExecutionResponse> SubmitAsync(
            TradePlan plan,
            PositionOwnership ownership,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_ownership, ownership);
            SubmitCount++;

            return Task.FromResult(
                new BrokerExecutionResponse(
                    BrokerExecutionOutcome.Filled,
                    BrokerOrderId: "order-1",
                    BrokerDealId: "deal-1",
                    BrokerPositionId: "position-1",
                    RequestedPrice: plan.PlannedEntryPrice,
                    FillPrice: plan.PlannedEntryPrice,
                    RequestedVolumeLots: plan.VolumeLots,
                    FilledVolumeLots: plan.VolumeLots,
                    SlippagePoints: 0,
                    Latency: TimeSpan.FromMilliseconds(5),
                    BrokerRetcode: "DONE",
                    Message: null,
                    SafeToRetry: false));
        }

        public Task<BrokerExecutionResponse> ModifyAsync(
            PositionCommand command,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<BrokerExecutionResponse> CloseAsync(
            PositionCommand command,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<BrokerReconciliationSnapshot> QueryStateAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueryCount++;
            return Task.FromResult(_reconciliation);
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
