using XauScalp.Domain;

namespace XauScalp.Features;

internal sealed class RollingTickSeries
{
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(20);
    private readonly List<TickPoint> _ticks = [];

    public DateTimeOffset? HistoryStartUtc { get; private set; }

    public int Count => _ticks.Count;

    public void Add(TickEvent tick)
    {
        double mid = decimal.ToDouble((tick.Bid + tick.Ask) / 2m);
        if (!double.IsFinite(mid))
        {
            throw new InvalidDataException("Tick midpoint is not finite.");
        }

        if (_ticks.Count > 0 && tick.TimestampUtc < _ticks[^1].TimestampUtc)
        {
            throw new InvalidDataException("Rolling tick series cannot accept a timestamp regression.");
        }

        HistoryStartUtc ??= tick.TimestampUtc;
        _ticks.Add(new TickPoint(tick.TimestampUtc, mid));
        Trim(tick.TimestampUtc);
    }

    public bool IsWarm(DateTimeOffset asOfUtc, TimeSpan window)
    {
        return HistoryStartUtc is DateTimeOffset start && start <= asOfUtc - window;
    }

    public bool TryReturn(DateTimeOffset asOfUtc, TimeSpan window, out double value)
    {
        if (!IsWarm(asOfUtc, window)
            || !TryStepPriceAtOrBefore(asOfUtc - window, out double anchor)
            || !TryLatestAtOrBefore(asOfUtc, out double current))
        {
            value = default;
            return false;
        }

        value = current - anchor;
        return true;
    }

    public bool TryVelocity(DateTimeOffset asOfUtc, TimeSpan window, out double value)
    {
        if (!TryReturn(asOfUtc, window, out double priceReturn))
        {
            value = default;
            return false;
        }

        value = priceReturn / window.TotalSeconds;
        return true;
    }

    public bool TryAcceleration(DateTimeOffset asOfUtc, TimeSpan window, out double value)
    {
        if (!IsWarm(asOfUtc, window))
        {
            value = default;
            return false;
        }

        TimeSpan half = TimeSpan.FromTicks(window.Ticks / 2);
        DateTimeOffset start = asOfUtc - window;
        DateTimeOffset middle = asOfUtc - half;

        if (!TryStepPriceAtOrBefore(start, out double startPrice)
            || !TryStepPriceAtOrBefore(middle, out double middlePrice)
            || !TryLatestAtOrBefore(asOfUtc, out double endPrice))
        {
            value = default;
            return false;
        }

        double priorVelocity = (middlePrice - startPrice) / half.TotalSeconds;
        double recentVelocity = (endPrice - middlePrice) / half.TotalSeconds;
        value = (recentVelocity - priorVelocity) / half.TotalSeconds;
        return true;
    }

    public bool TryTickCount(DateTimeOffset asOfUtc, TimeSpan window, out int count)
    {
        if (!IsWarm(asOfUtc, window))
        {
            count = default;
            return false;
        }

        DateTimeOffset cutoff = asOfUtc - window;
        count = _ticks.Count(point => point.TimestampUtc > cutoff && point.TimestampUtc <= asOfUtc);
        return true;
    }

    public bool TryIntervalStatistics(
        DateTimeOffset asOfUtc,
        TimeSpan window,
        out double meanMs,
        out double stdMs)
    {
        TickPoint[] points = GetWindowPoints(asOfUtc, window, includeBoundaryAnchor: true);
        if (!IsWarm(asOfUtc, window) || points.Length < 2)
        {
            meanMs = default;
            stdMs = default;
            return false;
        }

        double[] intervals = new double[points.Length - 1];
        for (int index = 1; index < points.Length; index++)
        {
            intervals[index - 1] = (points[index].TimestampUtc - points[index - 1].TimestampUtc).TotalMilliseconds;
        }

        double localMeanMs = intervals.Average();
        double variance = intervals.Select(value => Math.Pow(value - localMeanMs, 2)).Average();
        meanMs = localMeanMs;
        stdMs = Math.Sqrt(variance);
        return true;
    }

    public bool TryDirectionRatios(
        DateTimeOffset asOfUtc,
        TimeSpan window,
        out double upRatio,
        out double downRatio)
    {
        TickPoint[] points = GetWindowPoints(asOfUtc, window, includeBoundaryAnchor: true);
        if (!IsWarm(asOfUtc, window) || points.Length < 2)
        {
            upRatio = default;
            downRatio = default;
            return false;
        }

        int up = 0;
        int down = 0;
        int intervals = points.Length - 1;

        for (int index = 1; index < points.Length; index++)
        {
            double delta = points[index].Mid - points[index - 1].Mid;
            if (delta > 0)
            {
                up++;
            }
            else if (delta < 0)
            {
                down++;
            }
        }

        upRatio = (double)up / intervals;
        downRatio = (double)down / intervals;
        return true;
    }

    public bool TryMicroRange(DateTimeOffset asOfUtc, TimeSpan window, out double range)
    {
        TickPoint[] points = GetWindowPoints(asOfUtc, window, includeBoundaryAnchor: true);
        if (!IsWarm(asOfUtc, window) || points.Length == 0)
        {
            range = default;
            return false;
        }

        double min = points.Min(static point => point.Mid);
        double max = points.Max(static point => point.Mid);
        range = max - min;
        return true;
    }

    public bool TryBurstZScore(DateTimeOffset asOfUtc, out double zScore)
    {
        DateTimeOffset currentSecond = FloorToSecond(asOfUtc);
        DateTimeOffset baselineStart = currentSecond - TimeSpan.FromSeconds(15);

        if (HistoryStartUtc is not DateTimeOffset historyStart || historyStart > baselineStart)
        {
            zScore = default;
            return false;
        }

        int currentCount = CountRange(currentSecond, asOfUtc, endInclusive: true);
        double[] baseline = new double[15];

        for (int index = 0; index < baseline.Length; index++)
        {
            DateTimeOffset start = baselineStart + TimeSpan.FromSeconds(index);
            DateTimeOffset end = start + TimeSpan.FromSeconds(1);
            baseline[index] = CountRange(start, end, endInclusive: false);
        }

        double mean = baseline.Average();
        double variance = baseline.Select(value => Math.Pow(value - mean, 2)).Average();
        double std = Math.Sqrt(variance);

        if (std <= 1e-12)
        {
            zScore = default;
            return false;
        }

        zScore = (currentCount - mean) / std;
        return true;
    }

    private TickPoint[] GetWindowPoints(
        DateTimeOffset asOfUtc,
        TimeSpan window,
        bool includeBoundaryAnchor)
    {
        DateTimeOffset cutoff = asOfUtc - window;
        int startIndex = 0;

        if (includeBoundaryAnchor)
        {
            int anchor = FindLatestIndexAtOrBefore(cutoff);
            startIndex = anchor >= 0 ? anchor : 0;
        }
        else
        {
            while (startIndex < _ticks.Count && _ticks[startIndex].TimestampUtc <= cutoff)
            {
                startIndex++;
            }
        }

        return _ticks
            .Skip(startIndex)
            .TakeWhile(point => point.TimestampUtc <= asOfUtc)
            .ToArray();
    }

    private bool TryStepPriceAtOrBefore(DateTimeOffset timestampUtc, out double price)
    {
        int index = FindLatestIndexAtOrBefore(timestampUtc);
        if (index < 0)
        {
            price = default;
            return false;
        }

        price = _ticks[index].Mid;
        return true;
    }

    private bool TryLatestAtOrBefore(DateTimeOffset timestampUtc, out double price)
    {
        return TryStepPriceAtOrBefore(timestampUtc, out price);
    }

    private int FindLatestIndexAtOrBefore(DateTimeOffset timestampUtc)
    {
        for (int index = _ticks.Count - 1; index >= 0; index--)
        {
            if (_ticks[index].TimestampUtc <= timestampUtc)
            {
                return index;
            }
        }

        return -1;
    }

    private int CountRange(DateTimeOffset startInclusive, DateTimeOffset end, bool endInclusive)
    {
        return _ticks.Count(
            point => point.TimestampUtc >= startInclusive
                && (endInclusive ? point.TimestampUtc <= end : point.TimestampUtc < end));
    }

    private void Trim(DateTimeOffset nowUtc)
    {
        DateTimeOffset retentionCutoff = nowUtc - Retention;

        while (_ticks.Count > 1 && _ticks[1].TimestampUtc <= retentionCutoff)
        {
            _ticks.RemoveAt(0);
        }
    }

    private static DateTimeOffset FloorToSecond(DateTimeOffset timestampUtc)
    {
        long ticks = timestampUtc.UtcDateTime.Ticks;
        long secondTicks = TimeSpan.TicksPerSecond;
        long floored = ticks - (ticks % secondTicks);
        return new DateTimeOffset(floored, TimeSpan.Zero);
    }

    private sealed record TickPoint(DateTimeOffset TimestampUtc, double Mid);
}
