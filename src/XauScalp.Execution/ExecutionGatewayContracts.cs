using XauScalp.Domain;

namespace XauScalp.Execution;

public enum BrokerExecutionOutcome
{
    Accepted = 0,
    PartiallyFilled = 1,
    Filled = 2,
    Rejected = 3,
    Requote = 4,
}

public sealed record BrokerExecutionResponse(
    BrokerExecutionOutcome Outcome,
    string? BrokerOrderId,
    string? BrokerDealId,
    string? BrokerPositionId,
    decimal? RequestedPrice,
    decimal? FillPrice,
    decimal RequestedVolumeLots,
    decimal FilledVolumeLots,
    double? SlippagePoints,
    TimeSpan Latency,
    string? BrokerRetcode,
    string? Message,
    bool SafeToRetry);

public sealed record PositionCommand(
    Guid TradeIntentId,
    string BrokerPositionId,
    PositionOwnership Ownership,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice);

public sealed record BrokerPositionSnapshot(
    Guid TradeIntentId,
    string BrokerPositionId,
    string Symbol,
    string BrokerSymbol,
    TradeSide Side,
    decimal VolumeLots,
    decimal EntryPrice,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice,
    PositionOwnership Ownership);

public sealed record BrokerOrderSnapshot(
    Guid TradeIntentId,
    string BrokerOrderId,
    string Symbol,
    string BrokerSymbol,
    PositionOwnership Ownership);

public sealed record BrokerReconciliationSnapshot(
    IReadOnlyList<BrokerPositionSnapshot> Positions,
    IReadOnlyList<BrokerOrderSnapshot> Orders,
    IReadOnlySet<Guid> ClosedTradeIntentIds);

public interface IExecutionBrokerGateway
{
    Task<BrokerExecutionResponse> SubmitAsync(
        TradePlan plan,
        PositionOwnership ownership,
        CancellationToken cancellationToken);

    Task<BrokerExecutionResponse> ModifyAsync(
        PositionCommand command,
        CancellationToken cancellationToken);

    Task<BrokerExecutionResponse> CloseAsync(
        PositionCommand command,
        CancellationToken cancellationToken);

    Task<BrokerReconciliationSnapshot> QueryStateAsync(
        CancellationToken cancellationToken);
}

public sealed class BrokerOutcomeUnknownException : IOException
{
    public BrokerOutcomeUnknownException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class BrokerSafeToRetryException : IOException
{
    public BrokerSafeToRetryException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
