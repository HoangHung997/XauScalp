using XauScalp.Domain;

namespace XauScalp.Execution;

public enum ExecutionOperationKind
{
    Submit = 0,
    Modify = 1,
    Close = 2,
    Reconcile = 3,
}

public sealed record ExecutionJournalEvent(
    Guid EventId,
    Guid TradeIntentId,
    TradePlan Plan,
    PositionOwnership Ownership,
    ExecutionOperationKind Operation,
    OrderLifecycleState EffectiveState,
    string? BrokerPositionId,
    ExecutionResult Result,
    DateTimeOffset RecordedAtUtc);

public sealed record ExecutionLifecycleSnapshot(
    TradePlan Plan,
    PositionOwnership Ownership,
    OrderLifecycleState EffectiveState,
    string? BrokerPositionId,
    ExecutionResult LatestResult,
    IReadOnlyList<ExecutionJournalEvent> History);

public interface IExecutionJournal
{
    ValueTask AppendAsync(
        ExecutionJournalEvent journalEvent,
        CancellationToken cancellationToken);

    Task<ExecutionLifecycleSnapshot?> GetAsync(
        Guid tradeIntentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ExecutionLifecycleSnapshot>> GetAllAsync(
        CancellationToken cancellationToken);
}

public sealed class InMemoryExecutionJournal : IExecutionJournal
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, List<ExecutionJournalEvent>> _events = [];

    public ValueTask AppendAsync(
        ExecutionJournalEvent journalEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(journalEvent);

        lock (_sync)
        {
            if (!_events.TryGetValue(
                journalEvent.TradeIntentId,
                out List<ExecutionJournalEvent>? list))
            {
                list = [];
                _events.Add(journalEvent.TradeIntentId, list);
            }

            if (list.Count > 0
                && list[^1].RecordedAtUtc > journalEvent.RecordedAtUtc)
            {
                throw new InvalidDataException(
                    "Execution journal timestamps cannot regress within one trade intent.");
            }

            list.Add(journalEvent);
        }

        return ValueTask.CompletedTask;
    }

    public Task<ExecutionLifecycleSnapshot?> GetAsync(
        Guid tradeIntentId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (!_events.TryGetValue(
                tradeIntentId,
                out List<ExecutionJournalEvent>? list))
            {
                return Task.FromResult<ExecutionLifecycleSnapshot?>(null);
            }

            return Task.FromResult<ExecutionLifecycleSnapshot?>(
                MakeSnapshot(list));
        }
    }

    public Task<IReadOnlyList<ExecutionLifecycleSnapshot>> GetAllAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            IReadOnlyList<ExecutionLifecycleSnapshot> snapshots = _events
                .Values
                .Select(MakeSnapshot)
                .OrderBy(static snapshot => snapshot.Plan.CreatedAtUtc)
                .ThenBy(static snapshot => snapshot.Plan.TradeIntentId)
                .ToArray();

            return Task.FromResult(snapshots);
        }
    }

    private static ExecutionLifecycleSnapshot MakeSnapshot(
        List<ExecutionJournalEvent> events)
    {
        ExecutionJournalEvent latest = events[^1];

        string? brokerPositionId = events
            .Where(static item => !string.IsNullOrWhiteSpace(item.BrokerPositionId))
            .Select(static item => item.BrokerPositionId)
            .LastOrDefault();

        return new ExecutionLifecycleSnapshot(
            latest.Plan,
            latest.Ownership,
            latest.EffectiveState,
            brokerPositionId,
            latest.Result,
            events.ToArray());
    }
}

public sealed record ExecutionEngineOptions(int MaxSafeSubmitRetries)
{
    public ExecutionEngineOptions()
        : this(MaxSafeSubmitRetries: 1)
    {
    }
}

public enum ReconciliationIssueKind
{
    LocalTradeMissingAtBroker = 0,
    BrokerPositionMissingLocally = 1,
    BrokerOrderMissingLocally = 2,
    OwnershipMismatch = 3,
}

public sealed record ReconciliationIssue(
    ReconciliationIssueKind Kind,
    Guid? TradeIntentId,
    string Message);

public sealed record ExecutionReconciliationReport(
    bool IsSafeForNewEntry,
    IReadOnlyList<ReconciliationIssue> Issues,
    int MatchedLocalTradeIntents,
    int ImportedOrphanCount);
