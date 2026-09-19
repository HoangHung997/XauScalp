using System.Text.Json.Serialization;

namespace XauScalp.Domain;

public sealed record PositionOwnership
{
    [JsonConstructor]
    public PositionOwnership(long magicNumber, string runtimeInstanceId, string strategyId)
    {
        if (magicNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(magicNumber), magicNumber, "Magic number must be non-negative.");
        }

        MagicNumber = magicNumber;
        RuntimeInstanceId = ContractGuard.Required(runtimeInstanceId, nameof(runtimeInstanceId));
        StrategyId = ContractGuard.Required(strategyId, nameof(strategyId));
    }

    public long MagicNumber { get; }

    public string RuntimeInstanceId { get; }

    public string StrategyId { get; }
}

public sealed record PositionState
{
    [JsonConstructor]
    public PositionState(
        Guid positionId,
        Guid tradeIntentId,
        string brokerPositionId,
        string symbol,
        string brokerSymbol,
        TradeSide side,
        decimal volumeLots,
        decimal entryPrice,
        decimal currentPrice,
        decimal? stopLossPrice,
        decimal? takeProfitPrice,
        decimal unrealizedPnlMoney,
        decimal mfePrice,
        decimal maePrice,
        DateTimeOffset openedAtUtc,
        PositionOwnership ownership)
    {
        PositionId = ContractGuard.NonEmpty(positionId, nameof(positionId));
        TradeIntentId = ContractGuard.NonEmpty(tradeIntentId, nameof(tradeIntentId));
        BrokerPositionId = ContractGuard.Required(brokerPositionId, nameof(brokerPositionId));
        Symbol = ContractGuard.Required(symbol, nameof(symbol));
        BrokerSymbol = ContractGuard.Required(brokerSymbol, nameof(brokerSymbol));
        Side = side;
        VolumeLots = ContractGuard.Positive(volumeLots, nameof(volumeLots));
        EntryPrice = ContractGuard.Positive(entryPrice, nameof(entryPrice));
        CurrentPrice = ContractGuard.Positive(currentPrice, nameof(currentPrice));
        if (stopLossPrice is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stopLossPrice), stopLossPrice, "Stop-loss price must be positive when supplied.");
        }

        if (takeProfitPrice is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeProfitPrice), takeProfitPrice, "Take-profit price must be positive when supplied.");
        }

        StopLossPrice = stopLossPrice;
        TakeProfitPrice = takeProfitPrice;
        UnrealizedPnlMoney = unrealizedPnlMoney;
        MfePrice = ContractGuard.NonNegative(mfePrice, nameof(mfePrice));
        MaePrice = ContractGuard.NonNegative(maePrice, nameof(maePrice));
        OpenedAtUtc = ContractGuard.Utc(openedAtUtc, nameof(openedAtUtc));
        Ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
    }

    public Guid PositionId { get; }

    public Guid TradeIntentId { get; }

    public string BrokerPositionId { get; }

    public string Symbol { get; }

    public string BrokerSymbol { get; }

    public TradeSide Side { get; }

    public decimal VolumeLots { get; }

    public decimal EntryPrice { get; }

    public decimal CurrentPrice { get; }

    public decimal? StopLossPrice { get; }

    public decimal? TakeProfitPrice { get; }

    public decimal UnrealizedPnlMoney { get; }

    public decimal MfePrice { get; }

    public decimal MaePrice { get; }

    public DateTimeOffset OpenedAtUtc { get; }

    public PositionOwnership Ownership { get; }
}

public sealed record PortfolioState
{
    [JsonConstructor]
    public PortfolioState(
        string contractVersion,
        DateTimeOffset asOfUtc,
        decimal balance,
        decimal equity,
        decimal freeMargin,
        decimal realizedPnlToday,
        int tradesToday,
        PositionState[] positions)
    {
        ContractVersion = ContractGuard.ExactVersion(contractVersion, ContractVersions.PortfolioStateV1, nameof(contractVersion));
        AsOfUtc = ContractGuard.Utc(asOfUtc, nameof(asOfUtc));
        Balance = ContractGuard.NonNegative(balance, nameof(balance));
        Equity = ContractGuard.NonNegative(equity, nameof(equity));
        FreeMargin = freeMargin;
        RealizedPnlToday = realizedPnlToday;
        TradesToday = ContractGuard.NonNegative(tradesToday, nameof(tradesToday));
        Positions = positions?.ToArray() ?? throw new ArgumentNullException(nameof(positions));

        if (Positions.Select(static position => position.PositionId).Distinct().Count() != Positions.Length)
        {
            throw new ArgumentException("Portfolio position identifiers must be unique.", nameof(positions));
        }
    }

    public string ContractVersion { get; }

    public DateTimeOffset AsOfUtc { get; }

    public decimal Balance { get; }

    public decimal Equity { get; }

    public decimal FreeMargin { get; }

    public decimal RealizedPnlToday { get; }

    public int TradesToday { get; }

    public PositionState[] Positions { get; }
}
