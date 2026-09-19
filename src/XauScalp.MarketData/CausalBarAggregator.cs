using XauScalp.Domain;

namespace XauScalp.MarketData;

public sealed record FormingBarSnapshot(
    FormingBarState State,
    TimeSpan Age,
    double Progress01,
    bool BoundaryElapsed);

public sealed class CausalBarAggregator
{
    private static readonly BarTimeframe[] SupportedTimeframes =
    [
        BarTimeframe.M1,
        BarTimeframe.M5,
        BarTimeframe.M15,
        BarTimeframe.H1,
    ];

    private readonly MarketClockNormalizer _clockNormalizer;
    private readonly string _barDataSourceId;
    private readonly Dictionary<BarTimeframe, MutableBar> _forming = [];
    private DateTimeOffset? _lastTickTimestampUtc;

    public CausalBarAggregator(
        MarketClockNormalizer clockNormalizer,
        string barDataSourceId = "causal-bars-v1")
    {
        _clockNormalizer = clockNormalizer ?? throw new ArgumentNullException(nameof(clockNormalizer));

        if (string.IsNullOrWhiteSpace(barDataSourceId))
        {
            throw new ArgumentException("Bar data-source id is required.", nameof(barDataSourceId));
        }

        _barDataSourceId = barDataSourceId;
    }

    public NormalizedMarketTime? LastNormalizedTime { get; private set; }

    public IReadOnlyList<BarEvent> Apply(TickEvent tick)
    {
        ArgumentNullException.ThrowIfNull(tick);

        if (_lastTickTimestampUtc is DateTimeOffset last && tick.TimestampUtc < last)
        {
            throw new InvalidDataException(
                $"Tick UTC timestamp regressed from {last:O} to {tick.TimestampUtc:O}. "
                + "The causal bar builder never reorders input.");
        }

        _lastTickTimestampUtc = tick.TimestampUtc;
        LastNormalizedTime = _clockNormalizer.Normalize(tick.TimestampUtc, tick.BrokerTimestamp);

        var output = new List<BarEvent>(SupportedTimeframes.Length * 2);
        decimal price = tick.Bid;

        foreach (BarTimeframe timeframe in SupportedTimeframes)
        {
            ApplyToTimeframe(tick, timeframe, price, output);
        }

        return output;
    }

    public FormingBarSnapshot? GetFormingSnapshot(BarTimeframe timeframe, DateTimeOffset asOfUtc)
    {
        EnsureSupported(timeframe);

        if (!_forming.TryGetValue(timeframe, out MutableBar? mutable))
        {
            return null;
        }

        DateTimeOffset normalizedAsOf = asOfUtc.Offset == TimeSpan.Zero
            ? asOfUtc
            : asOfUtc.ToUniversalTime();

        if (normalizedAsOf < mutable.OpenTimeUtc)
        {
            throw new ArgumentException("Snapshot time cannot precede the bar open time.", nameof(asOfUtc));
        }

        TimeSpan duration = GetDuration(timeframe);
        TimeSpan age = normalizedAsOf - mutable.OpenTimeUtc;
        double progress = Math.Clamp(age.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);

        return new FormingBarSnapshot(
            mutable.ToFormingState(),
            age,
            progress,
            normalizedAsOf >= mutable.OpenTimeUtc + duration);
    }

    private void ApplyToTimeframe(
        TickEvent tick,
        BarTimeframe timeframe,
        decimal price,
        List<BarEvent> output)
    {
        TimeSpan duration = GetDuration(timeframe);
        DateTimeOffset bucketStart = FloorToBucket(tick.TimestampUtc, duration);

        if (!_forming.TryGetValue(timeframe, out MutableBar? current))
        {
            MutableBar opened = MutableBar.Open(timeframe, bucketStart, tick.TimestampUtc, price);
            _forming[timeframe] = opened;
            output.Add(CreateBarEvent(tick, BarUpdateKind.Opened, opened.ToFormingState()));
            return;
        }

        if (bucketStart == current.OpenTimeUtc)
        {
            current.Update(tick.TimestampUtc, price);
            output.Add(CreateBarEvent(tick, BarUpdateKind.Updated, current.ToFormingState()));
            return;
        }

        if (bucketStart < current.OpenTimeUtc)
        {
            throw new InvalidDataException(
                $"Tick bucket {bucketStart:O} precedes current {timeframe} bucket {current.OpenTimeUtc:O}.");
        }

        ClosedBarState closed = current.ToClosedState(current.OpenTimeUtc + duration);
        output.Add(CreateBarEvent(tick, BarUpdateKind.Closed, closed));

        MutableBar next = MutableBar.Open(timeframe, bucketStart, tick.TimestampUtc, price);
        _forming[timeframe] = next;
        output.Add(CreateBarEvent(tick, BarUpdateKind.Opened, next.ToFormingState()));
    }

    private BarEvent CreateBarEvent(TickEvent trigger, BarUpdateKind updateKind, BarState state)
    {
        return new BarEvent(
            ContractVersions.MarketEventV1,
            trigger.TimestampUtc,
            trigger.BrokerTimestamp,
            trigger.SequenceId,
            _barDataSourceId,
            trigger.Symbol,
            trigger.BrokerSymbol,
            updateKind,
            state);
    }

    public static TimeSpan GetDuration(BarTimeframe timeframe)
    {
        return timeframe switch
        {
            BarTimeframe.M1 => TimeSpan.FromMinutes(1),
            BarTimeframe.M5 => TimeSpan.FromMinutes(5),
            BarTimeframe.M15 => TimeSpan.FromMinutes(15),
            BarTimeframe.H1 => TimeSpan.FromHours(1),
            _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "Unsupported bar timeframe."),
        };
    }

    public static DateTimeOffset FloorToBucket(DateTimeOffset timestampUtc, TimeSpan duration)
    {
        DateTimeOffset normalizedUtc = timestampUtc.Offset == TimeSpan.Zero
            ? timestampUtc
            : timestampUtc.ToUniversalTime();

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Bucket duration must be positive.");
        }

        long durationTicks = duration.Ticks;
        long utcTicks = normalizedUtc.UtcDateTime.Ticks;
        long bucketTicks = utcTicks - (utcTicks % durationTicks);

        return new DateTimeOffset(bucketTicks, TimeSpan.Zero);
    }

    private static void EnsureSupported(BarTimeframe timeframe)
    {
        _ = GetDuration(timeframe);
    }

    private sealed class MutableBar
    {
        private MutableBar(
            BarTimeframe timeframe,
            DateTimeOffset openTimeUtc,
            DateTimeOffset lastUpdateUtc,
            decimal open,
            decimal high,
            decimal low,
            decimal close,
            long tickCount)
        {
            Timeframe = timeframe;
            OpenTimeUtc = openTimeUtc;
            LastUpdateUtc = lastUpdateUtc;
            Open = open;
            High = high;
            Low = low;
            Close = close;
            TickCount = tickCount;
        }

        public BarTimeframe Timeframe { get; }

        public DateTimeOffset OpenTimeUtc { get; }

        public DateTimeOffset LastUpdateUtc { get; private set; }

        public decimal Open { get; }

        public decimal High { get; private set; }

        public decimal Low { get; private set; }

        public decimal Close { get; private set; }

        public long TickCount { get; private set; }

        public static MutableBar Open(
            BarTimeframe timeframe,
            DateTimeOffset openTimeUtc,
            DateTimeOffset tickTimestampUtc,
            decimal price)
        {
            return new MutableBar(
                timeframe,
                openTimeUtc,
                tickTimestampUtc,
                price,
                price,
                price,
                price,
                tickCount: 1);
        }

        public void Update(DateTimeOffset tickTimestampUtc, decimal price)
        {
            High = Math.Max(High, price);
            Low = Math.Min(Low, price);
            Close = price;
            TickCount = checked(TickCount + 1);
            LastUpdateUtc = tickTimestampUtc;
        }

        public FormingBarState ToFormingState()
        {
            return new FormingBarState(
                Timeframe,
                OpenTimeUtc,
                Open,
                High,
                Low,
                Close,
                TickCount,
                LastUpdateUtc);
        }

        public ClosedBarState ToClosedState(DateTimeOffset closeTimeUtc)
        {
            return new ClosedBarState(
                Timeframe,
                OpenTimeUtc,
                Open,
                High,
                Low,
                Close,
                TickCount,
                closeTimeUtc);
        }
    }
}
