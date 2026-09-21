using XauScalp.Domain;
using XauScalp.Execution;
using XauScalp.Persistence;
using XauScalp.Risk;
using XauScalp.Runtime;

namespace XauScalp.DemoRunner;

public sealed class DemoRiskLedgerSynchronizer
{
    private readonly RiskLedgerJsonlStore _riskLedger;
    private readonly PositionOwnership _ownership;
    private readonly DemoObservationJsonlStore _observations;
    private readonly TimeProvider _timeProvider;

    public DemoRiskLedgerSynchronizer(
        RiskLedgerJsonlStore riskLedger,
        PositionOwnership ownership,
        DemoObservationJsonlStore observations,
        TimeProvider? timeProvider = null)
    {
        _riskLedger = riskLedger
            ?? throw new ArgumentNullException(nameof(riskLedger));
        _ownership = ownership
            ?? throw new ArgumentNullException(nameof(ownership));
        _observations = observations
            ?? throw new ArgumentNullException(nameof(observations));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task EnsureAndSyncAsync(
        Mt5DemoBrokerContextSnapshot context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        OwnedRiskLedgerSnapshot snapshot =
            _riskLedger.GetSnapshot(now);

        if (!snapshot.IsReady)
        {
            DateOnly currentUtcDate =
                DateOnly.FromDateTime(now.UtcDateTime);

            Mt5DemoClosedTradeWire[] currentDayBrokerCloses =
                context.ClosedTrades
                .Where(
                    item => DateOnly.FromDateTime(
                        DateTimeOffset
                            .FromUnixTimeMilliseconds(
                                item.ClosedAtUnixMs)
                            .UtcDateTime)
                        == currentUtcDate)
                .ToArray();

            bool ownedBrokerStateExists =
                context.Reconciliation.Positions.Count > 0
                || context.Reconciliation.Orders.Count > 0;

            if (currentDayBrokerCloses.Length > 0
                || ownedBrokerStateExists)
            {
                throw new InvalidOperationException(
                    "Risk ledger is not initialized for the current UTC trading day, "
                    + "but owned MT5 demo trade state already exists. "
                    + "Start-equity cannot be reconstructed safely; no new trade is allowed.");
            }

            if (context.Portfolio.Equity <= 0)
            {
                throw new InvalidDataException(
                    "Cannot initialize daily risk ledger from non-positive broker equity.");
            }

            await _riskLedger.StartTradingDayAsync(
                now,
                context.Portfolio.Equity,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (Mt5DemoClosedTradeWire closed in context.ClosedTrades)
        {
            DateTimeOffset closedAt =
                DateTimeOffset.FromUnixTimeMilliseconds(
                    closed.ClosedAtUnixMs);

            bool added =
                await _riskLedger.RecordClosedTradeOnceAsync(
                    closed.TradeIntentId,
                    closedAt,
                    closed.RealizedPnlMoney,
                    closed.CommissionCostMoney,
                    _ownership,
                    cancellationToken).ConfigureAwait(false);

            if (!added)
            {
                continue;
            }

            await _observations.AppendAsync(
                new DemoObservation(
                    DemoObservationKind.RiskLedgerSync,
                    now,
                    MarketStateId: null,
                    ComparisonId: null,
                    DecisionId: null,
                    RiskDecisionId: null,
                    TradeIntentId: closed.TradeIntentId,
                    Outcome: "closed-trade-synced",
                    ReasonCode: null,
                    BrokerOrderId: null,
                    BrokerDealId: null,
                    BrokerPositionId: null,
                    ModelLatencyMs: null,
                    SlippagePoints: null,
                    Message:
                        $"pnl={closed.RealizedPnlMoney};commission={closed.CommissionCostMoney}"),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RecordExecutionFailureAsync(
        XauMarketState state,
        DemoRuntimeEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(evaluation);

        ExecutionResult? result = evaluation.ExecutionResult;
        if (result is null
            || result.State is not (
                OrderLifecycleState.Failed
                or OrderLifecycleState.UnknownNeedsReconciliation))
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        await _riskLedger.RecordExecutionFailureAsync(
            now,
            _ownership,
            cancellationToken).ConfigureAwait(false);

        await _observations.AppendAsync(
            new DemoObservation(
                DemoObservationKind.ExecutionFailureRecorded,
                now,
                state.MarketStateId,
                evaluation.ComparisonId,
                evaluation.AuthoritativeDecision?.DecisionId,
                evaluation.RiskDecision?.RiskDecisionId,
                evaluation.TradePlan?.TradeIntentId,
                result.State.ToString(),
                evaluation.RiskDecision?.ReasonCode,
                result.BrokerOrderId,
                result.BrokerDealId,
                BrokerPositionId: null,
                result.Latency.TotalMilliseconds,
                result.SlippagePoints,
                result.Message),
            cancellationToken).ConfigureAwait(false);
    }
}
