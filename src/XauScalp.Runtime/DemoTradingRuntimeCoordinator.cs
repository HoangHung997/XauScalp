using XauScalp.DecisionModels;
using XauScalp.Domain;
using XauScalp.Execution;
using XauScalp.Risk;

namespace XauScalp.Runtime;

public enum DemoRuntimeOutcome
{
    NoActionablePrimaryDecision = 0,
    ReconciliationBlocked = 1,
    RiskRejected = 2,
    ExecutionAttempted = 3,
}

public sealed record DemoRuntimeEvaluation(
    DemoRuntimeOutcome Outcome,
    Guid ComparisonId,
    XauDecision? AuthoritativeDecision,
    bool StopNewTrades,
    bool UsedFallback,
    ExecutionReconciliationReport? Reconciliation,
    RiskDecision? RiskDecision,
    TradePlan? TradePlan,
    ExecutionResult? ExecutionResult);

public sealed class DemoTradingRuntimeCoordinator
{
    private readonly PrimaryShadowDecisionOrchestrator _models;
    private readonly IMt5DemoBrokerContextProvider _brokerContext;
    private readonly ExecutionEngine _execution;
    private readonly IOwnedRiskLedger _riskLedger;
    private readonly RiskPolicyConfiguration _riskPolicy;
    private readonly XauScalpSettings _settings;
    private readonly ITradePlanner _tradePlanner;
    private readonly TimeProvider _timeProvider;

    public DemoTradingRuntimeCoordinator(
        PrimaryShadowDecisionOrchestrator models,
        IMt5DemoBrokerContextProvider brokerContext,
        ExecutionEngine execution,
        IOwnedRiskLedger riskLedger,
        RiskPolicyConfiguration riskPolicy,
        XauScalpSettings settings,
        ITradePlanner tradePlanner,
        TimeProvider? timeProvider = null)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _brokerContext = brokerContext
            ?? throw new ArgumentNullException(nameof(brokerContext));
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _riskLedger = riskLedger ?? throw new ArgumentNullException(nameof(riskLedger));
        _riskPolicy = riskPolicy ?? throw new ArgumentNullException(nameof(riskPolicy));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _tradePlanner = tradePlanner
            ?? throw new ArgumentNullException(nameof(tradePlanner));
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (!string.Equals(
                tradePlanner.SettingsVersion,
                settings.SettingsVersion,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Trade planner settings version must match runtime settings.",
                nameof(tradePlanner));
        }
    }

    public async Task<DemoRuntimeEvaluation> EvaluateStateAsync(
        XauMarketState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        PrimaryShadowEvaluationResult modelResult = await _models
            .EvaluateAsync(
                state,
                _settings.DecisionModels,
                cancellationToken)
            .ConfigureAwait(false);

        XauDecision? authoritative = modelResult.AuthoritativeDecision;
        if (modelResult.StopNewTrades
            || authoritative is null
            || authoritative.Action is not TradeAction.Long and not TradeAction.Short)
        {
            return new DemoRuntimeEvaluation(
                DemoRuntimeOutcome.NoActionablePrimaryDecision,
                modelResult.ComparisonId,
                authoritative,
                modelResult.StopNewTrades,
                modelResult.UsedFallback,
                Reconciliation: null,
                RiskDecision: null,
                TradePlan: null,
                ExecutionResult: null);
        }

        ExecutionReconciliationReport reconciliation = await _execution
            .ReconcileAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!reconciliation.IsSafeForNewEntry)
        {
            return new DemoRuntimeEvaluation(
                DemoRuntimeOutcome.ReconciliationBlocked,
                modelResult.ComparisonId,
                authoritative,
                StopNewTrades: true,
                modelResult.UsedFallback,
                reconciliation,
                RiskDecision: null,
                TradePlan: null,
                ExecutionResult: null);
        }

        Mt5DemoBrokerContextSnapshot brokerContext = await _brokerContext
            .QueryDemoContextAsync(cancellationToken)
            .ConfigureAwait(false);

        EnsureStateMatchesBrokerContext(state, brokerContext);

        var profile = new RiskSymbolProfile(
            state.Symbol,
            state.BrokerSymbol,
            brokerContext.SymbolRisk.Point,
            brokerContext.SymbolRisk.TickSize,
            brokerContext.SymbolRisk.TickValue,
            brokerContext.SymbolRisk.MinVolume,
            brokerContext.SymbolRisk.MaxVolume,
            brokerContext.SymbolRisk.VolumeStep,
            brokerContext.SymbolRisk.MinStopDistance,
            brokerContext.SymbolRisk.EstimatedMarginPerLotMoney);

        var riskEngine = new HardRiskEngine(
            new FixedRiskRuntimeContextProvider(profile),
            _riskLedger,
            _riskPolicy,
            _timeProvider);

        RiskDecision riskDecision = riskEngine.Evaluate(
            state,
            authoritative,
            brokerContext.Portfolio,
            _settings.Risk);

        if (riskDecision.Outcome != RiskDecisionOutcome.Authorized)
        {
            return new DemoRuntimeEvaluation(
                DemoRuntimeOutcome.RiskRejected,
                modelResult.ComparisonId,
                authoritative,
                StopNewTrades: false,
                modelResult.UsedFallback,
                reconciliation,
                riskDecision,
                TradePlan: null,
                ExecutionResult: null);
        }

        TradePlan plan = _tradePlanner.Create(
            state,
            authoritative,
            riskDecision);

        ExecutionResult executionResult = await _execution
            .SubmitAsync(plan, cancellationToken)
            .ConfigureAwait(false);

        return new DemoRuntimeEvaluation(
            DemoRuntimeOutcome.ExecutionAttempted,
            modelResult.ComparisonId,
            authoritative,
            StopNewTrades: false,
            modelResult.UsedFallback,
            reconciliation,
            riskDecision,
            plan,
            executionResult);
    }

    private static void EnsureStateMatchesBrokerContext(
        XauMarketState state,
        Mt5DemoBrokerContextSnapshot brokerContext)
    {
        if (!string.Equals(
                state.Symbol,
                brokerContext.CanonicalSymbol,
                StringComparison.Ordinal)
            || !string.Equals(
                state.BrokerSymbol,
                brokerContext.BrokerSymbol,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Market state does not match the configured MT5 demo broker context.");
        }
    }
}
