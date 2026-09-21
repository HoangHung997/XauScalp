using XauScalp.Domain;

namespace XauScalp.Execution;

public sealed class ExecutionEngine
{
    private readonly IExecutionBrokerGateway _broker;
    private readonly IExecutionJournal _journal;
    private readonly PositionOwnership _ownership;
    private readonly ExecutionEngineOptions _options;
    private readonly TimeProvider _timeProvider;

    public ExecutionEngine(
        IExecutionBrokerGateway broker,
        IExecutionJournal journal,
        PositionOwnership ownership,
        ExecutionEngineOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        _options = options ?? new ExecutionEngineOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (_options.MaxSafeSubmitRetries < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                _options.MaxSafeSubmitRetries,
                "Safe submit retries must be non-negative.");
        }
    }

    public async Task<ExecutionResult> SubmitAsync(
        TradePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePlan(plan);

        ExecutionLifecycleSnapshot? existing = await _journal
            .GetAsync(plan.TradeIntentId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            EnsureSamePlan(existing.Plan, plan);

            if (existing.EffectiveState is OrderLifecycleState.Submitting
                or OrderLifecycleState.Accepted
                or OrderLifecycleState.PartiallyFilled
                or OrderLifecycleState.Filled
                or OrderLifecycleState.Open
                or OrderLifecycleState.UnknownNeedsReconciliation)
            {
                return await ReconcileTradeIntentAsync(
                    plan.TradeIntentId,
                    cancellationToken).ConfigureAwait(false);
            }

            return existing.LatestResult;
        }

        await AppendSyntheticAsync(
            plan,
            ExecutionOperationKind.Submit,
            OrderLifecycleState.Created,
            OrderLifecycleState.Created,
            "created",
            cancellationToken).ConfigureAwait(false);

        await AppendSyntheticAsync(
            plan,
            ExecutionOperationKind.Submit,
            OrderLifecycleState.Authorized,
            OrderLifecycleState.Authorized,
            "risk-authorized-trade-plan",
            cancellationToken).ConfigureAwait(false);

        await AppendSyntheticAsync(
            plan,
            ExecutionOperationKind.Submit,
            OrderLifecycleState.Submitting,
            OrderLifecycleState.Submitting,
            "submitting",
            cancellationToken).ConfigureAwait(false);

        for (int attempt = 0; attempt <= _options.MaxSafeSubmitRetries; attempt++)
        {
            BrokerExecutionResponse response;

            try
            {
                response = await _broker
                    .SubmitAsync(plan, _ownership, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (BrokerSafeToRetryException exception)
            {
                if (attempt < _options.MaxSafeSubmitRetries)
                {
                    await AppendFailureAsync(
                        plan,
                        ExecutionOperationKind.Submit,
                        OrderLifecycleState.Submitting,
                        "safe-retry-pre-submit",
                        exception.Message,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return await AppendFailureAsync(
                    plan,
                    ExecutionOperationKind.Submit,
                    OrderLifecycleState.Failed,
                    "safe-retry-exhausted",
                    exception.Message,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await AppendUnknownAsync(
                    plan,
                    ExecutionOperationKind.Submit,
                    exception.Message,
                    cancellationToken).ConfigureAwait(false);

                return await ReconcileTradeIntentAsync(
                    plan.TradeIntentId,
                    cancellationToken).ConfigureAwait(false);
            }

            ExecutionResult mapped = MapBrokerResponse(plan, response);

            if (response.Outcome is BrokerExecutionOutcome.Rejected
                or BrokerExecutionOutcome.Requote)
            {
                if (response.SafeToRetry
                    && attempt < _options.MaxSafeSubmitRetries)
                {
                    await AppendAsync(
                        plan,
                        ExecutionOperationKind.Submit,
                        OrderLifecycleState.Submitting,
                        mapped,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await AppendAsync(
                    plan,
                    ExecutionOperationKind.Submit,
                    OrderLifecycleState.Failed,
                    mapped,
                    cancellationToken).ConfigureAwait(false);
                return mapped;
            }

            if (response.Outcome == BrokerExecutionOutcome.Filled)
            {
                await AppendAsync(
                    plan,
                    ExecutionOperationKind.Submit,
                    OrderLifecycleState.Filled,
                    mapped,
                    cancellationToken,
                    response.BrokerPositionId).ConfigureAwait(false);

                return await AppendOpenAsync(
                    plan,
                    mapped,
                    cancellationToken,
                    ExecutionOperationKind.Submit,
                    response.BrokerPositionId).ConfigureAwait(false);
            }

            OrderLifecycleState effective = response.Outcome switch
            {
                BrokerExecutionOutcome.Accepted => OrderLifecycleState.Accepted,
                BrokerExecutionOutcome.PartiallyFilled => OrderLifecycleState.PartiallyFilled,
                _ => throw new InvalidDataException(
                    $"Unsupported submit outcome {response.Outcome}."),
            };

            await AppendAsync(
                plan,
                ExecutionOperationKind.Submit,
                effective,
                mapped,
                cancellationToken).ConfigureAwait(false);

            return mapped;
        }

        throw new InvalidOperationException("Submit loop exited unexpectedly.");
    }

    public async Task<ExecutionResult> ModifyAsync(
        PositionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOwned(command.Ownership);

        ExecutionLifecycleSnapshot snapshot = await RequireManageableOpenAsync(
            command,
            cancellationToken).ConfigureAwait(false);

        await AppendSyntheticAsync(
            snapshot.Plan,
            ExecutionOperationKind.Modify,
            OrderLifecycleState.ModifyPending,
            snapshot.EffectiveState,
            "modify-pending",
            cancellationToken).ConfigureAwait(false);

        try
        {
            BrokerExecutionResponse response = await _broker
                .ModifyAsync(command, cancellationToken)
                .ConfigureAwait(false);

            ExecutionResult mapped = MapBrokerResponse(snapshot.Plan, response);

            if (response.Outcome is BrokerExecutionOutcome.Rejected
                or BrokerExecutionOutcome.Requote)
            {
                await AppendAsync(
                    snapshot.Plan,
                    ExecutionOperationKind.Modify,
                    OrderLifecycleState.Open,
                    mapped,
                    cancellationToken).ConfigureAwait(false);
                return mapped;
            }

            return await AppendOpenAsync(
                snapshot.Plan,
                mapped,
                cancellationToken,
                ExecutionOperationKind.Modify,
                snapshot.BrokerPositionId).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await AppendUnknownAsync(
                snapshot.Plan,
                ExecutionOperationKind.Modify,
                exception.Message,
                cancellationToken).ConfigureAwait(false);

            return await ReconcileTradeIntentAsync(
                command.TradeIntentId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ExecutionResult> CloseAsync(
        PositionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOwned(command.Ownership);

        ExecutionLifecycleSnapshot snapshot = await RequireManageableOpenAsync(
            command,
            cancellationToken).ConfigureAwait(false);

        await AppendSyntheticAsync(
            snapshot.Plan,
            ExecutionOperationKind.Close,
            OrderLifecycleState.ClosePending,
            snapshot.EffectiveState,
            "close-pending",
            cancellationToken).ConfigureAwait(false);

        try
        {
            BrokerExecutionResponse response = await _broker
                .CloseAsync(command, cancellationToken)
                .ConfigureAwait(false);

            ExecutionResult mapped = MapBrokerResponse(snapshot.Plan, response);

            if (response.Outcome is BrokerExecutionOutcome.Rejected
                or BrokerExecutionOutcome.Requote)
            {
                await AppendAsync(
                    snapshot.Plan,
                    ExecutionOperationKind.Close,
                    OrderLifecycleState.Open,
                    mapped,
                    cancellationToken).ConfigureAwait(false);
                return mapped;
            }

            if (response.Outcome is BrokerExecutionOutcome.Accepted
                or BrokerExecutionOutcome.PartiallyFilled)
            {
                await AppendAsync(
                    snapshot.Plan,
                    ExecutionOperationKind.Close,
                    OrderLifecycleState.ClosePending,
                    mapped,
                    cancellationToken,
                    snapshot.BrokerPositionId).ConfigureAwait(false);

                return mapped;
            }

            ExecutionResult closed = CopyWithState(
                mapped,
                OrderLifecycleState.Closed);

            await AppendAsync(
                snapshot.Plan,
                ExecutionOperationKind.Close,
                OrderLifecycleState.Closed,
                closed,
                cancellationToken,
                snapshot.BrokerPositionId).ConfigureAwait(false);

            return closed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await AppendUnknownAsync(
                snapshot.Plan,
                ExecutionOperationKind.Close,
                exception.Message,
                cancellationToken).ConfigureAwait(false);

            return await ReconcileTradeIntentAsync(
                command.TradeIntentId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ExecutionReconciliationReport> ReconcileAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        BrokerReconciliationSnapshot broker = await _broker
            .QueryStateAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<ExecutionLifecycleSnapshot> locals = await _journal
            .GetAllAsync(cancellationToken)
            .ConfigureAwait(false);

        var issues = new List<ReconciliationIssue>();
        var localIds = locals
            .Select(static snapshot => snapshot.Plan.TradeIntentId)
            .ToHashSet();

        int matched = 0;

        foreach (ExecutionLifecycleSnapshot local in locals)
        {
            bool resolved = await ReconcileOneAsync(
                local,
                broker,
                cancellationToken).ConfigureAwait(false);

            if (resolved)
            {
                matched++;
                continue;
            }

            if (local.EffectiveState is OrderLifecycleState.Submitting
                or OrderLifecycleState.Accepted
                or OrderLifecycleState.PartiallyFilled
                or OrderLifecycleState.Filled
                or OrderLifecycleState.Open
                or OrderLifecycleState.ModifyPending
                or OrderLifecycleState.ClosePending
                or OrderLifecycleState.UnknownNeedsReconciliation)
            {
                issues.Add(
                    new ReconciliationIssue(
                        ReconciliationIssueKind.LocalTradeMissingAtBroker,
                        local.Plan.TradeIntentId,
                        "Local active/uncertain trade has no conclusive broker position/order/closed evidence."));
            }
        }

        int orphans = 0;

        foreach (BrokerPositionSnapshot position in broker.Positions)
        {
            if (!IsOwned(position.Ownership))
            {
                continue;
            }

            if (!localIds.Contains(position.TradeIntentId))
            {
                orphans++;
                issues.Add(
                    new ReconciliationIssue(
                        ReconciliationIssueKind.BrokerPositionMissingLocally,
                        position.TradeIntentId,
                        $"Owned broker position {position.BrokerPositionId} is not represented locally."));
            }
        }

        foreach (BrokerOrderSnapshot order in broker.Orders)
        {
            if (!IsOwned(order.Ownership))
            {
                continue;
            }

            if (!localIds.Contains(order.TradeIntentId))
            {
                orphans++;
                issues.Add(
                    new ReconciliationIssue(
                        ReconciliationIssueKind.BrokerOrderMissingLocally,
                        order.TradeIntentId,
                        $"Owned broker order {order.BrokerOrderId} is not represented locally."));
            }
        }

        return new ExecutionReconciliationReport(
            IsSafeForNewEntry: issues.Count == 0,
            issues,
            matched,
            orphans);
    }

    public async Task<ExecutionResult> ReconcileTradeIntentAsync(
        Guid tradeIntentId,
        CancellationToken cancellationToken)
    {
        ExecutionLifecycleSnapshot snapshot = await _journal
            .GetAsync(tradeIntentId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"Trade intent {tradeIntentId} is not present in the execution journal.");

        BrokerReconciliationSnapshot broker = await _broker
            .QueryStateAsync(cancellationToken)
            .ConfigureAwait(false);

        bool resolved = await ReconcileOneAsync(
            snapshot,
            broker,
            cancellationToken).ConfigureAwait(false);

        if (!resolved
            && snapshot.EffectiveState != OrderLifecycleState.UnknownNeedsReconciliation)
        {
            return await AppendUnknownAsync(
                snapshot.Plan,
                ExecutionOperationKind.Reconcile,
                "Broker state is not conclusive for this trade intent.",
                cancellationToken).ConfigureAwait(false);
        }

        ExecutionLifecycleSnapshot latest = await _journal
            .GetAsync(tradeIntentId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Execution journal lost a trade intent during reconciliation.");

        return latest.LatestResult;
    }

    private async Task<bool> ReconcileOneAsync(
        ExecutionLifecycleSnapshot local,
        BrokerReconciliationSnapshot broker,
        CancellationToken cancellationToken)
    {
        BrokerPositionSnapshot? position = broker.Positions.FirstOrDefault(
            item => item.TradeIntentId == local.Plan.TradeIntentId
                && IsOwned(item.Ownership));

        if (position is not null)
        {
            ExecutionResult open = new(
                ContractVersions.ExecutionResultV1,
                Guid.NewGuid(),
                local.Plan.TradeIntentId,
                OrderLifecycleState.Open,
                brokerOrderId: local.LatestResult.BrokerOrderId,
                brokerDealId: local.LatestResult.BrokerDealId,
                requestedPrice: local.Plan.PlannedEntryPrice,
                fillPrice: position.EntryPrice,
                requestedVolumeLots: local.Plan.VolumeLots,
                filledVolumeLots: position.VolumeLots,
                slippagePoints: local.LatestResult.SlippagePoints,
                latency: TimeSpan.Zero,
                brokerRetcode: "RECONCILED_POSITION",
                message: position.BrokerPositionId,
                _timeProvider.GetUtcNow());

            await AppendAsync(
                local.Plan,
                ExecutionOperationKind.Reconcile,
                OrderLifecycleState.Open,
                open,
                cancellationToken,
                position.BrokerPositionId).ConfigureAwait(false);

            return true;
        }

        BrokerOrderSnapshot? order = broker.Orders.FirstOrDefault(
            item => item.TradeIntentId == local.Plan.TradeIntentId
                && IsOwned(item.Ownership));

        if (order is not null)
        {
            ExecutionResult accepted = new(
                ContractVersions.ExecutionResultV1,
                Guid.NewGuid(),
                local.Plan.TradeIntentId,
                OrderLifecycleState.Accepted,
                order.BrokerOrderId,
                brokerDealId: null,
                requestedPrice: local.Plan.PlannedEntryPrice,
                fillPrice: null,
                local.Plan.VolumeLots,
                filledVolumeLots: 0,
                slippagePoints: null,
                latency: TimeSpan.Zero,
                brokerRetcode: "RECONCILED_ORDER",
                message: null,
                _timeProvider.GetUtcNow());

            await AppendAsync(
                local.Plan,
                ExecutionOperationKind.Reconcile,
                OrderLifecycleState.Accepted,
                accepted,
                cancellationToken).ConfigureAwait(false);

            return true;
        }

        if (broker.ClosedTradeIntentIds.Contains(local.Plan.TradeIntentId))
        {
            ExecutionResult closed = new(
                ContractVersions.ExecutionResultV1,
                Guid.NewGuid(),
                local.Plan.TradeIntentId,
                OrderLifecycleState.Closed,
                local.LatestResult.BrokerOrderId,
                local.LatestResult.BrokerDealId,
                local.LatestResult.RequestedPrice,
                local.LatestResult.FillPrice,
                local.Plan.VolumeLots,
                local.LatestResult.FilledVolumeLots,
                local.LatestResult.SlippagePoints,
                TimeSpan.Zero,
                "RECONCILED_CLOSED",
                null,
                _timeProvider.GetUtcNow());

            await AppendAsync(
                local.Plan,
                ExecutionOperationKind.Reconcile,
                OrderLifecycleState.Closed,
                closed,
                cancellationToken).ConfigureAwait(false);

            return true;
        }

        return false;
    }

    private async Task<ExecutionLifecycleSnapshot> RequireManageableOpenAsync(
        PositionCommand command,
        CancellationToken cancellationToken)
    {
        ExecutionLifecycleSnapshot snapshot = await _journal
            .GetAsync(command.TradeIntentId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"Trade intent {command.TradeIntentId} is not present locally.");

        if (snapshot.EffectiveState != OrderLifecycleState.Open)
        {
            throw new InvalidOperationException(
                "Position modification/close requires reconciled Open state.");
        }

        string? localBrokerPositionId = snapshot.BrokerPositionId;
        if (!string.IsNullOrWhiteSpace(localBrokerPositionId)
            && !string.Equals(
                localBrokerPositionId,
                command.BrokerPositionId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Broker position identity does not match the reconciled local position.");
        }

        return snapshot;
    }

    private async Task<ExecutionResult> AppendOpenAsync(
        TradePlan plan,
        ExecutionResult source,
        CancellationToken cancellationToken,
        ExecutionOperationKind operation = ExecutionOperationKind.Submit,
        string? brokerPositionId = null)
    {
        ExecutionResult open = CopyWithState(
            source,
            OrderLifecycleState.Open);

        await AppendAsync(
            plan,
            operation,
            OrderLifecycleState.Open,
            open,
            cancellationToken,
            brokerPositionId).ConfigureAwait(false);

        return open;
    }

    private async Task<ExecutionResult> AppendFailureAsync(
        TradePlan plan,
        ExecutionOperationKind operation,
        OrderLifecycleState effectiveState,
        string retcode,
        string message,
        CancellationToken cancellationToken)
    {
        ExecutionResult failed = new(
            ContractVersions.ExecutionResultV1,
            Guid.NewGuid(),
            plan.TradeIntentId,
            OrderLifecycleState.Failed,
            brokerOrderId: null,
            brokerDealId: null,
            requestedPrice: plan.PlannedEntryPrice,
            fillPrice: null,
            requestedVolumeLots: plan.VolumeLots,
            filledVolumeLots: 0,
            slippagePoints: null,
            latency: TimeSpan.Zero,
            retcode,
            message,
            _timeProvider.GetUtcNow());

        await AppendAsync(
            plan,
            operation,
            effectiveState,
            failed,
            cancellationToken).ConfigureAwait(false);

        return failed;
    }

    private Task<ExecutionResult> AppendUnknownAsync(
        TradePlan plan,
        ExecutionOperationKind operation,
        string message,
        CancellationToken cancellationToken)
    {
        return AppendSyntheticAsync(
            plan,
            operation,
            OrderLifecycleState.UnknownNeedsReconciliation,
            OrderLifecycleState.UnknownNeedsReconciliation,
            message,
            cancellationToken);
    }

    private async Task<ExecutionResult> AppendSyntheticAsync(
        TradePlan plan,
        ExecutionOperationKind operation,
        OrderLifecycleState resultState,
        OrderLifecycleState effectiveState,
        string message,
        CancellationToken cancellationToken)
    {
        ExecutionResult result = new(
            ContractVersions.ExecutionResultV1,
            Guid.NewGuid(),
            plan.TradeIntentId,
            resultState,
            brokerOrderId: null,
            brokerDealId: null,
            requestedPrice: plan.PlannedEntryPrice,
            fillPrice: null,
            plan.VolumeLots,
            filledVolumeLots: 0,
            slippagePoints: null,
            TimeSpan.Zero,
            brokerRetcode: resultState.ToString(),
            message,
            _timeProvider.GetUtcNow());

        await AppendAsync(
            plan,
            operation,
            effectiveState,
            result,
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    private async Task AppendAsync(
        TradePlan plan,
        ExecutionOperationKind operation,
        OrderLifecycleState effectiveState,
        ExecutionResult result,
        CancellationToken cancellationToken,
        string? brokerPositionId = null)
    {
        await _journal
            .AppendAsync(
                new ExecutionJournalEvent(
                    Guid.NewGuid(),
                    plan.TradeIntentId,
                    plan,
                    _ownership,
                    operation,
                    effectiveState,
                    brokerPositionId,
                    result,
                    _timeProvider.GetUtcNow()),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private ExecutionResult CopyWithState(
        ExecutionResult source,
        OrderLifecycleState state)
    {
        return new ExecutionResult(
            ContractVersions.ExecutionResultV1,
            Guid.NewGuid(),
            source.TradeIntentId,
            state,
            source.BrokerOrderId,
            source.BrokerDealId,
            source.RequestedPrice,
            source.FillPrice,
            source.RequestedVolumeLots,
            source.FilledVolumeLots,
            source.SlippagePoints,
            source.Latency,
            source.BrokerRetcode,
            source.Message,
            _timeProvider.GetUtcNow());
    }

    private ExecutionResult MapBrokerResponse(
        TradePlan plan,
        BrokerExecutionResponse response)
    {
        OrderLifecycleState state = response.Outcome switch
        {
            BrokerExecutionOutcome.Accepted => OrderLifecycleState.Accepted,
            BrokerExecutionOutcome.PartiallyFilled => OrderLifecycleState.PartiallyFilled,
            BrokerExecutionOutcome.Filled => OrderLifecycleState.Filled,
            BrokerExecutionOutcome.Rejected => OrderLifecycleState.Failed,
            BrokerExecutionOutcome.Requote => OrderLifecycleState.Failed,
            _ => throw new InvalidDataException(
                $"Unsupported broker execution outcome {response.Outcome}."),
        };

        return new ExecutionResult(
            ContractVersions.ExecutionResultV1,
            Guid.NewGuid(),
            plan.TradeIntentId,
            state,
            response.BrokerOrderId,
            response.BrokerDealId,
            response.RequestedPrice ?? plan.PlannedEntryPrice,
            response.FillPrice,
            response.RequestedVolumeLots,
            response.FilledVolumeLots,
            response.SlippagePoints,
            response.Latency,
            response.BrokerRetcode,
            response.Message,
            _timeProvider.GetUtcNow());
    }

    private void ValidatePlan(TradePlan plan)
    {
        if (plan.Side == TradeSide.Long
            && plan.ProtectiveStopPrice >= plan.PlannedEntryPrice)
        {
            throw new InvalidDataException(
                "Long trade plan protective stop must be below entry.");
        }

        if (plan.Side == TradeSide.Short
            && plan.ProtectiveStopPrice <= plan.PlannedEntryPrice)
        {
            throw new InvalidDataException(
                "Short trade plan protective stop must be above entry.");
        }
    }

    private void EnsureOwned(PositionOwnership ownership)
    {
        if (!IsOwned(ownership))
        {
            throw new InvalidOperationException(
                "Execution command ownership does not match this runtime.");
        }
    }

    private bool IsOwned(PositionOwnership ownership)
    {
        return ownership.MagicNumber == _ownership.MagicNumber
            && string.Equals(
                ownership.RuntimeInstanceId,
                _ownership.RuntimeInstanceId,
                StringComparison.Ordinal)
            && string.Equals(
                ownership.StrategyId,
                _ownership.StrategyId,
                StringComparison.Ordinal);
    }

    private static void EnsureSamePlan(TradePlan existing, TradePlan supplied)
    {
        if (existing != supplied)
        {
            throw new InvalidDataException(
                "TradeIntentId is already associated with a different TradePlan.");
        }
    }
}
