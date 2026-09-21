using XauScalp.MarketData;

namespace XauScalp.Features;

public sealed record MarketSessionSegment
{
    public MarketSessionSegment(int code, string name, TimeOnly startInclusive, TimeOnly endExclusive)
    {
        if (code < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Session code must be non-negative.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Session name is required.", nameof(name));
        }

        if (startInclusive == endExclusive)
        {
            throw new ArgumentException("Session start and end cannot be equal.");
        }

        Code = code;
        Name = name;
        StartInclusive = startInclusive;
        EndExclusive = endExclusive;
    }

    public int Code { get; }

    public string Name { get; }

    public TimeOnly StartInclusive { get; }

    public TimeOnly EndExclusive { get; }

    public bool Contains(TimeOnly time)
    {
        return StartInclusive < EndExclusive
            ? time >= StartInclusive && time < EndExclusive
            : time >= StartInclusive || time < EndExclusive;
    }
}

public sealed class MarketSessionSchedule
{
    private readonly MarketSessionSegment[] _segments;

    public MarketSessionSchedule(IEnumerable<MarketSessionSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        _segments = segments.ToArray();

        if (_segments.Length == 0)
        {
            throw new ArgumentException("At least one market-session segment is required.", nameof(segments));
        }

        if (_segments.Select(static segment => segment.Code).Distinct().Count() != _segments.Length)
        {
            throw new ArgumentException("Market-session codes must be unique.", nameof(segments));
        }

        for (int minute = 0; minute < 24 * 60; minute++)
        {
            var time = new TimeOnly(minute / 60, minute % 60);
            int matches = _segments.Count(segment => segment.Contains(time));
            if (matches > 1)
            {
                throw new ArgumentException("Market-session segments must not overlap.", nameof(segments));
            }
        }
    }

    public int? ResolveCode(DateTime brokerLocalDateTime)
    {
        TimeOnly time = TimeOnly.FromDateTime(brokerLocalDateTime);
        MarketSessionSegment? match = _segments.FirstOrDefault(segment => segment.Contains(time));
        return match?.Code;
    }
}

public sealed record FeatureExternalContext
{
    public FeatureExternalContext(
        DateTimeOffset observedAtUtc,
        double? atrM1 = null,
        double? estimatedLatencyMs = null,
        double? estimatedSlippagePoints = null,
        double? newsDistanceBeforeSec = null,
        double? newsDistanceAfterSec = null,
        bool? isHighImpactNewsWindow = null)
    {
        if (observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("External-context timestamp must use UTC offset +00:00.", nameof(observedAtUtc));
        }

        AtrM1 = ValidatePositiveOptional(atrM1, nameof(atrM1));
        EstimatedLatencyMs = ValidateNonNegativeOptional(estimatedLatencyMs, nameof(estimatedLatencyMs));
        EstimatedSlippagePoints = ValidateNonNegativeOptional(estimatedSlippagePoints, nameof(estimatedSlippagePoints));
        NewsDistanceBeforeSec = ValidateNonNegativeOptional(newsDistanceBeforeSec, nameof(newsDistanceBeforeSec));
        NewsDistanceAfterSec = ValidateNonNegativeOptional(newsDistanceAfterSec, nameof(newsDistanceAfterSec));
        ObservedAtUtc = observedAtUtc;
        IsHighImpactNewsWindow = isHighImpactNewsWindow;
    }

    public DateTimeOffset ObservedAtUtc { get; }

    public double? AtrM1 { get; }

    public double? EstimatedLatencyMs { get; }

    public double? EstimatedSlippagePoints { get; }

    public double? NewsDistanceBeforeSec { get; }

    public double? NewsDistanceAfterSec { get; }

    public bool? IsHighImpactNewsWindow { get; }

    private static double? ValidatePositiveOptional(double? value, string parameterName)
    {
        if (value is not null && (!double.IsFinite(value.Value) || value <= 0))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be finite and positive.");
        }

        return value;
    }

    private static double? ValidateNonNegativeOptional(double? value, string parameterName)
    {
        if (value is not null && (!double.IsFinite(value.Value) || value < 0))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be finite and non-negative.");
        }

        return value;
    }
}

public sealed class XauFeatureEngineOptions
{
    public XauFeatureEngineOptions(
        BrokerClockConfiguration clockConfiguration,
        MarketSessionSchedule? sessionSchedule = null,
        TimeSpan? externalContextMaxAge = null,
        int highImpactNewsFeatureWindowBeforeSec = 900,
        int highImpactNewsFeatureWindowAfterSec = 900)
    {
        ClockConfiguration = clockConfiguration ?? throw new ArgumentNullException(nameof(clockConfiguration));
        SessionSchedule = sessionSchedule;

        TimeSpan maxAge = externalContextMaxAge ?? TimeSpan.FromSeconds(60);
        if (maxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(externalContextMaxAge), maxAge, "External context max age must be positive.");
        }

        if (highImpactNewsFeatureWindowBeforeSec < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(highImpactNewsFeatureWindowBeforeSec),
                highImpactNewsFeatureWindowBeforeSec,
                "News feature window must be non-negative.");
        }

        if (highImpactNewsFeatureWindowAfterSec < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(highImpactNewsFeatureWindowAfterSec),
                highImpactNewsFeatureWindowAfterSec,
                "News feature window must be non-negative.");
        }

        ExternalContextMaxAge = maxAge;
        HighImpactNewsFeatureWindowBeforeSec = highImpactNewsFeatureWindowBeforeSec;
        HighImpactNewsFeatureWindowAfterSec = highImpactNewsFeatureWindowAfterSec;
    }

    public BrokerClockConfiguration ClockConfiguration { get; }

    public MarketSessionSchedule? SessionSchedule { get; }

    public TimeSpan ExternalContextMaxAge { get; }

    public int HighImpactNewsFeatureWindowBeforeSec { get; }

    public int HighImpactNewsFeatureWindowAfterSec { get; }
}
