namespace XauScalp.MarketData;

public enum Mt5WireConnectionState
{
    Connected = 0,
    Reconnecting = 1,
    Disconnected = 2,
}

public abstract record Mt5WireMessage(long SourceSequenceId, string BrokerSymbol);

public sealed record Mt5WireTick(
    long SourceSequenceId,
    string BrokerSymbol,
    long BrokerTimeMilliseconds,
    decimal Bid,
    decimal Ask,
    decimal? Last,
    double? Volume,
    int MqlFlags)
    : Mt5WireMessage(SourceSequenceId, BrokerSymbol);

public sealed record Mt5WireSymbolSpecification(
    long SourceSequenceId,
    string BrokerSymbol,
    int Digits,
    decimal Point,
    decimal TickSize,
    decimal TickValue,
    decimal ContractSize,
    decimal MinVolume,
    decimal MaxVolume,
    decimal VolumeStep,
    decimal MinStopDistance)
    : Mt5WireMessage(SourceSequenceId, BrokerSymbol);

public sealed record Mt5WireConnection(
    long SourceSequenceId,
    string BrokerSymbol,
    Mt5WireConnectionState State,
    string? Reason)
    : Mt5WireMessage(SourceSequenceId, BrokerSymbol);

public sealed record Mt5WireNewsContext(
    long SourceSequenceId,
    string BrokerSymbol,
    bool IsAvailable,
    double? NewsDistanceBeforeSec,
    double? NewsDistanceAfterSec,
    string Source,
    int? SourceErrorCode)
    : Mt5WireMessage(SourceSequenceId, BrokerSymbol);

public interface IMt5Transport
{
    IAsyncEnumerable<Mt5WireMessage> ReadAsync(CancellationToken cancellationToken = default);
}
