using XauScalp.Domain;

namespace XauScalp.Features;

public sealed record LiquidityTickInputs(
    double? AtrM1,
    double? Velocity500Ms,
    double? PeakVelocityUp,
    double? PeakVelocityDown,
    double? DecelerationRatio,
    double? DirectionFlipAgeMs,
    double WickBodyRatio);

public sealed record LiquidityFeatureMetric(
    string Name,
    double? Value,
    string Unit,
    DateTimeOffset? ObservedAtUtc,
    string? UnavailableReason)
{
    public bool IsAvailable => Value is not null;
}

public sealed class CausalLiquidityTracker
{
    private const int SwingLeftRightBars = 2;
    private const int MaxClosedBars = 256;
    private const int MaxSwings = 128;
    private static readonly TimeSpan ReactionRetention = TimeSpan.FromSeconds(60);

    private readonly List<ClosedBarState> _closedM1 = [];
    private readonly List<SwingLevel> _swingHighs = [];
    private readonly List<SwingLevel> _swingLows = [];
    private readonly Queue<double> _tickVolumes = new();

    private DateOnly? _currentDay;
    private decimal _currentDayHigh;
    private decimal _currentDayLow;
    private DateOnly? _currentWeekStart;
    private decimal _currentWeekHigh;
    private decimal _currentWeekLow;
    private decimal? _previousDayHigh;
    private decimal? _previousDayLow;
    private decimal? _previousWeekHigh;
    private decimal? _previousWeekLow;
    private decimal? _previousMid;
    private ReactionState? _reaction;

    public void ObserveClosedM1(ClosedBarState bar)
    {
        ArgumentNullException.ThrowIfNull(bar);

        if (bar.Timeframe != BarTimeframe.M1)
        {
            throw new ArgumentException("Liquidity swing confirmation accepts closed M1 bars only.", nameof(bar));
        }

        if (_closedM1.Count > 0 && bar.CloseTimeUtc <= _closedM1[^1].CloseTimeUtc)
        {
            throw new InvalidDataException("Closed M1 bars must be observed once in strictly increasing close-time order.");
        }

        _closedM1.Add(bar);
        if (_closedM1.Count > MaxClosedBars)
        {
            _closedM1.RemoveAt(0);
        }

        ConfirmNewestEligibleSwing();
    }

    public void UpdateTick(TickEvent tick, LiquidityTickInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentNullException.ThrowIfNull(inputs);

        decimal mid = (tick.Bid + tick.Ask) / 2m;
        UpdatePeriodLevels(tick.TimestampUtc, mid);
        double volumeRatio = UpdateVolumeRatio(tick.TickVolume);

        if (inputs.AtrM1 is not double atr || !double.IsFinite(atr) || atr <= 0)
        {
            _previousMid = mid;
            return;
        }

        decimal previous = _previousMid ?? mid;
        decimal? crossedUpper = FindCrossedLevel(previous, mid, upper: true);
        decimal? crossedLower = FindCrossedLevel(previous, mid, upper: false);
        decimal touchTolerance = (decimal)atr * 0.05m;

        if (crossedUpper is decimal upper)
        {
            _reaction = StartReaction(
                upper,
                direction: 1,
                tick,
                inputs,
                volumeRatio);
        }
        else if (crossedLower is decimal lower)
        {
            _reaction = StartReaction(
                lower,
                direction: -1,
                tick,
                inputs,
                volumeRatio);
        }
        else if (_reaction is null || tick.TimestampUtc - _reaction.TouchAtUtc > ReactionRetention)
        {
            decimal? touchUpper = NearestLevel(mid, upper: true);
            decimal? touchLower = NearestLevel(mid, upper: false);

            if (touchUpper is decimal upperLevel && Math.Abs(upperLevel - mid) <= touchTolerance)
            {
                _reaction = StartReaction(upperLevel, 1, tick, inputs, volumeRatio);
            }
            else if (touchLower is decimal lowerLevel && Math.Abs(mid - lowerLevel) <= touchTolerance)
            {
                _reaction = StartReaction(lowerLevel, -1, tick, inputs, volumeRatio);
            }
        }

        UpdateReaction(tick, inputs, atr);
        _previousMid = mid;
    }

    public IReadOnlyList<LiquidityFeatureMetric> Snapshot(
        DateTimeOffset asOfUtc,
        decimal currentMid,
        double? atrM1)
    {
        if (atrM1 is not double atr || !double.IsFinite(atr) || atr <= 0)
        {
            return UnavailableSnapshot("causal M1 ATR unavailable");
        }

        var metrics = new List<LiquidityFeatureMetric>(40);
        decimal atrPrice = (decimal)atr;

        SwingLevel? nearestHigh = NearestSwing(_swingHighs, currentMid);
        SwingLevel? nearestLow = NearestSwing(_swingLows, currentMid);

        AddOptionalDistance(metrics, FeatureNames.NearestSwingHighDistanceAtr, nearestHigh?.Price, currentMid, atrPrice, upper: true, asOfUtc);
        AddOptionalDistance(metrics, FeatureNames.NearestSwingLowDistanceAtr, nearestLow?.Price, currentMid, atrPrice, upper: false, asOfUtc);

        bool swingsReady = _swingHighs.Count > 0 || _swingLows.Count > 0;
        if (swingsReady)
        {
            Add(metrics, FeatureNames.EqualHighStrength, EqualStrength(_swingHighs, atrPrice), "score", asOfUtc);
            Add(metrics, FeatureNames.EqualLowStrength, EqualStrength(_swingLows, atrPrice), "score", asOfUtc);
        }
        else
        {
            Unavailable(metrics, FeatureNames.EqualHighStrength, "score", "confirmed swing history unavailable");
            Unavailable(metrics, FeatureNames.EqualLowStrength, "score", "confirmed swing history unavailable");
        }

        AddPeriodDistances(metrics, currentMid, atrPrice, asOfUtc);

        MagnetSnapshot upperMagnet = Magnet(currentMid, atrPrice, upper: true);
        MagnetSnapshot lowerMagnet = Magnet(currentMid, atrPrice, upper: false);

        AddMagnet(metrics, upperMagnet, FeatureNames.UpperMagnetScore, FeatureNames.UpperMagnetDistanceAtr, asOfUtc);
        AddMagnet(metrics, lowerMagnet, FeatureNames.LowerMagnetScore, FeatureNames.LowerMagnetDistanceAtr, asOfUtc);

        if (upperMagnet.Available && lowerMagnet.Available)
        {
            double pullUp = upperMagnet.Score / (0.25 + upperMagnet.DistanceAtr);
            double pullDown = lowerMagnet.Score / (0.25 + lowerMagnet.DistanceAtr);
            Add(metrics, FeatureNames.PullUp, pullUp, "score", asOfUtc);
            Add(metrics, FeatureNames.PullDown, pullDown, "score", asOfUtc);
            Add(metrics, FeatureNames.PullDelta, pullUp - pullDown, "score", asOfUtc);
            Add(metrics, FeatureNames.VacuumUpWidthAtr, upperMagnet.DistanceAtr, "atr", asOfUtc);
            Add(metrics, FeatureNames.VacuumDownWidthAtr, lowerMagnet.DistanceAtr, "atr", asOfUtc);
            Add(
                metrics,
                FeatureNames.InsideVacuum,
                upperMagnet.DistanceAtr >= 0.75 && lowerMagnet.DistanceAtr >= 0.75 ? 1 : 0,
                "boolean",
                asOfUtc);
        }
        else
        {
            foreach (string name in new[]
            {
                FeatureNames.PullUp,
                FeatureNames.PullDown,
                FeatureNames.PullDelta,
                FeatureNames.VacuumUpWidthAtr,
                FeatureNames.VacuumDownWidthAtr,
                FeatureNames.InsideVacuum,
            })
            {
                Unavailable(metrics, name, name == FeatureNames.InsideVacuum ? "boolean" : "score", "two-sided estimated liquidity levels unavailable");
            }
        }

        AddReactionMetrics(metrics, asOfUtc, atr);

        return metrics;
    }

    private void ConfirmNewestEligibleSwing()
    {
        int count = _closedM1.Count;
        if (count < (SwingLeftRightBars * 2) + 1)
        {
            return;
        }

        int candidateIndex = count - SwingLeftRightBars - 1;
        ClosedBarState candidate = _closedM1[candidateIndex];

        bool swingHigh = true;
        bool swingLow = true;

        for (int offset = -SwingLeftRightBars; offset <= SwingLeftRightBars; offset++)
        {
            if (offset == 0)
            {
                continue;
            }

            ClosedBarState neighbor = _closedM1[candidateIndex + offset];
            swingHigh &= candidate.High > neighbor.High;
            swingLow &= candidate.Low < neighbor.Low;
        }

        DateTimeOffset confirmedAtUtc = _closedM1[^1].CloseTimeUtc;

        if (swingHigh)
        {
            AddSwing(_swingHighs, new SwingLevel(candidate.High, candidate.OpenTimeUtc, confirmedAtUtc));
        }

        if (swingLow)
        {
            AddSwing(_swingLows, new SwingLevel(candidate.Low, candidate.OpenTimeUtc, confirmedAtUtc));
        }
    }

    private static void AddSwing(List<SwingLevel> swings, SwingLevel level)
    {
        if (swings.Any(existing => existing.SourceOpenTimeUtc == level.SourceOpenTimeUtc))
        {
            return;
        }

        swings.Add(level);
        if (swings.Count > MaxSwings)
        {
            swings.RemoveAt(0);
        }
    }

    private void UpdatePeriodLevels(DateTimeOffset timestampUtc, decimal mid)
    {
        DateOnly day = DateOnly.FromDateTime(timestampUtc.UtcDateTime);
        DateOnly weekStart = StartOfIsoWeek(day);

        if (_currentDay is null)
        {
            _currentDay = day;
            _currentDayHigh = mid;
            _currentDayLow = mid;
        }
        else if (_currentDay.Value != day)
        {
            _previousDayHigh = _currentDayHigh;
            _previousDayLow = _currentDayLow;
            _currentDay = day;
            _currentDayHigh = mid;
            _currentDayLow = mid;
        }
        else
        {
            _currentDayHigh = Math.Max(_currentDayHigh, mid);
            _currentDayLow = Math.Min(_currentDayLow, mid);
        }

        if (_currentWeekStart is null)
        {
            _currentWeekStart = weekStart;
            _currentWeekHigh = mid;
            _currentWeekLow = mid;
        }
        else if (_currentWeekStart.Value != weekStart)
        {
            _previousWeekHigh = _currentWeekHigh;
            _previousWeekLow = _currentWeekLow;
            _currentWeekStart = weekStart;
            _currentWeekHigh = mid;
            _currentWeekLow = mid;
        }
        else
        {
            _currentWeekHigh = Math.Max(_currentWeekHigh, mid);
            _currentWeekLow = Math.Min(_currentWeekLow, mid);
        }
    }

    private static DateOnly StartOfIsoWeek(DateOnly day)
    {
        int delta = ((int)day.DayOfWeek + 6) % 7;
        return day.AddDays(-delta);
    }

    private double UpdateVolumeRatio(double? tickVolume)
    {
        if (tickVolume is not double current || !double.IsFinite(current) || current < 0)
        {
            return 1;
        }

        double baseline = _tickVolumes.Count == 0 ? current : _tickVolumes.Average();
        _tickVolumes.Enqueue(current);
        while (_tickVolumes.Count > 50)
        {
            _tickVolumes.Dequeue();
        }

        return baseline <= 1e-12 ? 1 : current / baseline;
    }

    private ReactionState StartReaction(
        decimal level,
        int direction,
        TickEvent tick,
        LiquidityTickInputs inputs,
        double volumeRatio)
    {
        double velocityInto = direction > 0
            ? Math.Max(inputs.Velocity500Ms ?? 0, 0)
            : Math.Max(-(inputs.Velocity500Ms ?? 0), 0);

        double peak = direction > 0
            ? inputs.PeakVelocityUp ?? velocityInto
            : inputs.PeakVelocityDown ?? velocityInto;

        return new ReactionState(
            level,
            direction,
            tick.TimestampUtc,
            velocityInto,
            Math.Max(peak, 0),
            inputs.WickBodyRatio,
            volumeRatio);
    }

    private void UpdateReaction(TickEvent tick, LiquidityTickInputs inputs, double atr)
    {
        if (_reaction is null)
        {
            return;
        }

        if (tick.TimestampUtc - _reaction.TouchAtUtc > ReactionRetention)
        {
            _reaction = null;
            return;
        }

        decimal mid = (tick.Bid + tick.Ask) / 2m;
        decimal atrPrice = (decimal)atr;
        decimal outward = _reaction.Direction > 0
            ? mid - _reaction.Level
            : _reaction.Level - mid;

        if (outward > 0)
        {
            _reaction.SweepOccurred = true;
            _reaction.SweepDepthPrice = Math.Max(_reaction.SweepDepthPrice, outward);
        }

        decimal inside = _reaction.Direction > 0
            ? _reaction.Level - mid
            : mid - _reaction.Level;

        if (_reaction.SweepOccurred && inside >= 0)
        {
            _reaction.CloseBackInsidePrice = Math.Max(_reaction.CloseBackInsidePrice, inside);
        }

        if (inputs.DecelerationRatio is double deceleration && double.IsFinite(deceleration))
        {
            _reaction.DecelerationAfterTouch = Math.Clamp(deceleration, 0, 1);
        }

        if (inputs.DirectionFlipAgeMs is double flipAge
            && double.IsFinite(flipAge)
            && flipAge >= 0)
        {
            DateTimeOffset flipAt = tick.TimestampUtc - TimeSpan.FromMilliseconds(flipAge);
            if (flipAt >= _reaction.TouchAtUtc)
            {
                _reaction.DirectionFlipAfterTouch = true;
                _reaction.DirectionFlipDelayMs = (flipAt - _reaction.TouchAtUtc).TotalMilliseconds;
            }
        }

        UpdateMicroRetest(_reaction, mid, atrPrice, inputs.Velocity500Ms ?? 0);
    }

    private static void UpdateMicroRetest(
        ReactionState reaction,
        decimal mid,
        decimal atr,
        double velocity500Ms)
    {
        if (!reaction.SweepOccurred || reaction.CloseBackInsidePrice <= 0)
        {
            return;
        }

        decimal insideDistance = reaction.Direction > 0
            ? reaction.Level - mid
            : mid - reaction.Level;

        if (reaction.MicroStage == 0 && insideDistance >= atr * 0.10m)
        {
            reaction.MicroStage = 1;
            reaction.AwayExtreme = mid;
            return;
        }

        if (reaction.MicroStage == 1)
        {
            reaction.AwayExtreme = reaction.Direction > 0
                ? Math.Min(reaction.AwayExtreme, mid)
                : Math.Max(reaction.AwayExtreme, mid);

            decimal distanceToLevel = Math.Abs(reaction.Level - mid);
            if (distanceToLevel <= atr * 0.05m)
            {
                reaction.MicroStage = 2;
                reaction.RetestPrice = mid;
                reaction.MicroRetestDepthPrice = distanceToLevel;
            }

            return;
        }

        if (reaction.MicroStage == 2)
        {
            decimal resumeDistance = reaction.Direction > 0
                ? reaction.RetestPrice - mid
                : mid - reaction.RetestPrice;

            if (resumeDistance >= atr * 0.10m)
            {
                reaction.MicroRetestOccurred = true;
                reaction.MicroStage = 3;
                reaction.ResumeVelocity = reaction.Direction > 0
                    ? -velocity500Ms
                    : velocity500Ms;
            }
        }
    }

    private decimal? FindCrossedLevel(decimal previous, decimal current, bool upper)
    {
        IEnumerable<decimal> levels = CandidateLevels(upper);

        return upper
            ? levels.Where(level => previous <= level && current > level)
                .OrderBy(level => Math.Abs(level - previous))
                .Select(static level => (decimal?)level)
                .FirstOrDefault()
            : levels.Where(level => previous >= level && current < level)
                .OrderBy(level => Math.Abs(level - previous))
                .Select(static level => (decimal?)level)
                .FirstOrDefault();
    }

    private decimal? NearestLevel(decimal current, bool upper)
    {
        IEnumerable<decimal> candidates = CandidateLevels(upper)
            .Where(level => upper ? level >= current : level <= current);

        return candidates
            .OrderBy(level => Math.Abs(level - current))
            .Select(static level => (decimal?)level)
            .FirstOrDefault();
    }

    private IEnumerable<decimal> CandidateLevels(bool upper)
    {
        IEnumerable<decimal> swings = upper
            ? _swingHighs.Select(static swing => swing.Price)
            : _swingLows.Select(static swing => swing.Price);

        IEnumerable<decimal?> period = upper
            ? [_previousDayHigh, _previousWeekHigh]
            : [_previousDayLow, _previousWeekLow];

        return swings.Concat(period.Where(static value => value is not null).Select(static value => value!.Value));
    }

    private static SwingLevel? NearestSwing(List<SwingLevel> swings, decimal current)
    {
        return swings.Count == 0
            ? null
            : swings.OrderBy(swing => Math.Abs(swing.Price - current)).First();
    }

    private static double EqualStrength(List<SwingLevel> swings, decimal atr)
    {
        if (swings.Count < 2)
        {
            return 0;
        }

        decimal tolerance = atr * 0.10m;
        int best = 1;

        foreach (SwingLevel anchor in swings.TakeLast(20))
        {
            int count = swings.TakeLast(20)
                .Count(other => Math.Abs(other.Price - anchor.Price) <= tolerance);
            best = Math.Max(best, count);
        }

        return Math.Clamp((best - 1) / 2.0, 0, 1);
    }

    private MagnetSnapshot Magnet(decimal current, decimal atr, bool upper)
    {
        decimal[] candidates = CandidateLevels(upper)
            .Where(level => upper ? level > current : level < current)
            .Distinct()
            .ToArray();

        if (candidates.Length == 0)
        {
            return MagnetSnapshot.Unavailable;
        }

        decimal level = candidates.OrderBy(value => Math.Abs(value - current)).First();
        double distance = (double)(Math.Abs(level - current) / atr);
        double equal = upper
            ? EqualStrength(_swingHighs, atr)
            : EqualStrength(_swingLows, atr);
        double score = Math.Clamp((1.0 / (1.0 + distance)) * (0.7 + (0.3 * equal)), 0, 1);

        return new MagnetSnapshot(true, distance, score);
    }

    private void AddPeriodDistances(
        List<LiquidityFeatureMetric> metrics,
        decimal current,
        decimal atr,
        DateTimeOffset asOfUtc)
    {
        AddOptionalDistance(metrics, FeatureNames.PdhDistanceAtr, _previousDayHigh, current, atr, true, asOfUtc);
        AddOptionalDistance(metrics, FeatureNames.PdlDistanceAtr, _previousDayLow, current, atr, false, asOfUtc);
        AddOptionalDistance(metrics, FeatureNames.PwhDistanceAtr, _previousWeekHigh, current, atr, true, asOfUtc);
        AddOptionalDistance(metrics, FeatureNames.PwlDistanceAtr, _previousWeekLow, current, atr, false, asOfUtc);
    }

    private void AddReactionMetrics(
        List<LiquidityFeatureMetric> metrics,
        DateTimeOffset asOfUtc,
        double atr)
    {
        ReactionState? reaction = _reaction is not null
            && asOfUtc >= _reaction.TouchAtUtc
            && asOfUtc - _reaction.TouchAtUtc <= ReactionRetention
            ? _reaction
            : null;

        if (reaction is null)
        {
            Add(metrics, FeatureNames.BuySideSweepDepthAtr, 0, "atr", asOfUtc);
            Add(metrics, FeatureNames.SellSideSweepDepthAtr, 0, "atr", asOfUtc);
            Add(metrics, FeatureNames.CloseBackInsideDistanceAtr, 0, "atr", asOfUtc);
            Add(metrics, FeatureNames.SweepOccurred, 0, "boolean", asOfUtc);
            Add(metrics, FeatureNames.AbsorptionUpScore, 0, "score", asOfUtc);
            Add(metrics, FeatureNames.AbsorptionDownScore, 0, "score", asOfUtc);

            foreach ((string Name, string Unit) item in new[]
            {
                (FeatureNames.LiquidityTouchAgeMs, "ms"),
                (FeatureNames.SweepDirection, "direction"),
                (FeatureNames.SweepDepthAtr, "atr"),
                (FeatureNames.WickBodyRatioAtSweep, "ratio"),
                (FeatureNames.CloseBackInsideAtr, "atr"),
                (FeatureNames.VolumeRatioAtSweep, "ratio"),
                (FeatureNames.VelocityIntoLevel, "pricePerSecond"),
                (FeatureNames.PeakVelocityAtLevel, "pricePerSecond"),
                (FeatureNames.DecelerationAfterTouch, "ratio"),
                (FeatureNames.DirectionFlipAfterTouch, "boolean"),
                (FeatureNames.DirectionFlipDelayMs, "ms"),
                (FeatureNames.MicroRetestOccurred, "boolean"),
                (FeatureNames.MicroRetestDepthAtr, "atr"),
                (FeatureNames.ResumeVelocity, "pricePerSecond"),
            })
            {
                Unavailable(metrics, item.Name, item.Unit, "no recent liquidity touch");
            }

            return;
        }

        double sweepDepthAtr = (double)reaction.SweepDepthPrice / atr;
        double closeBackAtr = (double)reaction.CloseBackInsidePrice / atr;
        Add(metrics, FeatureNames.BuySideSweepDepthAtr, reaction.Direction > 0 ? sweepDepthAtr : 0, "atr", asOfUtc);
        Add(metrics, FeatureNames.SellSideSweepDepthAtr, reaction.Direction < 0 ? sweepDepthAtr : 0, "atr", asOfUtc);
        Add(metrics, FeatureNames.CloseBackInsideDistanceAtr, closeBackAtr, "atr", asOfUtc);
        Add(metrics, FeatureNames.LiquidityTouchAgeMs, (asOfUtc - reaction.TouchAtUtc).TotalMilliseconds, "ms", asOfUtc);
        Add(metrics, FeatureNames.SweepOccurred, reaction.SweepOccurred ? 1 : 0, "boolean", asOfUtc);
        Add(metrics, FeatureNames.SweepDirection, reaction.SweepOccurred ? reaction.Direction : 0, "direction", asOfUtc);
        Add(metrics, FeatureNames.SweepDepthAtr, sweepDepthAtr, "atr", asOfUtc);
        Add(metrics, FeatureNames.WickBodyRatioAtSweep, reaction.WickBodyRatioAtTouch, "ratio", reaction.TouchAtUtc);
        Add(metrics, FeatureNames.CloseBackInsideAtr, closeBackAtr, "atr", asOfUtc);
        Add(metrics, FeatureNames.VolumeRatioAtSweep, reaction.VolumeRatioAtTouch, "ratio", reaction.TouchAtUtc);
        Add(metrics, FeatureNames.VelocityIntoLevel, reaction.VelocityIntoLevel, "pricePerSecond", reaction.TouchAtUtc);
        Add(metrics, FeatureNames.PeakVelocityAtLevel, reaction.PeakVelocityAtLevel, "pricePerSecond", reaction.TouchAtUtc);
        Add(metrics, FeatureNames.DecelerationAfterTouch, reaction.DecelerationAfterTouch, "ratio", asOfUtc);
        Add(metrics, FeatureNames.DirectionFlipAfterTouch, reaction.DirectionFlipAfterTouch ? 1 : 0, "boolean", asOfUtc);

        if (reaction.DirectionFlipDelayMs is double flipDelay)
        {
            Add(metrics, FeatureNames.DirectionFlipDelayMs, flipDelay, "ms", asOfUtc);
        }
        else
        {
            Unavailable(metrics, FeatureNames.DirectionFlipDelayMs, "ms", "no post-touch direction flip");
        }

        Add(metrics, FeatureNames.MicroRetestOccurred, reaction.MicroRetestOccurred ? 1 : 0, "boolean", asOfUtc);
        Add(metrics, FeatureNames.MicroRetestDepthAtr, (double)reaction.MicroRetestDepthPrice / atr, "atr", asOfUtc);

        if (reaction.MicroRetestOccurred)
        {
            Add(metrics, FeatureNames.ResumeVelocity, reaction.ResumeVelocity, "pricePerSecond", asOfUtc);
        }
        else
        {
            Unavailable(metrics, FeatureNames.ResumeVelocity, "pricePerSecond", "micro-retest resume not observed");
        }

        double volumeEvidence = Math.Clamp((reaction.VolumeRatioAtTouch - 1) / 2.0, 0, 1);
        double absorption = Math.Clamp(
            (0.45 * reaction.DecelerationAfterTouch)
            + (0.35 * Math.Clamp(closeBackAtr, 0, 1))
            + (0.20 * volumeEvidence),
            0,
            1);

        Add(metrics, FeatureNames.AbsorptionUpScore, reaction.Direction > 0 ? absorption : 0, "score", asOfUtc);
        Add(metrics, FeatureNames.AbsorptionDownScore, reaction.Direction < 0 ? absorption : 0, "score", asOfUtc);
    }

    private static void AddOptionalDistance(
        List<LiquidityFeatureMetric> metrics,
        string name,
        decimal? level,
        decimal current,
        decimal atr,
        bool upper,
        DateTimeOffset asOfUtc)
    {
        if (level is null)
        {
            Unavailable(metrics, name, "atr", "causal level unavailable");
            return;
        }

        double distance = upper
            ? (double)((level.Value - current) / atr)
            : (double)((current - level.Value) / atr);
        Add(metrics, name, distance, "atr", asOfUtc);
    }

    private static void AddMagnet(
        List<LiquidityFeatureMetric> metrics,
        MagnetSnapshot magnet,
        string scoreName,
        string distanceName,
        DateTimeOffset asOfUtc)
    {
        if (!magnet.Available)
        {
            Unavailable(metrics, scoreName, "score", "estimated magnet unavailable");
            Unavailable(metrics, distanceName, "atr", "estimated magnet unavailable");
            return;
        }

        Add(metrics, scoreName, magnet.Score, "score", asOfUtc);
        Add(metrics, distanceName, magnet.DistanceAtr, "atr", asOfUtc);
    }

    private IReadOnlyList<LiquidityFeatureMetric> UnavailableSnapshot(string reason)
    {
        return LiquidityFeatureNames.All
            .Select(name => new LiquidityFeatureMetric(name, null, UnitFor(name), null, reason))
            .ToArray();
    }

    private static string UnitFor(string name)
    {
        if (name.EndsWith("AgeMs", StringComparison.Ordinal)
            || name.EndsWith("DelayMs", StringComparison.Ordinal))
        {
            return "ms";
        }

        if (name.Contains("Velocity", StringComparison.Ordinal))
        {
            return "pricePerSecond";
        }

        if (name.Contains("Occurred", StringComparison.Ordinal)
            || name == FeatureNames.DirectionFlipAfterTouch
            || name == FeatureNames.InsideVacuum)
        {
            return "boolean";
        }

        if (name.Contains("Score", StringComparison.Ordinal)
            || name.StartsWith("Pull", StringComparison.Ordinal))
        {
            return "score";
        }

        if (name.Contains("Direction", StringComparison.Ordinal))
        {
            return "direction";
        }

        if (name.Contains("Ratio", StringComparison.Ordinal))
        {
            return "ratio";
        }

        return "atr";
    }

    private static void Add(
        List<LiquidityFeatureMetric> metrics,
        string name,
        double value,
        string unit,
        DateTimeOffset observedAtUtc)
    {
        metrics.Add(new LiquidityFeatureMetric(name, value, unit, observedAtUtc, null));
    }

    private static void Unavailable(
        List<LiquidityFeatureMetric> metrics,
        string name,
        string unit,
        string reason)
    {
        metrics.Add(new LiquidityFeatureMetric(name, null, unit, null, reason));
    }

    private sealed record SwingLevel(
        decimal Price,
        DateTimeOffset SourceOpenTimeUtc,
        DateTimeOffset ConfirmedAtUtc);

    private sealed class ReactionState
    {
        public ReactionState(
            decimal level,
            int direction,
            DateTimeOffset touchAtUtc,
            double velocityIntoLevel,
            double peakVelocityAtLevel,
            double wickBodyRatioAtTouch,
            double volumeRatioAtTouch)
        {
            Level = level;
            Direction = direction;
            TouchAtUtc = touchAtUtc;
            VelocityIntoLevel = velocityIntoLevel;
            PeakVelocityAtLevel = peakVelocityAtLevel;
            WickBodyRatioAtTouch = wickBodyRatioAtTouch;
            VolumeRatioAtTouch = volumeRatioAtTouch;
            AwayExtreme = level;
            RetestPrice = level;
        }

        public decimal Level { get; }

        public int Direction { get; }

        public DateTimeOffset TouchAtUtc { get; }

        public double VelocityIntoLevel { get; }

        public double PeakVelocityAtLevel { get; }

        public double WickBodyRatioAtTouch { get; }

        public double VolumeRatioAtTouch { get; }

        public bool SweepOccurred { get; set; }

        public decimal SweepDepthPrice { get; set; }

        public decimal CloseBackInsidePrice { get; set; }

        public double DecelerationAfterTouch { get; set; }

        public bool DirectionFlipAfterTouch { get; set; }

        public double? DirectionFlipDelayMs { get; set; }

        public int MicroStage { get; set; }

        public decimal AwayExtreme { get; set; }

        public decimal RetestPrice { get; set; }

        public bool MicroRetestOccurred { get; set; }

        public decimal MicroRetestDepthPrice { get; set; }

        public double ResumeVelocity { get; set; }
    }

    private sealed record MagnetSnapshot(bool Available, double DistanceAtr, double Score)
    {
        public static MagnetSnapshot Unavailable { get; } = new(false, 0, 0);
    }
}

public static class LiquidityFeatureNames
{
    public static IReadOnlyList<string> All { get; } =
    [
        FeatureNames.NearestSwingHighDistanceAtr,
        FeatureNames.NearestSwingLowDistanceAtr,
        FeatureNames.BuySideSweepDepthAtr,
        FeatureNames.SellSideSweepDepthAtr,
        FeatureNames.CloseBackInsideDistanceAtr,
        FeatureNames.EqualHighStrength,
        FeatureNames.EqualLowStrength,
        FeatureNames.UpperMagnetScore,
        FeatureNames.LowerMagnetScore,
        FeatureNames.UpperMagnetDistanceAtr,
        FeatureNames.LowerMagnetDistanceAtr,
        FeatureNames.PullUp,
        FeatureNames.PullDown,
        FeatureNames.PullDelta,
        FeatureNames.InsideVacuum,
        FeatureNames.VacuumUpWidthAtr,
        FeatureNames.VacuumDownWidthAtr,
        FeatureNames.AbsorptionUpScore,
        FeatureNames.AbsorptionDownScore,
        FeatureNames.PdhDistanceAtr,
        FeatureNames.PdlDistanceAtr,
        FeatureNames.PwhDistanceAtr,
        FeatureNames.PwlDistanceAtr,
        FeatureNames.LiquidityTouchAgeMs,
        FeatureNames.SweepOccurred,
        FeatureNames.SweepDirection,
        FeatureNames.SweepDepthAtr,
        FeatureNames.WickBodyRatioAtSweep,
        FeatureNames.CloseBackInsideAtr,
        FeatureNames.VolumeRatioAtSweep,
        FeatureNames.VelocityIntoLevel,
        FeatureNames.PeakVelocityAtLevel,
        FeatureNames.DecelerationAfterTouch,
        FeatureNames.DirectionFlipAfterTouch,
        FeatureNames.DirectionFlipDelayMs,
        FeatureNames.MicroRetestOccurred,
        FeatureNames.MicroRetestDepthAtr,
        FeatureNames.ResumeVelocity,
    ];
}
