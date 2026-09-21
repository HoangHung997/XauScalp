using System.Runtime.CompilerServices;
using XauScalp.Domain;

namespace XauScalp.MarketData;

public sealed class Mt5MarketDataSource : IMarketDataSource
{
    private const int MqlTickFlagBid = 2;
    private const int MqlTickFlagAsk = 4;
    private const int MqlTickFlagLast = 8;
    private const int MqlTickFlagVolume = 16;

    private readonly IMt5Transport _transport;
    private readonly Mt5GatewayOptions _options;
    private readonly IUtcClock _clock;
    private long? _highestSourceSequenceId;

    public Mt5MarketDataSource(
        IMt5Transport transport,
        Mt5GatewayOptions options,
        IUtcClock? clock = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? new SystemUtcClock();
    }

    public async IAsyncEnumerable<MarketEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (Mt5WireMessage message in _transport.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ValidateBrokerSymbol(message);
            DateTimeOffset receivedAtUtc = NormalizeUtc(_clock.UtcNow);

            if (_highestSourceSequenceId is long highest)
            {
                long expected = checked(highest + 1);
                if (message.SourceSequenceId > expected)
                {
                    yield return CreateSequenceAnomaly(
                        message,
                        receivedAtUtc,
                        expected,
                        FeedSequenceAnomalyKind.MissingRange);
                }
                else if (message.SourceSequenceId <= highest)
                {
                    yield return CreateSequenceAnomaly(
                        message,
                        receivedAtUtc,
                        expected,
                        FeedSequenceAnomalyKind.DuplicateOrOutOfOrder);
                }
            }

            yield return ConvertMessage(message, receivedAtUtc);

            if (_highestSourceSequenceId is null || message.SourceSequenceId > _highestSourceSequenceId.Value)
            {
                _highestSourceSequenceId = message.SourceSequenceId;
            }
        }
    }

    private MarketEvent ConvertMessage(Mt5WireMessage message, DateTimeOffset receivedAtUtc)
    {
        return message switch
        {
            Mt5WireTick tick => ConvertTick(tick, receivedAtUtc),
            Mt5WireSymbolSpecification specification => ConvertSymbolSpecification(specification, receivedAtUtc),
            Mt5WireConnection connection => ConvertConnection(connection, receivedAtUtc),
            Mt5WireNewsContext news => ConvertNewsContext(news, receivedAtUtc),
            _ => throw new InvalidDataException($"Unsupported MT5 wire message type '{message.GetType().Name}'."),
        };
    }

    private TickEvent ConvertTick(Mt5WireTick tick, DateTimeOffset receivedAtUtc)
    {
        TickFlags flags = TickFlags.None;
        if ((tick.MqlFlags & MqlTickFlagBid) != 0)
        {
            flags |= TickFlags.Bid;
        }

        if ((tick.MqlFlags & MqlTickFlagAsk) != 0)
        {
            flags |= TickFlags.Ask;
        }

        if ((tick.MqlFlags & MqlTickFlagLast) != 0)
        {
            flags |= TickFlags.Last;
        }

        if ((tick.MqlFlags & MqlTickFlagVolume) != 0)
        {
            flags |= TickFlags.Volume;
        }

        return new TickEvent(
            ContractVersions.MarketEventV1,
            receivedAtUtc,
            DateTimeOffset.FromUnixTimeMilliseconds(tick.BrokerTimeMilliseconds),
            tick.SourceSequenceId,
            _options.DataSourceId,
            _options.SymbolMapping.CanonicalSymbol,
            _options.SymbolMapping.BrokerSymbol,
            tick.Bid,
            tick.Ask,
            tick.Last,
            tick.Volume,
            flags);
    }

    private SymbolSpecificationEvent ConvertSymbolSpecification(
        Mt5WireSymbolSpecification message,
        DateTimeOffset receivedAtUtc)
    {
        var specification = new SymbolSpecification(
            message.Digits,
            message.Point,
            message.TickSize,
            message.TickValue,
            message.ContractSize,
            message.MinVolume,
            message.MaxVolume,
            message.VolumeStep,
            message.MinStopDistance);

        return new SymbolSpecificationEvent(
            ContractVersions.MarketEventV1,
            receivedAtUtc,
            null,
            message.SourceSequenceId,
            _options.DataSourceId,
            _options.SymbolMapping.CanonicalSymbol,
            _options.SymbolMapping.BrokerSymbol,
            specification);
    }

    private ConnectionStatusEvent ConvertConnection(Mt5WireConnection message, DateTimeOffset receivedAtUtc)
    {
        MarketConnectionState state = message.State switch
        {
            Mt5WireConnectionState.Connected => MarketConnectionState.Connected,
            Mt5WireConnectionState.Reconnecting => MarketConnectionState.Reconnecting,
            Mt5WireConnectionState.Disconnected => MarketConnectionState.Disconnected,
            _ => throw new InvalidDataException($"Unsupported MT5 connection state '{message.State}'."),
        };

        return new ConnectionStatusEvent(
            ContractVersions.MarketEventV1,
            receivedAtUtc,
            null,
            message.SourceSequenceId,
            _options.DataSourceId,
            _options.SymbolMapping.CanonicalSymbol,
            _options.SymbolMapping.BrokerSymbol,
            state,
            message.Reason);
    }

    private NewsContextEvent ConvertNewsContext(
        Mt5WireNewsContext message,
        DateTimeOffset receivedAtUtc)
    {
        return new NewsContextEvent(
            ContractVersions.MarketEventV1,
            receivedAtUtc,
            message.SourceSequenceId,
            _options.DataSourceId,
            _options.SymbolMapping.CanonicalSymbol,
            _options.SymbolMapping.BrokerSymbol,
            message.IsAvailable,
            message.NewsDistanceBeforeSec,
            message.NewsDistanceAfterSec,
            message.Source,
            message.SourceErrorCode);
    }

    private FeedGapEvent CreateSequenceAnomaly(
        Mt5WireMessage message,
        DateTimeOffset receivedAtUtc,
        long expectedSequenceId,
        FeedSequenceAnomalyKind kind)
    {
        return new FeedGapEvent(
            ContractVersions.MarketEventV1,
            receivedAtUtc,
            message.SourceSequenceId,
            _options.DataSourceId,
            _options.SymbolMapping.CanonicalSymbol,
            _options.SymbolMapping.BrokerSymbol,
            expectedSequenceId,
            message.SourceSequenceId,
            kind);
    }

    private void ValidateBrokerSymbol(Mt5WireMessage message)
    {
        if (!string.Equals(
                message.BrokerSymbol,
                _options.SymbolMapping.BrokerSymbol,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Received broker symbol '{message.BrokerSymbol}' but gateway is configured for '{_options.SymbolMapping.BrokerSymbol}'.");
        }
    }

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value)
    {
        return value.Offset == TimeSpan.Zero ? value : value.ToUniversalTime();
    }
}
