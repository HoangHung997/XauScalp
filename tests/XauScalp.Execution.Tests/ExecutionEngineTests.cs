using XauScalp.Domain;
using XauScalp.Persistence;

namespace XauScalp.Execution.Tests;

public sealed class ExecutionEngineTests
{
    private static readonly PositionOwnership Ownership =
        new(991188, "runtime-1", "xauscalp");

    [Fact]
    public async Task FilledSubmit_RecordsOpenWithFillSlippageLatencyAndSingleBrokerCall()
    {
        TradePlan plan = Plan();
        var broker = new FakeBroker
        {
            SubmitHandler = (_, _, _) => Task.FromResult(
                Filled(
                    plan,
                    fillPrice: 100.25m,
                    slippagePoints: 5,
                    latency: TimeSpan.FromMilliseconds(42))),
        };

        var journal = new InMemoryExecutionJournal();
        ExecutionEngine engine = Engine(broker, journal);

        ExecutionResult result = await engine.SubmitAsync(
            plan,
            CancellationToken.None);

        Assert.Equal(OrderLifecycleState.Open, result.State);
        Assert.Equal(100.25m, result.FillPrice);
        Assert.Equal(5, result.SlippagePoints);
        Assert.Equal(TimeSpan.FromMilliseconds(42), result.Latency);
        Assert.Equal(1, broker.SubmitCalls);

        ExecutionLifecycleSnapshot? snapshot = await journal.GetAsync(
            plan.TradeIntentId,
            CancellationToken.None);
        Assert.NotNull(snapshot);

        Assert.Equal(OrderLifecycleState.Open, snapshot!.EffectiveState);
        Assert.Equal("position-1", snapshot.BrokerPositionId);
        Assert.Contains(
            snapshot.History,
            item => item.EffectiveState == OrderLifecycleState.Filled);
    }

    [Fact]
    public async Task ExplicitSafeRequote_RetriesWithSameTradeIntentWithoutDuplicateIdentity()
    {
        TradePlan plan = Plan();
        var broker = new FakeBroker();
        broker.SubmitResponses.Enqueue(
            new BrokerExecutionResponse(
                Outcome: BrokerExecutionOutcome.Requote,
                BrokerOrderId: null,
                BrokerDealId: null,
                BrokerPositionId: null,
                RequestedPrice: plan.PlannedEntryPrice,
                FillPrice: null,
                RequestedVolumeLots: plan.VolumeLots,
                FilledVolumeLots: 0,
                SlippagePoints: null,
                Latency: TimeSpan.FromMilliseconds(5),
                BrokerRetcode: "REQUOTE",
                Message: "price changed",
                SafeToRetry: true));
        broker.SubmitResponses.Enqueue(Filled(plan));

        ExecutionResult result = await Engine(
            broker,
            new InMemoryExecutionJournal(),
            maxSafeRetries: 1).SubmitAsync(
                plan,
                CancellationToken.None);

        Assert.Equal(OrderLifecycleState.Open, result.State);
        Assert.Equal(2, broker.SubmitCalls);
        Assert.All(
            broker.SubmittedTradeIntentIds,
            id => Assert.Equal(plan.TradeIntentId, id));
    }

    [Fact]
    public async Task TimeoutAfterBrokerAccepted_ReconcilesAndNeverResubmits()
    {
        TradePlan plan = Plan();
        var broker = new FakeBroker
        {
            SubmitHandler = (_, _, _) =>
                throw new BrokerOutcomeUnknownException("timeout after send"),
        };

        broker.Snapshot = Snapshot(
            positions:
            [
                BrokerPosition(
                    plan,
                    brokerPositionId: "position-1",
                    Ownership),
            ]);

        var journal = new InMemoryExecutionJournal();
        ExecutionEngine engine = Engine(broker, journal);

        ExecutionResult first = await engine.SubmitAsync(
            plan,
            CancellationToken.None);

        Assert.Equal(OrderLifecycleState.Open, first.State);
        Assert.Equal(1, broker.SubmitCalls);
        Assert.True(broker.QueryCalls >= 1);

        ExecutionResult second = await engine.SubmitAsync(
            plan,
            CancellationToken.None);

        Assert.Equal(OrderLifecycleState.Open, second.State);
        Assert.Equal(1, broker.SubmitCalls);
    }

    [Fact]
    public async Task UnknownWithoutBrokerEvidence_RemainsUnknownAndSecondSubmitStillDoesNotResend()
    {
        TradePlan plan = Plan();
        var broker = new FakeBroker
        {
            SubmitHandler = (_, _, _) =>
                throw new BrokerOutcomeUnknownException("transport lost"),
            Snapshot = Snapshot(),
        };

        ExecutionEngine engine = Engine(
            broker,
            new InMemoryExecutionJournal());

        ExecutionResult first = await engine.SubmitAsync(
            plan,
            CancellationToken.None);
        ExecutionResult second = await engine.SubmitAsync(
            plan,
            CancellationToken.None);

        Assert.Equal(OrderLifecycleState.UnknownNeedsReconciliation, first.State);
        Assert.Equal(OrderLifecycleState.UnknownNeedsReconciliation, second.State);
        Assert.Equal(1, broker.SubmitCalls);
        Assert.True(broker.QueryCalls >= 2);
    }

    [Fact]
    public async Task PartialFill_IsPersistedWithoutPretendingPositionIsFullyOpen()
    {
        TradePlan plan = Plan();
        var broker = new FakeBroker
        {
            SubmitHandler = (_, _, _) => Task.FromResult(
                new BrokerExecutionResponse(
                    Outcome: BrokerExecutionOutcome.PartiallyFilled,
                    BrokerOrderId: "order-1",
                    BrokerDealId: "deal-1",
                    BrokerPositionId: "position-1",
                    RequestedPrice: plan.PlannedEntryPrice,
                    FillPrice: 100.2m,
                    RequestedVolumeLots: plan.VolumeLots,
                    FilledVolumeLots: plan.VolumeLots / 2m,
                    SlippagePoints: 2,
                    Latency: TimeSpan.FromMilliseconds(20),
                    BrokerRetcode: "PARTIAL",
                    Message: null,
                    SafeToRetry: false)),
        };

        var journal = new InMemoryExecutionJournal();
        ExecutionResult result = await Engine(broker, journal).SubmitAsync(
            plan,
            CancellationToken.None);

        Assert.Equal(OrderLifecycleState.PartiallyFilled, result.State);

        ExecutionLifecycleSnapshot? snapshot = await journal.GetAsync(
            plan.TradeIntentId,
            CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal(
            OrderLifecycleState.PartiallyFilled,
            snapshot!.EffectiveState);
    }

    [Fact]
    public async Task ModifyReject_ReturnsFailedButPositionRemainsEffectivelyOpen()
    {
        TradePlan plan = Plan();
        var broker = new FakeBroker
        {
            SubmitHandler = (_, _, _) => Task.FromResult(Filled(plan)),
            ModifyHandler = (_, _) => Task.FromResult(
                Rejected(plan, "MODIFY_REJECTED")),
        };

        var journal = new InMemoryExecutionJournal();
        ExecutionEngine engine = Engine(broker, journal);
        _ = await engine.SubmitAsync(
            plan,
            CancellationToken.None);

        ExecutionLifecycleSnapshot? openSnapshot = await journal.GetAsync(
            plan.TradeIntentId,
            CancellationToken.None);
        Assert.NotNull(openSnapshot);
        Assert.Equal("position-1", openSnapshot!.BrokerPositionId);

        PositionCommand command = Command(
            plan,
            brokerPositionId: openSnapshot.BrokerPositionId!);

        ExecutionResult modify = await engine.ModifyAsync(
            command,
            CancellationToken.None);

        Assert.Equal(OrderLifecycleState.Failed, modify.State);

        ExecutionLifecycleSnapshot? snapshot = await journal.GetAsync(
            plan.TradeIntentId,
            CancellationToken.None);
        Assert.NotNull(snapshot);

        Assert.Equal(OrderLifecycleState.Open, snapshot!.EffectiveState);
    }

    [Fact]
    public async Task CloseReject_ReturnsFailedButPositionRemainsEffectivelyOpen()
    {
        TradePlan plan = Plan();
        var broker = new FakeBroker
        {
            SubmitHandler = (_, _, _) => Task.FromResult(Filled(plan)),
            CloseHandler = (_, _) => Task.FromResult(
                Rejected(plan, "CLOSE_REJECTED")),
        };

        var journal = new InMemoryExecutionJournal();
        ExecutionEngine engine = Engine(broker, journal);
        _ = await engine.SubmitAsync(
            plan,
            CancellationToken.None);

        ExecutionLifecycleSnapshot? openSnapshot = await journal.GetAsync(
            plan.TradeIntentId,
            CancellationToken.None);
        Assert.NotNull(openSnapshot);
        Assert.Equal("position-1", openSnapshot!.BrokerPositionId);

        ExecutionResult close = await engine.CloseAsync(
            Command(plan, openSnapshot.BrokerPositionId!),
            CancellationToken.None);

        Assert.Equal(OrderLifecycleState.Failed, close.State);

        ExecutionLifecycleSnapshot? snapshot = await journal.GetAsync(
            plan.TradeIntentId,
            CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal(OrderLifecycleState.Open, snapshot!.EffectiveState);
    }

    [Fact]
    public async Task DurableJournalRestart_ReconcilesOpenPositionWithoutDuplicateSubmit()
    {
        TradePlan plan = Plan();
        string path = Path.Combine(
            Path.GetTempPath(),
            "xauscalp-execution-tests",
            Guid.NewGuid().ToString("N"),
            "journal.jsonl");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            var firstBroker = new FakeBroker
            {
                SubmitHandler = (_, _, _) => Task.FromResult(Filled(plan)),
            };

            await using (var firstJournal = new ExecutionJournalJsonlStore(path))
            {
                ExecutionEngine firstEngine = Engine(
                    firstBroker,
                    firstJournal);

                _ = await firstEngine.SubmitAsync(
                    plan,
                    CancellationToken.None);
            }

            var restartBroker = new FakeBroker
            {
                Snapshot = Snapshot(
                    positions:
                    [
                        BrokerPosition(
                            plan,
                            "position-1",
                            Ownership),
                    ]),
            };

            await using var restartedJournal =
                new ExecutionJournalJsonlStore(path);

            ExecutionEngine restarted = Engine(
                restartBroker,
                restartedJournal);

            ExecutionReconciliationReport report = await restarted
                .ReconcileAsync(CancellationToken.None);

            Assert.True(report.IsSafeForNewEntry);

            ExecutionResult duplicate = await restarted.SubmitAsync(
                plan,
                CancellationToken.None);

            Assert.Equal(OrderLifecycleState.Open, duplicate.State);
            Assert.Equal(0, restartBroker.SubmitCalls);
        }
        finally
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
    }

    [Fact]
    public async Task ReconciliationReportsLocalAndBrokerMismatchesUnsafe()
    {
        TradePlan localPlan = Plan();
        var journal = new InMemoryExecutionJournal();
        var firstBroker = new FakeBroker
        {
            SubmitHandler = (_, _, _) => Task.FromResult(Filled(localPlan)),
        };

        _ = await Engine(firstBroker, journal).SubmitAsync(
            localPlan,
            CancellationToken.None);

        TradePlan orphanPlan = Plan(Guid.NewGuid());
        var reconcileBroker = new FakeBroker
        {
            Snapshot = Snapshot(
                positions:
                [
                    BrokerPosition(
                        orphanPlan,
                        "orphan-position",
                        Ownership),
                ]),
        };

        ExecutionReconciliationReport report = await Engine(
            reconcileBroker,
            journal).ReconcileAsync(
                CancellationToken.None);

        Assert.False(report.IsSafeForNewEntry);
        Assert.Contains(
            report.Issues,
            issue => issue.Kind
                == ReconciliationIssueKind.LocalTradeMissingAtBroker);
        Assert.Contains(
            report.Issues,
            issue => issue.Kind
                == ReconciliationIssueKind.BrokerPositionMissingLocally);
    }

    [Fact]
    public async Task ForeignBrokerPosition_IsIgnoredByOwnershipReconciliation()
    {
        var broker = new FakeBroker
        {
            Snapshot = Snapshot(
                positions:
                [
                    BrokerPosition(
                        Plan(Guid.NewGuid()),
                        "foreign-position",
                        new PositionOwnership(
                            777,
                            "foreign-runtime",
                            "other")),
                ]),
        };

        ExecutionReconciliationReport report = await Engine(
            broker,
            new InMemoryExecutionJournal()).ReconcileAsync(
                CancellationToken.None);

        Assert.True(report.IsSafeForNewEntry);
        Assert.Empty(report.Issues);
    }

    private static ExecutionEngine Engine(
        FakeBroker broker,
        IExecutionJournal journal,
        int maxSafeRetries = 1)
    {
        return new ExecutionEngine(
            broker,
            journal,
            Ownership,
            new ExecutionEngineOptions(maxSafeRetries),
            new FixedTimeProvider(Utc(12, 0, 0)));
    }

    private static TradePlan Plan(Guid? tradeIntentId = null)
    {
        return new TradePlan(
            ContractVersions.TradePlanV1,
            tradeIntentId ?? Guid.Parse(
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
            Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"),
            Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"),
            "XAUUSD",
            "XAUUSD.G",
            TradeSide.Long,
            0.20m,
            100.20m,
            98.20m,
            105.20m,
            "risk-v1",
            "settings-v1",
            Utc(11, 59, 59));
    }

    private static PositionCommand Command(
        TradePlan plan,
        string brokerPositionId)
    {
        return new PositionCommand(
            plan.TradeIntentId,
            brokerPositionId,
            Ownership,
            plan.ProtectiveStopPrice,
            plan.TakeProfitPrice);
    }

    private static BrokerExecutionResponse Filled(
        TradePlan plan,
        decimal fillPrice = 100.20m,
        double slippagePoints = 0,
        TimeSpan? latency = null)
    {
        return new BrokerExecutionResponse(
            Outcome: BrokerExecutionOutcome.Filled,
            BrokerOrderId: "order-1",
            BrokerDealId: "deal-1",
            BrokerPositionId: "position-1",
            RequestedPrice: plan.PlannedEntryPrice,
            FillPrice: fillPrice,
            RequestedVolumeLots: plan.VolumeLots,
            FilledVolumeLots: plan.VolumeLots,
            SlippagePoints: slippagePoints,
            Latency: latency ?? TimeSpan.FromMilliseconds(10),
            BrokerRetcode: "DONE",
            Message: null,
            SafeToRetry: false);
    }

    private static BrokerExecutionResponse Rejected(
        TradePlan plan,
        string retcode)
    {
        return new BrokerExecutionResponse(
            Outcome: BrokerExecutionOutcome.Rejected,
            BrokerOrderId: null,
            BrokerDealId: null,
            BrokerPositionId: null,
            RequestedPrice: plan.PlannedEntryPrice,
            FillPrice: null,
            RequestedVolumeLots: plan.VolumeLots,
            FilledVolumeLots: 0,
            SlippagePoints: null,
            Latency: TimeSpan.FromMilliseconds(5),
            BrokerRetcode: retcode,
            Message: "rejected",
            SafeToRetry: false);
    }

    private static BrokerPositionSnapshot BrokerPosition(
        TradePlan plan,
        string brokerPositionId,
        PositionOwnership ownership)
    {
        return new BrokerPositionSnapshot(
            plan.TradeIntentId,
            brokerPositionId,
            plan.Symbol,
            plan.BrokerSymbol,
            plan.Side,
            plan.VolumeLots,
            plan.PlannedEntryPrice,
            plan.ProtectiveStopPrice,
            plan.TakeProfitPrice,
            ownership);
    }

    private static BrokerReconciliationSnapshot Snapshot(
        BrokerPositionSnapshot[]? positions = null,
        BrokerOrderSnapshot[]? orders = null,
        IReadOnlySet<Guid>? closed = null)
    {
        return new BrokerReconciliationSnapshot(
            positions ?? [],
            orders ?? [],
            closed ?? new HashSet<Guid>());
    }

    private static DateTimeOffset Utc(int hour, int minute, int second)
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

    private sealed class FakeBroker : IExecutionBrokerGateway
    {
        public Func<
            TradePlan,
            PositionOwnership,
            CancellationToken,
            Task<BrokerExecutionResponse>>?
        SubmitHandler
        { get; init; }

        public Func<
            PositionCommand,
            CancellationToken,
            Task<BrokerExecutionResponse>>?
        ModifyHandler
        { get; init; }

        public Func<
            PositionCommand,
            CancellationToken,
            Task<BrokerExecutionResponse>>?
        CloseHandler
        { get; init; }

        public Queue<BrokerExecutionResponse> SubmitResponses { get; } = new();

        public BrokerReconciliationSnapshot Snapshot { get; set; } =
            ExecutionEngineTests.Snapshot();

        public int SubmitCalls { get; private set; }

        public int QueryCalls { get; private set; }

        public List<Guid> SubmittedTradeIntentIds { get; } = [];

        public Task<BrokerExecutionResponse> SubmitAsync(
            TradePlan plan,
            PositionOwnership ownership,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubmitCalls++;
            SubmittedTradeIntentIds.Add(plan.TradeIntentId);

            if (SubmitHandler is not null)
            {
                return SubmitHandler(plan, ownership, cancellationToken);
            }

            if (SubmitResponses.Count > 0)
            {
                return Task.FromResult(SubmitResponses.Dequeue());
            }

            throw new InvalidOperationException(
                "Fake broker submit response was not configured.");
        }

        public Task<BrokerExecutionResponse> ModifyAsync(
            PositionCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ModifyHandler is not null
                ? ModifyHandler(command, cancellationToken)
                : Task.FromResult(
                    Rejected(Plan(command.TradeIntentId), "MODIFY_NOT_CONFIGURED"));
        }

        public Task<BrokerExecutionResponse> CloseAsync(
            PositionCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return CloseHandler is not null
                ? CloseHandler(command, cancellationToken)
                : Task.FromResult(
                    Rejected(Plan(command.TradeIntentId), "CLOSE_NOT_CONFIGURED"));
        }

        public Task<BrokerReconciliationSnapshot> QueryStateAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueryCalls++;
            return Task.FromResult(Snapshot);
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
