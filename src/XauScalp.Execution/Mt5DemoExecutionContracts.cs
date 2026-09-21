using XauScalp.Domain;

namespace XauScalp.Execution;

public static class Mt5DemoExecutionProtocol
{
    public const string Version = "xau-mt5-demo-exec-v1";
    public const string SubmitOperation = "submit";
    public const string ModifyOperation = "modify";
    public const string CloseOperation = "close";
    public const string QueryStateOperation = "queryState";

    public const string ExecutionResultType = "executionResult";
    public const string StateType = "state";
    public const string BridgeReadyType = "bridgeReady";
    public const string BridgeHeartbeatType = "bridgeHeartbeat";
    public const string DispatchingType = "dispatching";
}

public sealed record Mt5DemoExecutionGatewayOptions
{
    public Mt5DemoExecutionGatewayOptions(
        string commandFilePath,
        string eventFilePath,
        string canonicalSymbol,
        string brokerSymbol,
        PositionOwnership ownership,
        int maxSlippagePoints = 30,
        TimeSpan? commandTimeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? maxHeartbeatAge = null)
    {
        if (string.IsNullOrWhiteSpace(commandFilePath))
        {
            throw new ArgumentException("MT5 command file path is required.", nameof(commandFilePath));
        }

        if (string.IsNullOrWhiteSpace(eventFilePath))
        {
            throw new ArgumentException("MT5 event file path is required.", nameof(eventFilePath));
        }

        string commands = Path.GetFullPath(commandFilePath);
        string events = Path.GetFullPath(eventFilePath);
        if (string.Equals(commands, events, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Command and event files must be different files.", nameof(eventFilePath));
        }

        if (string.IsNullOrWhiteSpace(canonicalSymbol))
        {
            throw new ArgumentException("Canonical symbol is required.", nameof(canonicalSymbol));
        }

        if (string.IsNullOrWhiteSpace(brokerSymbol))
        {
            throw new ArgumentException("Broker symbol is required.", nameof(brokerSymbol));
        }

        if (maxSlippagePoints < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSlippagePoints),
                maxSlippagePoints,
                "Maximum slippage points must be non-negative.");
        }

        TimeSpan timeout = commandTimeout ?? TimeSpan.FromSeconds(10);
        TimeSpan polling = pollInterval ?? TimeSpan.FromMilliseconds(50);
        TimeSpan heartbeatAge = maxHeartbeatAge ?? TimeSpan.FromSeconds(5);

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(commandTimeout), timeout, "Command timeout must be positive.");
        }

        if (polling <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), polling, "Poll interval must be positive.");
        }

        if (heartbeatAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHeartbeatAge), heartbeatAge, "Heartbeat age must be positive.");
        }

        CommandFilePath = commands;
        EventFilePath = events;
        CanonicalSymbol = canonicalSymbol.Trim();
        BrokerSymbol = brokerSymbol.Trim();
        Ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        MaxSlippagePoints = maxSlippagePoints;
        CommandTimeout = timeout;
        PollInterval = polling;
        MaxHeartbeatAge = heartbeatAge;
    }

    public string CommandFilePath { get; }

    public string EventFilePath { get; }

    public string CanonicalSymbol { get; }

    public string BrokerSymbol { get; }

    public PositionOwnership Ownership { get; }

    public int MaxSlippagePoints { get; }

    public TimeSpan CommandTimeout { get; }

    public TimeSpan PollInterval { get; }

    public TimeSpan MaxHeartbeatAge { get; }
}

public sealed record Mt5DemoExecutionCommand(
    string ProtocolVersion,
    Guid CommandId,
    string BridgeSessionId,
    string Operation,
    Guid? TradeIntentId,
    string BrokerSymbol,
    long MagicNumber,
    string? BrokerComment,
    string? BrokerPositionId,
    string? Side,
    decimal? VolumeLots,
    decimal? PlannedEntryPrice,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice,
    int MaxSlippagePoints,
    bool DemoOnly);

public sealed record Mt5DemoExecutionReply(
    string ProtocolVersion,
    string Type,
    Guid CommandId,
    string BridgeSessionId,
    bool DemoAccountVerified,
    string? Operation,
    Guid? TradeIntentId,
    string? Outcome,
    string? BrokerOrderId,
    string? BrokerDealId,
    string? BrokerPositionId,
    decimal? RequestedPrice,
    decimal? FillPrice,
    decimal RequestedVolumeLots,
    decimal FilledVolumeLots,
    double? SlippagePoints,
    double LatencyMs,
    string? BrokerRetcode,
    string? Message,
    bool SafeToRetry,
    Mt5DemoPositionWire[]? Positions,
    Mt5DemoOrderWire[]? Orders,
    Guid[]? ClosedTradeIntentIds,
    Mt5DemoAccountWire? Account = null,
    Mt5DemoSymbolRiskWire? SymbolRisk = null);

public sealed record Mt5DemoPositionWire(
    Guid? TradeIntentId,
    string BrokerPositionId,
    string BrokerSymbol,
    string? BrokerComment,
    string Side,
    decimal VolumeLots,
    decimal EntryPrice,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice,
    long MagicNumber,
    decimal? CurrentPrice = null,
    decimal? UnrealizedPnlMoney = null,
    long? OpenedAtUnixMs = null);

public sealed record Mt5DemoOrderWire(
    Guid? TradeIntentId,
    string BrokerOrderId,
    string BrokerSymbol,
    string? BrokerComment,
    long MagicNumber);

public sealed record Mt5DemoAccountWire(
    decimal Balance,
    decimal Equity,
    decimal FreeMargin);

public sealed record Mt5DemoSymbolRiskWire(
    decimal Point,
    decimal TickSize,
    decimal TickValue,
    decimal MinVolume,
    decimal MaxVolume,
    decimal VolumeStep,
    decimal MinStopDistance,
    decimal EstimatedMarginPerLotMoney);

public sealed record Mt5DemoBrokerContextSnapshot(
    string CanonicalSymbol,
    string BrokerSymbol,
    BrokerReconciliationSnapshot Reconciliation,
    PortfolioState Portfolio,
    Mt5DemoSymbolRiskWire SymbolRisk);

public interface IMt5DemoBrokerContextProvider
{
    Task<Mt5DemoBrokerContextSnapshot> QueryDemoContextAsync(
        CancellationToken cancellationToken);
}

public interface IMt5DemoExecutionTransport
{
    Task<Mt5DemoExecutionReply> ExchangeAsync(
        Mt5DemoExecutionCommand command,
        CancellationToken cancellationToken);
}
