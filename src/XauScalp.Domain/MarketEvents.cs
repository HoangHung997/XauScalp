using System.Text.Json.Serialization;

namespace XauScalp.Domain;

[Flags]
public enum TickFlags
{
    None = 0,
    Bid = 1,
    Ask = 2,
    Last = 4,
    Volume = 8,
}

public enum MarketConnectionState
{
    Connected = 0,
    Reconnecting = 1,
    Disconnected = 2,
}

public enum FeedSequenceAnomalyKind
{
    MissingRange = 0,
    DuplicateOrOutOfOrder = 1,
}

public enum BarTimeframe
{
    M1 = 1,
    M5 = 5,
    M15 = 15,
    H1 = 60,
}

public enum BarUpdateKind
{
    Opened = 0,
    Updated = 1,
    Closed = 2,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$eventType")]
[JsonDerivedType(typeof(TickEvent), "tick")]
[JsonDerivedType(typeof(BarEvent), "bar")]
[JsonDerivedType(typeof(ConnectionStatusEvent), "connection")]
[JsonDerivedType(typeof(SymbolSpecificationEvent), "symbolSpecification")]
[JsonDerivedType(typeof(FeedGapEvent), "feedGap")]
public abstract record MarketEvent
{
    protected MarketEvent(
        string contractVersion,
        DateTimeOffset timestampUtc,
        DateTimeOffset? brokerTimestamp,
        long sequenceId,
        string dataSourceId,
        string symbol,
        string brokerSymbol)
    {
        ContractVersion = ContractGuard.ExactVersion(contractVersion, ContractVersions.MarketEventV1, nameof(contractVersion));
        TimestampUtc = ContractGuard.Utc(timestampUtc, nameof(timestampUtc));
        BrokerTimestamp = brokerTimestamp;
        SequenceId = ContractGuard.NonNegative(sequenceId, nameof(sequenceId));
        DataSourceId = ContractGuard.Required(dataSourceId, nameof(dataSourceId));
        Symbol = ContractGuard.Required(symbol, nameof(symbol));
        BrokerSymbol = ContractGuard.Required(brokerSymbol, nameof(brokerSymbol));
    }

    public string ContractVersion { get; }

    public DateTimeOffset TimestampUtc { get; }

    public DateTimeOffset? BrokerTimestamp { get; }

    public long SequenceId { get; }

    public string DataSourceId { get; }

    public string Symbol { get; }

    public string BrokerSymbol { get; }
}

public sealed record TickEvent : MarketEvent
{
    [JsonConstructor]
    public TickEvent(
        string contractVersion,
        DateTimeOffset timestampUtc,
        DateTimeOffset? brokerTimestamp,
        long sequenceId,
        string dataSourceId,
        string symbol,
        string brokerSymbol,
        decimal bid,
        decimal ask,
        decimal? last,
        double? tickVolume,
        TickFlags flags)
        : base(contractVersion, timestampUtc, brokerTimestamp, sequenceId, dataSourceId, symbol, brokerSymbol)
    {
        Bid = ContractGuard.Positive(bid, nameof(bid));
        Ask = ContractGuard.Positive(ask, nameof(ask));
        if (ask < bid)
        {
            throw new ArgumentException("Ask must be greater than or equal to bid.", nameof(ask));
        }

        if (last is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(last), last, "Last must be positive when supplied.");
        }

        if (tickVolume is < 0 || (tickVolume is not null && !double.IsFinite(tickVolume.Value)))
        {
            throw new ArgumentOutOfRangeException(nameof(tickVolume), tickVolume, "Tick volume must be finite and non-negative when supplied.");
        }

        Last = last;
        TickVolume = tickVolume;
        Flags = flags;
    }

    public decimal Bid { get; }

    public decimal Ask { get; }

    public decimal? Last { get; }

    public double? TickVolume { get; }

    public TickFlags Flags { get; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$barStateType")]
[JsonDerivedType(typeof(FormingBarState), "forming")]
[JsonDerivedType(typeof(ClosedBarState), "closed")]
public abstract record BarState
{
    protected BarState(
        BarTimeframe timeframe,
        DateTimeOffset openTimeUtc,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        long tickCount)
    {
        Timeframe = timeframe;
        OpenTimeUtc = ContractGuard.Utc(openTimeUtc, nameof(openTimeUtc));
        Open = ContractGuard.Positive(open, nameof(open));
        High = ContractGuard.Positive(high, nameof(high));
        Low = ContractGuard.Positive(low, nameof(low));
        Close = ContractGuard.Positive(close, nameof(close));
        TickCount = ContractGuard.NonNegative(tickCount, nameof(tickCount));

        decimal maxBody = Math.Max(open, close);
        decimal minBody = Math.Min(open, close);
        if (high < maxBody)
        {
            throw new ArgumentException("High cannot be below open/close.", nameof(high));
        }

        if (low > minBody)
        {
            throw new ArgumentException("Low cannot be above open/close.", nameof(low));
        }
    }

    public BarTimeframe Timeframe { get; }

    public DateTimeOffset OpenTimeUtc { get; }

    public decimal Open { get; }

    public decimal High { get; }

    public decimal Low { get; }

    public decimal Close { get; }

    public long TickCount { get; }
}

public sealed record FormingBarState : BarState
{
    [JsonConstructor]
    public FormingBarState(
        BarTimeframe timeframe,
        DateTimeOffset openTimeUtc,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        long tickCount,
        DateTimeOffset lastUpdateUtc)
        : base(timeframe, openTimeUtc, open, high, low, close, tickCount)
    {
        LastUpdateUtc = ContractGuard.Utc(lastUpdateUtc, nameof(lastUpdateUtc));
        if (lastUpdateUtc < openTimeUtc)
        {
            throw new ArgumentException("Last update cannot precede bar open.", nameof(lastUpdateUtc));
        }
    }

    public DateTimeOffset LastUpdateUtc { get; }
}

public sealed record ClosedBarState : BarState
{
    [JsonConstructor]
    public ClosedBarState(
        BarTimeframe timeframe,
        DateTimeOffset openTimeUtc,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        long tickCount,
        DateTimeOffset closeTimeUtc)
        : base(timeframe, openTimeUtc, open, high, low, close, tickCount)
    {
        CloseTimeUtc = ContractGuard.Utc(closeTimeUtc, nameof(closeTimeUtc));
        if (closeTimeUtc <= openTimeUtc)
        {
            throw new ArgumentException("Closed-bar time must be after bar open.", nameof(closeTimeUtc));
        }
    }

    public DateTimeOffset CloseTimeUtc { get; }
}

public sealed record BarEvent : MarketEvent
{
    [JsonConstructor]
    public BarEvent(
        string contractVersion,
        DateTimeOffset timestampUtc,
        DateTimeOffset? brokerTimestamp,
        long sequenceId,
        string dataSourceId,
        string symbol,
        string brokerSymbol,
        BarUpdateKind updateKind,
        BarState bar)
        : base(contractVersion, timestampUtc, brokerTimestamp, sequenceId, dataSourceId, symbol, brokerSymbol)
    {
        Bar = bar ?? throw new ArgumentNullException(nameof(bar));
        UpdateKind = updateKind;

        if (updateKind == BarUpdateKind.Closed && bar is not ClosedBarState)
        {
            throw new ArgumentException("Closed bar events require ClosedBarState.", nameof(bar));
        }

        if (updateKind != BarUpdateKind.Closed && bar is not FormingBarState)
        {
            throw new ArgumentException("Opened/updated bar events require FormingBarState.", nameof(bar));
        }
    }

    public BarUpdateKind UpdateKind { get; }

    public BarState Bar { get; }
}

public sealed record ConnectionStatusEvent : MarketEvent
{
    [JsonConstructor]
    public ConnectionStatusEvent(
        string contractVersion,
        DateTimeOffset timestampUtc,
        DateTimeOffset? brokerTimestamp,
        long sequenceId,
        string dataSourceId,
        string symbol,
        string brokerSymbol,
        MarketConnectionState state,
        string? reason)
        : base(contractVersion, timestampUtc, brokerTimestamp, sequenceId, dataSourceId, symbol, brokerSymbol)
    {
        State = state;
        Reason = reason;
    }

    public MarketConnectionState State { get; }

    public string? Reason { get; }
}

public sealed record FeedGapEvent : MarketEvent
{
    [JsonConstructor]
    public FeedGapEvent(
        string contractVersion,
        DateTimeOffset timestampUtc,
        long sequenceId,
        string dataSourceId,
        string symbol,
        string brokerSymbol,
        long expectedSequenceId,
        long observedSequenceId,
        FeedSequenceAnomalyKind kind)
        : base(contractVersion, timestampUtc, null, sequenceId, dataSourceId, symbol, brokerSymbol)
    {
        ExpectedSequenceId = ContractGuard.NonNegative(expectedSequenceId, nameof(expectedSequenceId));
        ObservedSequenceId = ContractGuard.NonNegative(observedSequenceId, nameof(observedSequenceId));
        Kind = kind;
    }

    public long ExpectedSequenceId { get; }

    public long ObservedSequenceId { get; }

    public FeedSequenceAnomalyKind Kind { get; }
}

public sealed record SymbolSpecification
{
    [JsonConstructor]
    public SymbolSpecification(
        int digits,
        decimal point,
        decimal tickSize,
        decimal tickValue,
        decimal contractSize,
        decimal minVolume,
        decimal maxVolume,
        decimal volumeStep,
        decimal minStopDistance)
    {
        if (digits is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(digits), digits, "Digits must be between 0 and 10.");
        }

        Digits = digits;
        Point = ContractGuard.Positive(point, nameof(point));
        TickSize = ContractGuard.Positive(tickSize, nameof(tickSize));
        TickValue = ContractGuard.Positive(tickValue, nameof(tickValue));
        ContractSize = ContractGuard.Positive(contractSize, nameof(contractSize));
        MinVolume = ContractGuard.Positive(minVolume, nameof(minVolume));
        MaxVolume = ContractGuard.Positive(maxVolume, nameof(maxVolume));
        VolumeStep = ContractGuard.Positive(volumeStep, nameof(volumeStep));
        MinStopDistance = ContractGuard.NonNegative(minStopDistance, nameof(minStopDistance));

        if (maxVolume < minVolume)
        {
            throw new ArgumentException("Max volume cannot be below min volume.", nameof(maxVolume));
        }
    }

    public int Digits { get; }

    public decimal Point { get; }

    public decimal TickSize { get; }

    public decimal TickValue { get; }

    public decimal ContractSize { get; }

    public decimal MinVolume { get; }

    public decimal MaxVolume { get; }

    public decimal VolumeStep { get; }

    public decimal MinStopDistance { get; }
}

public sealed record SymbolSpecificationEvent : MarketEvent
{
    [JsonConstructor]
    public SymbolSpecificationEvent(
        string contractVersion,
        DateTimeOffset timestampUtc,
        DateTimeOffset? brokerTimestamp,
        long sequenceId,
        string dataSourceId,
        string symbol,
        string brokerSymbol,
        SymbolSpecification specification)
        : base(contractVersion, timestampUtc, brokerTimestamp, sequenceId, dataSourceId, symbol, brokerSymbol)
    {
        Specification = specification ?? throw new ArgumentNullException(nameof(specification));
    }

    public SymbolSpecification Specification { get; }
}
