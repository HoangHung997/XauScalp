namespace XauScalp.MarketData;

public sealed record BrokerClockSegment
{
    public BrokerClockSegment(DateTimeOffset effectiveFromUtc, TimeSpan utcOffset)
    {
        if (effectiveFromUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Effective timestamp must use UTC offset +00:00.", nameof(effectiveFromUtc));
        }

        if (utcOffset < TimeSpan.FromHours(-14) || utcOffset > TimeSpan.FromHours(14))
        {
            throw new ArgumentOutOfRangeException(
                nameof(utcOffset),
                utcOffset,
                "Broker UTC offset must be between -14:00 and +14:00.");
        }

        EffectiveFromUtc = effectiveFromUtc;
        UtcOffset = utcOffset;
    }

    public DateTimeOffset EffectiveFromUtc { get; }

    public TimeSpan UtcOffset { get; }
}

public sealed class BrokerClockConfiguration
{
    private readonly BrokerClockSegment[] _segments;

    public BrokerClockConfiguration(
        string version,
        TimeOnly sessionDayStartBrokerTime,
        IEnumerable<BrokerClockSegment> segments)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("Clock configuration version is required.", nameof(version));
        }

        ArgumentNullException.ThrowIfNull(segments);

        BrokerClockSegment[] materialized = segments
            .OrderBy(static segment => segment.EffectiveFromUtc)
            .ToArray();

        if (materialized.Length == 0)
        {
            throw new ArgumentException("At least one broker-clock segment is required.", nameof(segments));
        }

        if (materialized
            .Select(static segment => segment.EffectiveFromUtc)
            .Distinct()
            .Count() != materialized.Length)
        {
            throw new ArgumentException("Broker-clock segment effective timestamps must be unique.", nameof(segments));
        }

        Version = version;
        SessionDayStartBrokerTime = sessionDayStartBrokerTime;
        _segments = materialized;
    }

    public string Version { get; }

    public TimeOnly SessionDayStartBrokerTime { get; }

    public IReadOnlyList<BrokerClockSegment> Segments => Array.AsReadOnly(_segments);

    internal BrokerClockSegment Resolve(DateTimeOffset timestampUtc)
    {
        BrokerClockSegment? resolved = null;

        foreach (BrokerClockSegment segment in _segments)
        {
            if (segment.EffectiveFromUtc > timestampUtc)
            {
                break;
            }

            resolved = segment;
        }

        return resolved
            ?? throw new InvalidOperationException(
                $"No broker-clock segment is effective for UTC timestamp {timestampUtc:O}.");
    }

    public static BrokerClockConfiguration UtcV1 { get; } =
        new(
            "broker-clock-utc-v1",
            TimeOnly.MinValue,
            [new BrokerClockSegment(DateTimeOffset.MinValue, TimeSpan.Zero)]);
}

public sealed record NormalizedMarketTime(
    DateTimeOffset TimestampUtc,
    DateTimeOffset? SourceBrokerTimestamp,
    DateTime BrokerLocalDateTime,
    TimeSpan BrokerUtcOffset,
    DateOnly UtcDate,
    DateOnly BrokerDate,
    DateOnly SessionDate,
    string ClockConfigurationVersion);

public sealed class MarketClockNormalizer
{
    private readonly BrokerClockConfiguration _configuration;

    public MarketClockNormalizer(BrokerClockConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public BrokerClockConfiguration Configuration => _configuration;

    public NormalizedMarketTime Normalize(
        DateTimeOffset timestampUtc,
        DateTimeOffset? sourceBrokerTimestamp = null)
    {
        DateTimeOffset normalizedUtc = timestampUtc.Offset == TimeSpan.Zero
            ? timestampUtc
            : timestampUtc.ToUniversalTime();

        BrokerClockSegment segment = _configuration.Resolve(normalizedUtc);
        DateTime brokerLocal = DateTime.SpecifyKind(
            normalizedUtc.UtcDateTime + segment.UtcOffset,
            DateTimeKind.Unspecified);

        DateOnly utcDate = DateOnly.FromDateTime(normalizedUtc.UtcDateTime);
        DateOnly brokerDate = DateOnly.FromDateTime(brokerLocal);
        TimeOnly brokerTime = TimeOnly.FromDateTime(brokerLocal);

        DateOnly sessionDate = brokerTime < _configuration.SessionDayStartBrokerTime
            ? brokerDate.AddDays(-1)
            : brokerDate;

        return new NormalizedMarketTime(
            normalizedUtc,
            sourceBrokerTimestamp,
            brokerLocal,
            segment.UtcOffset,
            utcDate,
            brokerDate,
            sessionDate,
            _configuration.Version);
    }
}
