using XauScalp.Domain;

namespace XauScalp.Features;

public sealed record StructureFeatureMetric(
    string Name,
    double? Value,
    string Unit,
    DateTimeOffset? ObservedAtUtc,
    string? UnavailableReason)
{
    public bool IsAvailable => Value is not null;
}

public sealed class CausalStructureRegimeTracker
{
    private const int AtrPeriod = 14;
    private const int AdxPeriod = 14;
    private const int EmaPeriod = 20;
    private const int MaxBarsPerTimeframe = 512;
    private readonly Dictionary<BarTimeframe, List<ClosedBarState>> _bars =
        Enum.GetValues<BarTimeframe>()
            .ToDictionary(static timeframe => timeframe, static _ => new List<ClosedBarState>());

    private readonly List<SwingLevel> _swingHighs = [];
    private readonly List<SwingLevel> _swingLows = [];
    private readonly List<GapZone> _bullFvgs = [];
    private readonly List<GapZone> _bearFvgs = [];
    private readonly List<OrderBlockZone> _bullOrderBlocks = [];
    private readonly List<OrderBlockZone> _bearOrderBlocks = [];
    private readonly Dictionary<BarTimeframe, SqueezeState> _squeeze = [];

    private StructureBreak? _lastBos;
    private StructureBreak? _lastMss;
    private StructureBreak? _lastChoch;
    private int _structureDirection;
    private decimal? _previousM1Close;
    private double? _latestDisplacementRangeAtr;
    private double? _latestDisplacementBodyRatio;
    private DateTimeOffset? _latestDisplacementAtUtc;

    public void ObserveClosedBar(ClosedBarState bar)
    {
        ArgumentNullException.ThrowIfNull(bar);

        List<ClosedBarState> history = _bars[bar.Timeframe];
        if (history.Count > 0 && bar.CloseTimeUtc <= history[^1].CloseTimeUtc)
        {
            throw new InvalidDataException(
                $"{bar.Timeframe} closed bars must arrive in strictly increasing close-time order.");
        }

        history.Add(bar);
        if (history.Count > MaxBarsPerTimeframe)
        {
            history.RemoveAt(0);
        }

        if (bar.Timeframe == BarTimeframe.M1)
        {
            ConfirmNewestM1Swing();
            DetectM1Break(bar);
            DetectFvg();
            DetectDisplacementAndOrderBlock(bar);
            _previousM1Close = bar.Close;
        }

        if (bar.Timeframe is BarTimeframe.M1 or BarTimeframe.M5)
        {
            UpdateSqueeze(bar.Timeframe, bar.CloseTimeUtc);
        }
    }

    public void UpdateTick(TickEvent tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        decimal mid = (tick.Bid + tick.Ask) / 2m;

        foreach (GapZone gap in _bullFvgs)
        {
            gap.UpdateFill(mid);
        }

        foreach (GapZone gap in _bearFvgs)
        {
            gap.UpdateFill(mid);
        }
    }

    public IReadOnlyList<StructureFeatureMetric> Snapshot(
        DateTimeOffset asOfUtc,
        decimal currentMid,
        FormingBarState? formingM1)
    {
        var metrics = new List<StructureFeatureMetric>(64);

        double? atrM1 = Atr(BarTimeframe.M1);
        double? atrM5 = Atr(BarTimeframe.M5);
        double? atrRatioM1 = AtrRatio(BarTimeframe.M1);
        double? atrRatioM5 = AtrRatio(BarTimeframe.M5);

        AddOptional(metrics, FeatureNames.AtrM1, atrM1, "price", asOfUtc, "14 closed M1 bars unavailable");
        AddOptional(metrics, FeatureNames.AtrM5, atrM5, "price", asOfUtc, "14 closed M5 bars unavailable");
        AddOptional(metrics, FeatureNames.AtrRatioM1, atrRatioM1, "ratio", asOfUtc, "28 closed M1 true-range observations unavailable");
        AddOptional(metrics, FeatureNames.AtrRatioM5, atrRatioM5, "ratio", asOfUtc, "28 closed M5 true-range observations unavailable");
        AddOptional(metrics, FeatureNames.AdxM1, Adx(BarTimeframe.M1), "index", asOfUtc, "ADX warmup unavailable");
        AddOptional(metrics, FeatureNames.AdxM5, Adx(BarTimeframe.M5), "index", asOfUtc, "ADX warmup unavailable");

        AddCompression(metrics, BarTimeframe.M1, FeatureNames.M1CompressionScore, FeatureNames.M1Squeeze, FeatureNames.SecondsSinceM1SqueezeRelease, asOfUtc);
        AddCompression(metrics, BarTimeframe.M5, FeatureNames.M5CompressionScore, FeatureNames.M5Squeeze, FeatureNames.SecondsSinceM5SqueezeRelease, asOfUtc);

        AddStructureEvents(metrics, asOfUtc);
        AddFvg(metrics, asOfUtc, currentMid, atrM1);
        AddOrderBlocks(metrics, asOfUtc, currentMid, formingM1, atrM1);
        AddOptional(metrics, FeatureNames.DisplacementRangeAtr, _latestDisplacementRangeAtr, "atr", _latestDisplacementAtUtc, "causal M1 displacement unavailable");
        AddOptional(metrics, FeatureNames.DisplacementBodyRatio, _latestDisplacementBodyRatio, "ratio", _latestDisplacementAtUtc, "causal M1 displacement unavailable");

        double? slopeM5 = EmaSlope(BarTimeframe.M5);
        double? slopeM15 = EmaSlope(BarTimeframe.M15);
        double? slopeH1 = EmaSlope(BarTimeframe.H1);
        AddOptional(metrics, FeatureNames.EmaSlopeM5, slopeM5, "atrPerBar", asOfUtc, "EMA slope M5 warmup unavailable");
        AddOptional(metrics, FeatureNames.EmaSlopeM15, slopeM15, "atrPerBar", asOfUtc, "EMA slope M15 warmup unavailable");
        AddOptional(metrics, FeatureNames.EmaSlopeH1, slopeH1, "atrPerBar", asOfUtc, "EMA slope H1 warmup unavailable");

        int? dirM5 = DirectionState(slopeM5);
        int? dirM15 = DirectionState(slopeM15);
        int? dirH1 = DirectionState(slopeH1);
        AddOptional(metrics, FeatureNames.M5DirectionState, dirM5, "direction", asOfUtc, "M5 direction warmup unavailable");
        AddOptional(metrics, FeatureNames.M15DirectionState, dirM15, "direction", asOfUtc, "M15 direction warmup unavailable");
        AddOptional(metrics, FeatureNames.H1DirectionState, dirH1, "direction", asOfUtc, "H1 direction warmup unavailable");

        if (dirM5 is int m5 && dirM15 is int m15 && dirH1 is int h1)
        {
            double alignment = (m5 + m15 + h1) / 3.0;
            Add(metrics, FeatureNames.TrendAlignmentScore, alignment, "score", asOfUtc);
            Add(metrics, FeatureNames.HtfAlignedUp, m5 > 0 && m15 > 0 && h1 > 0 ? 1 : 0, "boolean", asOfUtc);
            Add(metrics, FeatureNames.HtfAlignedDown, m5 < 0 && m15 < 0 && h1 < 0 ? 1 : 0, "boolean", asOfUtc);
            bool hasUp = m5 > 0 || m15 > 0 || h1 > 0;
            bool hasDown = m5 < 0 || m15 < 0 || h1 < 0;
            Add(metrics, FeatureNames.HtfConflict, hasUp && hasDown ? 1 : 0, "boolean", asOfUtc);
            Add(metrics, FeatureNames.M15StructureState, m15, "direction", asOfUtc);
        }
        else
        {
            foreach ((string Name, string Unit) item in new[]
            {
                (FeatureNames.TrendAlignmentScore, "score"),
                (FeatureNames.HtfAlignedUp, "boolean"),
                (FeatureNames.HtfAlignedDown, "boolean"),
                (FeatureNames.HtfConflict, "boolean"),
                (FeatureNames.M15StructureState, "direction"),
            })
            {
                Unavailable(metrics, item.Name, item.Unit, "higher-timeframe EMA warmup unavailable");
            }
        }

        if (atrRatioM5 is double ratioM5)
        {
            int volatilityState = ratioM5 > 1.2 ? 1 : ratioM5 < 0.8 ? -1 : 0;
            Add(metrics, FeatureNames.M5VolatilityState, volatilityState, "state", asOfUtc);
        }
        else
        {
            Unavailable(metrics, FeatureNames.M5VolatilityState, "state", "M5 ATR ratio unavailable");
        }

        AddMeanDistance(metrics, BarTimeframe.M5, FeatureNames.DistanceToM5MeanAtr, currentMid, asOfUtc);
        AddMeanDistance(metrics, BarTimeframe.M15, FeatureNames.DistanceToM15MeanAtr, currentMid, asOfUtc);

        return metrics;
    }

    private void ConfirmNewestM1Swing()
    {
        List<ClosedBarState> bars = _bars[BarTimeframe.M1];
        if (bars.Count < 5)
        {
            return;
        }

        int center = bars.Count - 3;
        ClosedBarState candidate = bars[center];
        bool high = true;
        bool low = true;

        for (int offset = -2; offset <= 2; offset++)
        {
            if (offset == 0)
            {
                continue;
            }

            ClosedBarState neighbor = bars[center + offset];
            high &= candidate.High > neighbor.High;
            low &= candidate.Low < neighbor.Low;
        }

        DateTimeOffset confirmedAt = bars[^1].CloseTimeUtc;
        if (high)
        {
            AddSwing(_swingHighs, candidate.High, candidate.OpenTimeUtc, confirmedAt);
        }

        if (low)
        {
            AddSwing(_swingLows, candidate.Low, candidate.OpenTimeUtc, confirmedAt);
        }
    }

    private static void AddSwing(
        List<SwingLevel> swings,
        decimal price,
        DateTimeOffset sourceOpen,
        DateTimeOffset confirmedAt)
    {
        if (swings.Any(item => item.SourceOpenTimeUtc == sourceOpen))
        {
            return;
        }

        swings.Add(new SwingLevel(price, sourceOpen, confirmedAt));
        if (swings.Count > 128)
        {
            swings.RemoveAt(0);
        }
    }

    private void DetectM1Break(ClosedBarState latest)
    {
        if (_previousM1Close is not decimal previousClose)
        {
            return;
        }

        SwingLevel? upper = _swingHighs.LastOrDefault(item => item.ConfirmedAtUtc <= latest.CloseTimeUtc);
        SwingLevel? lower = _swingLows.LastOrDefault(item => item.ConfirmedAtUtc <= latest.CloseTimeUtc);

        int direction = 0;
        decimal? level = null;

        if (upper is not null && previousClose <= upper.Price && latest.Close > upper.Price)
        {
            direction = 1;
            level = upper.Price;
        }
        else if (lower is not null && previousClose >= lower.Price && latest.Close < lower.Price)
        {
            direction = -1;
            level = lower.Price;
        }

        if (direction == 0 || level is null)
        {
            return;
        }

        double? atr = Atr(BarTimeframe.M1);
        double? distanceAtr = atr is double a && a > 0
            ? (double)(Math.Abs(latest.Close - level.Value) / (decimal)a)
            : null;

        var structureBreak = new StructureBreak(direction, latest.CloseTimeUtc, distanceAtr);
        _lastBos = structureBreak;

        bool reversal = _structureDirection != 0 && direction != _structureDirection;
        if (reversal)
        {
            _lastMss = structureBreak;

            decimal range = latest.High - latest.Low;
            double bodyRatio = range > 0
                ? (double)(Math.Abs(latest.Close - latest.Open) / range)
                : 0;

            if (distanceAtr is >= 0.10 && bodyRatio >= 0.60)
            {
                _lastChoch = structureBreak;
            }
        }

        _structureDirection = direction;
    }

    private void DetectFvg()
    {
        List<ClosedBarState> bars = _bars[BarTimeframe.M1];
        if (bars.Count < 3)
        {
            return;
        }

        ClosedBarState first = bars[^3];
        ClosedBarState third = bars[^1];

        if (third.Low > first.High)
        {
            AddGap(_bullFvgs, new GapZone(
                direction: 1,
                lower: first.High,
                upper: third.Low,
                createdAtUtc: third.CloseTimeUtc));
        }

        if (third.High < first.Low)
        {
            AddGap(_bearFvgs, new GapZone(
                direction: -1,
                lower: third.High,
                upper: first.Low,
                createdAtUtc: third.CloseTimeUtc));
        }
    }

    private static void AddGap(List<GapZone> gaps, GapZone gap)
    {
        gaps.Add(gap);
        if (gaps.Count > 32)
        {
            gaps.RemoveAt(0);
        }
    }

    private void DetectDisplacementAndOrderBlock(ClosedBarState latest)
    {
        double? atr = Atr(BarTimeframe.M1);
        if (atr is not double a || a <= 0)
        {
            return;
        }

        decimal range = latest.High - latest.Low;
        double rangeAtr = (double)(range / (decimal)a);
        double bodyRatio = range > 0
            ? (double)(Math.Abs(latest.Close - latest.Open) / range)
            : 0;

        _latestDisplacementRangeAtr = rangeAtr;
        _latestDisplacementBodyRatio = bodyRatio;
        _latestDisplacementAtUtc = latest.CloseTimeUtc;

        if (rangeAtr < 1.5 || bodyRatio < 0.60)
        {
            return;
        }

        List<ClosedBarState> bars = _bars[BarTimeframe.M1];
        if (bars.Count < 2)
        {
            return;
        }

        ClosedBarState previous = bars[^2];

        if (latest.Close > latest.Open && previous.Close < previous.Open)
        {
            AddOrderBlock(_bullOrderBlocks, new OrderBlockZone(previous.Low, previous.High, latest.CloseTimeUtc));
        }
        else if (latest.Close < latest.Open && previous.Close > previous.Open)
        {
            AddOrderBlock(_bearOrderBlocks, new OrderBlockZone(previous.Low, previous.High, latest.CloseTimeUtc));
        }
    }

    private static void AddOrderBlock(List<OrderBlockZone> zones, OrderBlockZone zone)
    {
        zones.Add(zone);
        if (zones.Count > 32)
        {
            zones.RemoveAt(0);
        }
    }

    private void UpdateSqueeze(BarTimeframe timeframe, DateTimeOffset atUtc)
    {
        double? score = CompressionScore(timeframe);
        if (score is null)
        {
            return;
        }

        bool isSqueeze = score >= 0.25;
        if (!_squeeze.TryGetValue(timeframe, out SqueezeState? state))
        {
            _squeeze[timeframe] = new SqueezeState(isSqueeze, null);
            return;
        }

        if (state.IsSqueeze && !isSqueeze)
        {
            state.LastReleaseUtc = atUtc;
        }

        state.IsSqueeze = isSqueeze;
    }

    private void AddCompression(
        List<StructureFeatureMetric> metrics,
        BarTimeframe timeframe,
        string scoreName,
        string squeezeName,
        string releaseName,
        DateTimeOffset asOfUtc)
    {
        double? score = CompressionScore(timeframe);
        if (score is not double compression)
        {
            Unavailable(metrics, scoreName, "score", $"{timeframe} compression warmup unavailable");
            Unavailable(metrics, squeezeName, "boolean", $"{timeframe} compression warmup unavailable");
            Unavailable(metrics, releaseName, "seconds", $"{timeframe} squeeze release unavailable");
            return;
        }

        Add(metrics, scoreName, compression, "score", asOfUtc);
        bool squeeze = compression >= 0.25;
        Add(metrics, squeezeName, squeeze ? 1 : 0, "boolean", asOfUtc);

        if (_squeeze.TryGetValue(timeframe, out SqueezeState? state)
            && state.LastReleaseUtc is DateTimeOffset release)
        {
            Add(metrics, releaseName, Math.Max(0, (asOfUtc - release).TotalSeconds), "seconds", asOfUtc);
        }
        else
        {
            Unavailable(metrics, releaseName, "seconds", "no causal squeeze release observed");
        }
    }

    private void AddStructureEvents(List<StructureFeatureMetric> metrics, DateTimeOffset asOfUtc)
    {
        AddBreak(metrics, _lastBos, FeatureNames.BosDirection, FeatureNames.BosDistanceAtr, FeatureNames.BosAgeSec, asOfUtc);
        AddDirectionAge(metrics, _lastMss, FeatureNames.MssDirection, FeatureNames.MssAgeSec, asOfUtc);
        AddDirectionAge(metrics, _lastChoch, FeatureNames.ChochDirection, FeatureNames.ChochAgeSec, asOfUtc);
    }

    private static void AddBreak(
        List<StructureFeatureMetric> metrics,
        StructureBreak? value,
        string directionName,
        string distanceName,
        string ageName,
        DateTimeOffset asOfUtc)
    {
        if (value is null)
        {
            Unavailable(metrics, directionName, "direction", "no causal structure break observed");
            Unavailable(metrics, distanceName, "atr", "no causal structure break observed");
            Unavailable(metrics, ageName, "seconds", "no causal structure break observed");
            return;
        }

        Add(metrics, directionName, value.Direction, "direction", value.AtUtc);
        AddOptional(metrics, distanceName, value.DistanceAtr, "atr", value.AtUtc, "ATR unavailable when structure break occurred");
        Add(metrics, ageName, Math.Max(0, (asOfUtc - value.AtUtc).TotalSeconds), "seconds", asOfUtc);
    }

    private static void AddDirectionAge(
        List<StructureFeatureMetric> metrics,
        StructureBreak? value,
        string directionName,
        string ageName,
        DateTimeOffset asOfUtc)
    {
        if (value is null)
        {
            Unavailable(metrics, directionName, "direction", "structure change not observed");
            Unavailable(metrics, ageName, "seconds", "structure change not observed");
            return;
        }

        Add(metrics, directionName, value.Direction, "direction", value.AtUtc);
        Add(metrics, ageName, Math.Max(0, (asOfUtc - value.AtUtc).TotalSeconds), "seconds", asOfUtc);
    }

    private void AddFvg(
        List<StructureFeatureMetric> metrics,
        DateTimeOffset asOfUtc,
        decimal currentMid,
        double? atrM1)
    {
        if (atrM1 is not double atr || atr <= 0)
        {
            foreach (string name in new[]
            {
                FeatureNames.BullFvgDistanceAtr,
                FeatureNames.BearFvgDistanceAtr,
                FeatureNames.FvgSizeAtr,
                FeatureNames.FvgAgeSec,
                FeatureNames.FvgFillPct,
            })
            {
                string unit = name == FeatureNames.FvgAgeSec
                    ? "seconds"
                    : name == FeatureNames.FvgFillPct
                        ? "pct"
                        : "atr";
                Unavailable(metrics, name, unit, "M1 ATR unavailable for FVG normalization");
            }

            return;
        }

        GapZone? bull = NearestGap(_bullFvgs, currentMid);
        GapZone? bear = NearestGap(_bearFvgs, currentMid);
        AddGapDistance(metrics, bull, FeatureNames.BullFvgDistanceAtr, currentMid, atr, asOfUtc);
        AddGapDistance(metrics, bear, FeatureNames.BearFvgDistanceAtr, currentMid, atr, asOfUtc);

        GapZone? selected = new[] { bull, bear }
            .Where(static gap => gap is not null)
            .OrderBy(gap => gap!.DistanceTo(currentMid))
            .FirstOrDefault();

        if (selected is null)
        {
            Unavailable(metrics, FeatureNames.FvgSizeAtr, "atr", "no causal FVG observed");
            Unavailable(metrics, FeatureNames.FvgAgeSec, "seconds", "no causal FVG observed");
            Unavailable(metrics, FeatureNames.FvgFillPct, "pct", "no causal FVG observed");
            return;
        }

        Add(metrics, FeatureNames.FvgSizeAtr, (double)(selected.Size / (decimal)atr), "atr", selected.CreatedAtUtc);
        Add(metrics, FeatureNames.FvgAgeSec, Math.Max(0, (asOfUtc - selected.CreatedAtUtc).TotalSeconds), "seconds", asOfUtc);
        Add(metrics, FeatureNames.FvgFillPct, selected.FillPct * 100, "pct", asOfUtc);
    }

    private static GapZone? NearestGap(List<GapZone> gaps, decimal current)
    {
        return gaps.Count == 0
            ? null
            : gaps.OrderBy(gap => gap.DistanceTo(current)).First();
    }

    private static void AddGapDistance(
        List<StructureFeatureMetric> metrics,
        GapZone? gap,
        string name,
        decimal current,
        double atr,
        DateTimeOffset asOfUtc)
    {
        if (gap is null)
        {
            Unavailable(metrics, name, "atr", "no causal FVG observed");
            return;
        }

        Add(metrics, name, (double)(gap.DistanceTo(current) / (decimal)atr), "atr", asOfUtc);
    }

    private void AddOrderBlocks(
        List<StructureFeatureMetric> metrics,
        DateTimeOffset asOfUtc,
        decimal current,
        FormingBarState? formingM1,
        double? atrM1)
    {
        if (atrM1 is not double atr || atr <= 0)
        {
            Unavailable(metrics, FeatureNames.BullOrderBlockDistanceAtr, "atr", "M1 ATR unavailable");
            Unavailable(metrics, FeatureNames.BearOrderBlockDistanceAtr, "atr", "M1 ATR unavailable");
            Unavailable(metrics, FeatureNames.OrderBlockOverlapPct, "pct", "M1 ATR unavailable");
            return;
        }

        OrderBlockZone? bull = NearestOrderBlock(_bullOrderBlocks, current);
        OrderBlockZone? bear = NearestOrderBlock(_bearOrderBlocks, current);
        AddOrderBlockDistance(metrics, bull, FeatureNames.BullOrderBlockDistanceAtr, current, atr, asOfUtc);
        AddOrderBlockDistance(metrics, bear, FeatureNames.BearOrderBlockDistanceAtr, current, atr, asOfUtc);

        OrderBlockZone? selected = new[] { bull, bear }
            .Where(static zone => zone is not null)
            .OrderBy(zone => zone!.DistanceTo(current))
            .FirstOrDefault();

        if (selected is null || formingM1 is null)
        {
            Unavailable(metrics, FeatureNames.OrderBlockOverlapPct, "pct", "order-block or forming M1 unavailable");
            return;
        }

        decimal overlapLow = Math.Max(selected.Low, formingM1.Low);
        decimal overlapHigh = Math.Min(selected.High, formingM1.High);
        decimal overlap = Math.Max(0, overlapHigh - overlapLow);
        double pct = selected.Width > 0
            ? (double)(overlap / selected.Width) * 100
            : 0;
        Add(metrics, FeatureNames.OrderBlockOverlapPct, Math.Clamp(pct, 0, 100), "pct", asOfUtc);
    }

    private static OrderBlockZone? NearestOrderBlock(List<OrderBlockZone> zones, decimal current)
    {
        return zones.Count == 0
            ? null
            : zones.OrderBy(zone => zone.DistanceTo(current)).First();
    }

    private static void AddOrderBlockDistance(
        List<StructureFeatureMetric> metrics,
        OrderBlockZone? zone,
        string name,
        decimal current,
        double atr,
        DateTimeOffset asOfUtc)
    {
        if (zone is null)
        {
            Unavailable(metrics, name, "atr", "causal order block unavailable");
            return;
        }

        Add(metrics, name, (double)(zone.DistanceTo(current) / (decimal)atr), "atr", asOfUtc);
    }

    private void AddMeanDistance(
        List<StructureFeatureMetric> metrics,
        BarTimeframe timeframe,
        string name,
        decimal current,
        DateTimeOffset asOfUtc)
    {
        double? ema = EmaCurrent(timeframe);
        double? atr = Atr(timeframe);

        if (ema is not double mean || atr is not double a || a <= 0)
        {
            Unavailable(metrics, name, "atr", $"{timeframe} EMA/ATR warmup unavailable");
            return;
        }

        Add(metrics, name, (double)((current - (decimal)mean) / (decimal)a), "atr", asOfUtc);
    }

    private double? Atr(BarTimeframe timeframe)
    {
        double[] tr = TrueRanges(_bars[timeframe]);
        return tr.Length < AtrPeriod ? null : tr.TakeLast(AtrPeriod).Average();
    }

    private double? AtrRatio(BarTimeframe timeframe)
    {
        double[] tr = TrueRanges(_bars[timeframe]);
        if (tr.Length < AtrPeriod * 2)
        {
            return null;
        }

        double current = tr.TakeLast(AtrPeriod).Average();
        double baseline = tr
            .Skip(tr.Length - (AtrPeriod * 2))
            .Take(AtrPeriod)
            .Average();

        return baseline <= 1e-12 ? null : current / baseline;
    }

    private double? CompressionScore(BarTimeframe timeframe)
    {
        double? ratio = AtrRatio(timeframe);
        return ratio is null ? null : Math.Clamp(1 - ratio.Value, 0, 1);
    }

    private double? Adx(BarTimeframe timeframe)
    {
        List<ClosedBarState> bars = _bars[timeframe];
        if (bars.Count < (AdxPeriod * 2) + 1)
        {
            return null;
        }

        var dx = new List<double>();
        for (int end = AdxPeriod; end < bars.Count; end++)
        {
            int start = end - AdxPeriod + 1;
            double trSum = 0;
            double plusDm = 0;
            double minusDm = 0;

            for (int index = start; index <= end; index++)
            {
                ClosedBarState current = bars[index];
                ClosedBarState previous = bars[index - 1];
                trSum += TrueRange(current, previous.Close);

                double upMove = (double)(current.High - previous.High);
                double downMove = (double)(previous.Low - current.Low);
                plusDm += upMove > downMove && upMove > 0 ? upMove : 0;
                minusDm += downMove > upMove && downMove > 0 ? downMove : 0;
            }

            if (trSum <= 1e-12)
            {
                dx.Add(0);
                continue;
            }

            double plusDi = 100 * plusDm / trSum;
            double minusDi = 100 * minusDm / trSum;
            double denom = plusDi + minusDi;
            dx.Add(denom <= 1e-12 ? 0 : 100 * Math.Abs(plusDi - minusDi) / denom);
        }

        return dx.Count < AdxPeriod ? null : dx.TakeLast(AdxPeriod).Average();
    }

    private double? EmaCurrent(BarTimeframe timeframe)
    {
        double[] series = EmaSeries(timeframe);
        return series.Length < EmaPeriod ? null : series[^1];
    }

    private double? EmaSlope(BarTimeframe timeframe)
    {
        double[] ema = EmaSeries(timeframe);
        double? atr = Atr(timeframe);

        if (ema.Length < EmaPeriod + 5 || atr is not double a || a <= 0)
        {
            return null;
        }

        return ((ema[^1] - ema[^6]) / a) / 5.0;
    }

    private double[] EmaSeries(BarTimeframe timeframe)
    {
        List<ClosedBarState> bars = _bars[timeframe];
        if (bars.Count == 0)
        {
            return [];
        }

        double alpha = 2.0 / (EmaPeriod + 1);
        var result = new double[bars.Count];
        result[0] = decimal.ToDouble(bars[0].Close);

        for (int index = 1; index < bars.Count; index++)
        {
            double close = decimal.ToDouble(bars[index].Close);
            result[index] = (alpha * close) + ((1 - alpha) * result[index - 1]);
        }

        return result;
    }

    private static int? DirectionState(double? slope)
    {
        if (slope is not double value)
        {
            return null;
        }

        if (value > 0.01)
        {
            return 1;
        }

        if (value < -0.01)
        {
            return -1;
        }

        return 0;
    }

    private static double[] TrueRanges(List<ClosedBarState> bars)
    {
        if (bars.Count == 0)
        {
            return [];
        }

        var result = new double[bars.Count];
        result[0] = decimal.ToDouble(bars[0].High - bars[0].Low);

        for (int index = 1; index < bars.Count; index++)
        {
            result[index] = TrueRange(bars[index], bars[index - 1].Close);
        }

        return result;
    }

    private static double TrueRange(ClosedBarState current, decimal previousClose)
    {
        decimal range = current.High - current.Low;
        decimal gapHigh = Math.Abs(current.High - previousClose);
        decimal gapLow = Math.Abs(current.Low - previousClose);
        return decimal.ToDouble(Math.Max(range, Math.Max(gapHigh, gapLow)));
    }

    private static void AddOptional(
        List<StructureFeatureMetric> metrics,
        string name,
        double? value,
        string unit,
        DateTimeOffset? observedAtUtc,
        string unavailableReason)
    {
        if (value is double numeric && double.IsFinite(numeric))
        {
            Add(metrics, name, numeric, unit, observedAtUtc ?? DateTimeOffset.UnixEpoch);
        }
        else
        {
            Unavailable(metrics, name, unit, unavailableReason);
        }
    }

    private static void Add(
        List<StructureFeatureMetric> metrics,
        string name,
        double value,
        string unit,
        DateTimeOffset observedAtUtc)
    {
        metrics.Add(new StructureFeatureMetric(name, value, unit, observedAtUtc, null));
    }

    private static void Unavailable(
        List<StructureFeatureMetric> metrics,
        string name,
        string unit,
        string reason)
    {
        metrics.Add(new StructureFeatureMetric(name, null, unit, null, reason));
    }

    private sealed record SwingLevel(decimal Price, DateTimeOffset SourceOpenTimeUtc, DateTimeOffset ConfirmedAtUtc);

    private sealed record StructureBreak(int Direction, DateTimeOffset AtUtc, double? DistanceAtr);

    private sealed class SqueezeState
    {
        public SqueezeState(bool isSqueeze, DateTimeOffset? lastReleaseUtc)
        {
            IsSqueeze = isSqueeze;
            LastReleaseUtc = lastReleaseUtc;
        }

        public bool IsSqueeze { get; set; }

        public DateTimeOffset? LastReleaseUtc { get; set; }
    }

    private sealed class GapZone
    {
        public GapZone(int direction, decimal lower, decimal upper, DateTimeOffset createdAtUtc)
        {
            Direction = direction;
            Lower = lower;
            Upper = upper;
            CreatedAtUtc = createdAtUtc;
        }

        public int Direction { get; }

        public decimal Lower { get; }

        public decimal Upper { get; }

        public DateTimeOffset CreatedAtUtc { get; }

        public decimal Size => Upper - Lower;

        public double FillPct { get; private set; }

        public void UpdateFill(decimal current)
        {
            if (Size <= 0)
            {
                return;
            }

            double fill = Direction > 0
                ? (double)((Upper - current) / Size)
                : (double)((current - Lower) / Size);

            FillPct = Math.Max(FillPct, Math.Clamp(fill, 0, 1));
        }

        public decimal DistanceTo(decimal current)
        {
            if (current < Lower)
            {
                return Lower - current;
            }

            if (current > Upper)
            {
                return current - Upper;
            }

            return 0;
        }
    }

    private sealed record OrderBlockZone(decimal Low, decimal High, DateTimeOffset CreatedAtUtc)
    {
        public decimal Width => High - Low;

        public decimal DistanceTo(decimal current)
        {
            if (current < Low)
            {
                return Low - current;
            }

            if (current > High)
            {
                return current - High;
            }

            return 0;
        }
    }
}

public static class StructureRegimeFeatureNames
{
    public static IReadOnlyList<string> All { get; } =
    [
        FeatureNames.BosDirection,
        FeatureNames.BosDistanceAtr,
        FeatureNames.BosAgeSec,
        FeatureNames.MssDirection,
        FeatureNames.MssAgeSec,
        FeatureNames.ChochDirection,
        FeatureNames.ChochAgeSec,
        FeatureNames.BullFvgDistanceAtr,
        FeatureNames.BearFvgDistanceAtr,
        FeatureNames.FvgSizeAtr,
        FeatureNames.FvgAgeSec,
        FeatureNames.FvgFillPct,
        FeatureNames.BullOrderBlockDistanceAtr,
        FeatureNames.BearOrderBlockDistanceAtr,
        FeatureNames.OrderBlockOverlapPct,
        FeatureNames.DisplacementRangeAtr,
        FeatureNames.DisplacementBodyRatio,
        FeatureNames.AtrM1,
        FeatureNames.AtrM5,
        FeatureNames.AtrRatioM1,
        FeatureNames.AtrRatioM5,
        FeatureNames.AdxM1,
        FeatureNames.AdxM5,
        FeatureNames.M1CompressionScore,
        FeatureNames.M5CompressionScore,
        FeatureNames.M1Squeeze,
        FeatureNames.M5Squeeze,
        FeatureNames.SecondsSinceM1SqueezeRelease,
        FeatureNames.SecondsSinceM5SqueezeRelease,
        FeatureNames.EmaSlopeM5,
        FeatureNames.EmaSlopeM15,
        FeatureNames.EmaSlopeH1,
        FeatureNames.TrendAlignmentScore,
        FeatureNames.M5DirectionState,
        FeatureNames.M15DirectionState,
        FeatureNames.H1DirectionState,
        FeatureNames.M5VolatilityState,
        FeatureNames.M15StructureState,
        FeatureNames.DistanceToM5MeanAtr,
        FeatureNames.DistanceToM15MeanAtr,
        FeatureNames.HtfAlignedUp,
        FeatureNames.HtfAlignedDown,
        FeatureNames.HtfConflict,
    ];
}
