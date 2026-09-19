using System.Text.Json.Serialization;

namespace XauScalp.Domain;

public enum TradeSide
{
    Long = 1,
    Short = -1,
}

public enum OrderLifecycleState
{
    Created = 0,
    RiskRejected = 1,
    Authorized = 2,
    Submitting = 3,
    Accepted = 4,
    PartiallyFilled = 5,
    Filled = 6,
    Open = 7,
    ModifyPending = 8,
    ClosePending = 9,
    Closed = 10,
    Failed = 11,
    UnknownNeedsReconciliation = 12,
}

public sealed record TradePlan
{
    [JsonConstructor]
    public TradePlan(
        string contractVersion,
        Guid tradeIntentId,
        Guid marketStateId,
        Guid decisionId,
        Guid riskDecisionId,
        string symbol,
        string brokerSymbol,
        TradeSide side,
        decimal volumeLots,
        decimal plannedEntryPrice,
        decimal protectiveStopPrice,
        decimal? takeProfitPrice,
        string riskPolicyVersion,
        string settingsVersion,
        DateTimeOffset createdAtUtc)
    {
        ContractVersion = ContractGuard.ExactVersion(contractVersion, ContractVersions.TradePlanV1, nameof(contractVersion));
        TradeIntentId = ContractGuard.NonEmpty(tradeIntentId, nameof(tradeIntentId));
        MarketStateId = ContractGuard.NonEmpty(marketStateId, nameof(marketStateId));
        DecisionId = ContractGuard.NonEmpty(decisionId, nameof(decisionId));
        RiskDecisionId = ContractGuard.NonEmpty(riskDecisionId, nameof(riskDecisionId));
        Symbol = ContractGuard.Required(symbol, nameof(symbol));
        BrokerSymbol = ContractGuard.Required(brokerSymbol, nameof(brokerSymbol));
        Side = side;
        VolumeLots = ContractGuard.Positive(volumeLots, nameof(volumeLots));
        PlannedEntryPrice = ContractGuard.Positive(plannedEntryPrice, nameof(plannedEntryPrice));
        ProtectiveStopPrice = ContractGuard.Positive(protectiveStopPrice, nameof(protectiveStopPrice));
        if (takeProfitPrice is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeProfitPrice), takeProfitPrice, "Take-profit price must be positive when supplied.");
        }

        TakeProfitPrice = takeProfitPrice;
        RiskPolicyVersion = ContractGuard.Required(riskPolicyVersion, nameof(riskPolicyVersion));
        SettingsVersion = ContractGuard.Required(settingsVersion, nameof(settingsVersion));
        CreatedAtUtc = ContractGuard.Utc(createdAtUtc, nameof(createdAtUtc));
    }

    public string ContractVersion { get; }

    public Guid TradeIntentId { get; }

    public Guid MarketStateId { get; }

    public Guid DecisionId { get; }

    public Guid RiskDecisionId { get; }

    public string Symbol { get; }

    public string BrokerSymbol { get; }

    public TradeSide Side { get; }

    public decimal VolumeLots { get; }

    public decimal PlannedEntryPrice { get; }

    public decimal ProtectiveStopPrice { get; }

    public decimal? TakeProfitPrice { get; }

    public string RiskPolicyVersion { get; }

    public string SettingsVersion { get; }

    public DateTimeOffset CreatedAtUtc { get; }
}

public sealed record ExecutionResult
{
    [JsonConstructor]
    public ExecutionResult(
        string contractVersion,
        Guid executionResultId,
        Guid tradeIntentId,
        OrderLifecycleState state,
        string? brokerOrderId,
        string? brokerDealId,
        decimal? requestedPrice,
        decimal? fillPrice,
        decimal requestedVolumeLots,
        decimal filledVolumeLots,
        double? slippagePoints,
        TimeSpan latency,
        string? brokerRetcode,
        string? message,
        DateTimeOffset occurredAtUtc)
    {
        ContractVersion = ContractGuard.ExactVersion(contractVersion, ContractVersions.ExecutionResultV1, nameof(contractVersion));
        ExecutionResultId = ContractGuard.NonEmpty(executionResultId, nameof(executionResultId));
        TradeIntentId = ContractGuard.NonEmpty(tradeIntentId, nameof(tradeIntentId));
        State = state;

        if (requestedPrice is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedPrice), requestedPrice, "Requested price must be positive when supplied.");
        }

        if (fillPrice is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fillPrice), fillPrice, "Fill price must be positive when supplied.");
        }

        RequestedPrice = requestedPrice;
        FillPrice = fillPrice;
        RequestedVolumeLots = ContractGuard.NonNegative(requestedVolumeLots, nameof(requestedVolumeLots));
        FilledVolumeLots = ContractGuard.NonNegative(filledVolumeLots, nameof(filledVolumeLots));
        if (filledVolumeLots > requestedVolumeLots)
        {
            throw new ArgumentException("Filled volume cannot exceed requested volume.", nameof(filledVolumeLots));
        }

        if (slippagePoints is not null && !double.IsFinite(slippagePoints.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(slippagePoints), slippagePoints, "Slippage must be finite when supplied.");
        }

        if (latency < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(latency), latency, "Latency must be non-negative.");
        }

        BrokerOrderId = brokerOrderId;
        BrokerDealId = brokerDealId;
        SlippagePoints = slippagePoints;
        Latency = latency;
        BrokerRetcode = brokerRetcode;
        Message = message;
        OccurredAtUtc = ContractGuard.Utc(occurredAtUtc, nameof(occurredAtUtc));
    }

    public string ContractVersion { get; }

    public Guid ExecutionResultId { get; }

    public Guid TradeIntentId { get; }

    public OrderLifecycleState State { get; }

    public string? BrokerOrderId { get; }

    public string? BrokerDealId { get; }

    public decimal? RequestedPrice { get; }

    public decimal? FillPrice { get; }

    public decimal RequestedVolumeLots { get; }

    public decimal FilledVolumeLots { get; }

    public double? SlippagePoints { get; }

    public TimeSpan Latency { get; }

    public string? BrokerRetcode { get; }

    public string? Message { get; }

    public DateTimeOffset OccurredAtUtc { get; }
}
