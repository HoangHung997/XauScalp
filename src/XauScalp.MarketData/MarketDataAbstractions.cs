using XauScalp.Domain;

namespace XauScalp.MarketData;

public interface IMarketDataSource
{
    IAsyncEnumerable<MarketEvent> ReadEventsAsync(CancellationToken cancellationToken = default);
}

public interface IRawMarketEventSink
{
    ValueTask AppendAsync(MarketEvent marketEvent, CancellationToken cancellationToken = default);
}

public interface IUtcClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemUtcClock : IUtcClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed record BrokerSymbolMapping
{
    public BrokerSymbolMapping(string canonicalSymbol, string brokerSymbol)
    {
        if (string.IsNullOrWhiteSpace(canonicalSymbol))
        {
            throw new ArgumentException("Canonical symbol is required.", nameof(canonicalSymbol));
        }

        if (string.IsNullOrWhiteSpace(brokerSymbol))
        {
            throw new ArgumentException("Broker symbol is required.", nameof(brokerSymbol));
        }

        CanonicalSymbol = canonicalSymbol;
        BrokerSymbol = brokerSymbol;
    }

    public string CanonicalSymbol { get; }

    public string BrokerSymbol { get; }
}

public sealed record Mt5GatewayOptions
{
    public Mt5GatewayOptions(string dataSourceId, BrokerSymbolMapping symbolMapping)
    {
        if (string.IsNullOrWhiteSpace(dataSourceId))
        {
            throw new ArgumentException("Data source id is required.", nameof(dataSourceId));
        }

        DataSourceId = dataSourceId;
        SymbolMapping = symbolMapping ?? throw new ArgumentNullException(nameof(symbolMapping));
    }

    public string DataSourceId { get; }

    public BrokerSymbolMapping SymbolMapping { get; }
}
