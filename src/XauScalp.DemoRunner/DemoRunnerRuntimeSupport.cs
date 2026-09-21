using XauScalp.Domain;
using XauScalp.Features;

namespace XauScalp.DemoRunner;

public sealed class DemoRuntimeFeatureContext
{
    private readonly DemoCostConfiguration _cost;
    private double? _lastAtrM1;

    public DemoRuntimeFeatureContext(
        DemoCostConfiguration cost)
    {
        _cost = cost ?? throw new ArgumentNullException(nameof(cost));
    }

    public FeatureExternalContext Build(
        DateTimeOffset observedAtUtc)
    {
        return new FeatureExternalContext(
            observedAtUtc,
            atrM1: _lastAtrM1,
            estimatedLatencyMs: _cost.EstimatedLatencyMs,
            estimatedSlippagePoints: _cost.EstimatedSlippagePoints);
    }

    public void ObserveState(XauMarketState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        NumericFeatureValue? atr = state.Features.FirstOrDefault(
            static feature => string.Equals(
                feature.Name,
                FeatureNames.AtrM1,
                StringComparison.Ordinal));

        if (atr?.IsAvailable == true
            && atr.Value is double value
            && double.IsFinite(value)
            && value > 0)
        {
            _lastAtrM1 = value;
        }
    }
}

public sealed class DemoEvaluationTrigger
{
    private readonly DemoEvaluationConfiguration _configuration;
    private DateTimeOffset? _lastEvaluationAtUtc;
    private DateTimeOffset? _lastMinuteBucketUtc;
    private bool _lastDecelerationAbove;
    private bool _lastSweepOccurred;
    private bool _lastMicroRetestOccurred;
    private bool _pendingMeaningful;

    public DemoEvaluationTrigger(
        DemoEvaluationConfiguration configuration)
    {
        _configuration = configuration
            ?? throw new ArgumentNullException(nameof(configuration));
    }

    public bool ShouldEvaluate(XauMarketState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.Readiness.RequiredP0Ready)
        {
            ObserveEdges(state);
            return false;
        }

        DateTimeOffset minuteBucket = FloorMinute(
            state.TimestampUtc);
        bool newMinute = _lastMinuteBucketUtc is DateTimeOffset previousMinute
            && minuteBucket > previousMinute;

        bool decelerationAbove =
            TryFeature(
                state,
                FeatureNames.DecelerationRatio,
                out double deceleration)
            && deceleration >= _configuration.DecelerationThreshold;

        bool sweepOccurred =
            TryFeature(
                state,
                FeatureNames.SweepOccurred,
                out double sweep)
            && sweep >= 0.5;

        bool microRetestOccurred =
            TryFeature(
                state,
                FeatureNames.MicroRetestOccurred,
                out double microRetest)
            && microRetest >= 0.5;

        bool directionFlip =
            TryFeature(
                state,
                FeatureNames.DirectionFlipAgeMs,
                out double flipAgeMs)
            && flipAgeMs <= _configuration.DirectionFlipMaxAgeMs;

        bool meaningful =
            newMinute
            || directionFlip
            || (decelerationAbove && !_lastDecelerationAbove)
            || (sweepOccurred && !_lastSweepOccurred)
            || (microRetestOccurred && !_lastMicroRetestOccurred);

        _lastMinuteBucketUtc = minuteBucket;
        _lastDecelerationAbove = decelerationAbove;
        _lastSweepOccurred = sweepOccurred;
        _lastMicroRetestOccurred = microRetestOccurred;

        if (_lastEvaluationAtUtc is null)
        {
            _lastEvaluationAtUtc = state.TimestampUtc;
            _pendingMeaningful = false;
            return true;
        }

        TimeSpan elapsed =
            state.TimestampUtc - _lastEvaluationAtUtc.Value;

        if (elapsed < TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "Evaluation trigger state time regressed.");
        }

        if (meaningful)
        {
            _pendingMeaningful = true;
        }

        if (elapsed.TotalMilliseconds
            < _configuration.MinimumIntervalMs)
        {
            return false;
        }

        if (_pendingMeaningful
            || elapsed.TotalMilliseconds
                >= _configuration.MaximumIntervalMs)
        {
            _lastEvaluationAtUtc = state.TimestampUtc;
            _pendingMeaningful = false;
            return true;
        }

        return false;
    }

    private void ObserveEdges(XauMarketState state)
    {
        _lastMinuteBucketUtc = FloorMinute(state.TimestampUtc);

        _lastDecelerationAbove =
            TryFeature(
                state,
                FeatureNames.DecelerationRatio,
                out double deceleration)
            && deceleration >= _configuration.DecelerationThreshold;

        _lastSweepOccurred =
            TryFeature(
                state,
                FeatureNames.SweepOccurred,
                out double sweep)
            && sweep >= 0.5;

        _lastMicroRetestOccurred =
            TryFeature(
                state,
                FeatureNames.MicroRetestOccurred,
                out double microRetest)
            && microRetest >= 0.5;
    }

    private static bool TryFeature(
        XauMarketState state,
        string name,
        out double value)
    {
        NumericFeatureValue? feature = state.Features.FirstOrDefault(
            item => string.Equals(
                item.Name,
                name,
                StringComparison.Ordinal));

        if (feature?.IsAvailable == true
            && feature.Value is double numeric
            && double.IsFinite(numeric))
        {
            value = numeric;
            return true;
        }

        value = default;
        return false;
    }

    private static DateTimeOffset FloorMinute(
        DateTimeOffset timestampUtc)
    {
        DateTimeOffset utc = timestampUtc.ToUniversalTime();
        long ticks = utc.UtcDateTime.Ticks;
        long minute = TimeSpan.TicksPerMinute;
        return new DateTimeOffset(
            ticks - (ticks % minute),
            TimeSpan.Zero);
    }
}
