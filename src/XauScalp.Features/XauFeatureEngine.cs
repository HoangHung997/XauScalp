using System.Security.Cryptography;
using System.Text;
using XauScalp.Domain;
using XauScalp.MarketData;

namespace XauScalp.Features;

public sealed class XauFeatureEngine : IXauFeatureEngine
{
    public const string EngineVersion = "xau-feature-engine-xsp017-v1";

    private static readonly TimeSpan Window250Ms = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Window500Ms = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Window1S = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Window2S = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Window3S = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Window5S = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Window10S = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PeakWindow = TimeSpan.FromSeconds(5);

    private readonly XauFeatureEngineOptions _options;
    private readonly MarketClockNormalizer _clockNormalizer;
    private readonly CausalBarAggregator _barAggregator;
    private readonly RollingTickSeries _ticks = new();
    private readonly List<VelocitySample> _velocity500Ms = [];
    private readonly CausalLiquidityTracker _liquidity = new();
    private readonly CausalStructureRegimeTracker _structureRegime = new();

    private SymbolSpecificationEvent? _symbolSpecificationEvent;
    private FeatureExternalContext? _externalContext;
    private NewsContextEvent? _newsContextEvent;
    private TickEvent? _lastTick;
    private XauMarketState? _lastState;
    private MarketConnectionState? _connectionState;
    private bool _feedGapDetected;
    private int? _lastVelocityDirection;
    private DateTimeOffset? _lastDirectionFlipAtUtc;

    public XauFeatureEngine(XauFeatureEngineOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clockNormalizer = new MarketClockNormalizer(options.ClockConfiguration);
        _barAggregator = new CausalBarAggregator(_clockNormalizer, "feature-engine-bars-v1");
    }

    public string FeatureSchemaVersion => ContractVersions.FeatureSchemaV1;

    public string FeatureEngineVersion => EngineVersion;

    public XauMarketState Update(MarketEvent marketEvent)
    {
        ArgumentNullException.ThrowIfNull(marketEvent);

        if (marketEvent is TickEvent tick)
        {
            return UpdateTick(tick);
        }

        ObserveContext(marketEvent);

        if (_lastTick is null)
        {
            throw new InvalidOperationException(
                "A feature snapshot is unavailable until at least one market tick has been observed.");
        }

        _lastState = BuildState(
            asOfUtc: marketEvent.TimestampUtc,
            sequenceId: marketEvent.SequenceId,
            brokerTimestamp: marketEvent.BrokerTimestamp,
            dataSourceId: marketEvent.DataSourceId);

        return _lastState;
    }

    public void ObserveContext(MarketEvent marketEvent)
    {
        ArgumentNullException.ThrowIfNull(marketEvent);

        switch (marketEvent)
        {
            case SymbolSpecificationEvent specificationEvent:
                _symbolSpecificationEvent = specificationEvent;
                break;

            case ConnectionStatusEvent connectionEvent:
                _connectionState = connectionEvent.State;
                break;

            case NewsContextEvent newsContextEvent:
                _newsContextEvent = newsContextEvent;
                break;

            case FeedGapEvent:
                _feedGapDetected = true;
                break;

            case BarEvent:
            case TickEvent:
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(marketEvent),
                    marketEvent.GetType().Name,
                    "Unsupported market event.");
        }
    }

    public void SetExternalContext(FeatureExternalContext? context)
    {
        _externalContext = context;
    }

    private XauMarketState UpdateTick(TickEvent tick)
    {
        ValidateSymbolContinuity(tick);
        IReadOnlyList<BarEvent> barEvents = _barAggregator.Apply(tick);

        foreach (BarEvent barEvent in barEvents)
        {
            if (barEvent.UpdateKind == BarUpdateKind.Closed
                && barEvent.Bar is ClosedBarState closed)
            {
                _structureRegime.ObserveClosedBar(closed);

                if (closed.Timeframe == BarTimeframe.M1)
                {
                    _liquidity.ObserveClosedM1(closed);
                }
            }
        }

        _ticks.Add(tick);
        _lastTick = tick;

        UpdateVelocityState(tick.TimestampUtc);
        UpdateLiquidityState(tick);
        _structureRegime.UpdateTick(tick);

        _lastState = BuildState(
            tick.TimestampUtc,
            tick.SequenceId,
            tick.BrokerTimestamp,
            tick.DataSourceId);

        return _lastState;
    }

    private XauMarketState BuildState(
        DateTimeOffset asOfUtc,
        long sequenceId,
        DateTimeOffset? brokerTimestamp,
        string dataSourceId)
    {
        TickEvent lastTick = _lastTick
            ?? throw new InvalidOperationException("Cannot build feature state before the first tick.");

        if (asOfUtc < lastTick.TimestampUtc)
        {
            throw new InvalidDataException("Feature-state time cannot precede the latest observed tick.");
        }

        FormingBarSnapshot m1 = _barAggregator.GetFormingSnapshot(BarTimeframe.M1, asOfUtc)
            ?? throw new InvalidOperationException("M1 forming state is unavailable after a tick.");

        NormalizedMarketTime normalizedTime = _clockNormalizer.Normalize(asOfUtc, brokerTimestamp);
        FeatureExternalContext? external = ComposeExternalContext(asOfUtc);

        var features = new List<NumericFeatureValue>(176);
        AddExecutionFeatures(features, lastTick, asOfUtc, normalizedTime, external);
        AddLiveM1Features(features, m1, lastTick, asOfUtc, external);
        AddMicrostructureFeatures(features, asOfUtc);
        AddLiquidityFeatures(features, lastTick, asOfUtc, external);
        AddStructureRegimeFeatures(features, lastTick, m1.State, asOfUtc);

        string[] missingRequirements = DetermineMissingRequirements(asOfUtc, external);
        bool tickHistoryReady = _ticks.IsWarm(asOfUtc, TimeSpan.FromSeconds(15));
        bool barHistoryReady = external?.AtrM1 is not null;
        bool newsReady = external?.NewsDistanceBeforeSec is not null
            && external.NewsDistanceAfterSec is not null
            && external.IsHighImpactNewsWindow is not null;

        var readiness = new DataReadiness(
            requiredP0Ready: missingRequirements.Length == 0,
            tickHistoryReady,
            barHistoryReady,
            newsReady,
            missingRequirements);

        decimal mid = (lastTick.Bid + lastTick.Ask) / 2m;
        Guid marketStateId = CreateDeterministicStateId(
            lastTick.Symbol,
            lastTick.BrokerSymbol,
            dataSourceId,
            sequenceId,
            asOfUtc);

        return new XauMarketState(
            ContractVersions.MarketStateV1,
            marketStateId,
            asOfUtc,
            brokerTimestamp,
            sequenceId,
            lastTick.Symbol,
            lastTick.BrokerSymbol,
            lastTick.Bid,
            lastTick.Ask,
            mid,
            FeatureSchemaVersion,
            dataSourceId,
            LiquiditySource.Estimated,
            readiness,
            features.ToArray());
    }

    private void AddExecutionFeatures(
        List<NumericFeatureValue> features,
        TickEvent tick,
        DateTimeOffset asOfUtc,
        NormalizedMarketTime normalizedTime,
        FeatureExternalContext? external)
    {
        double spreadPrice = decimal.ToDouble(tick.Ask - tick.Bid);
        AddAvailable(features, FeatureNames.SpreadPrice, spreadPrice, "price", asOfUtc);

        SymbolSpecification? specification = _symbolSpecificationEvent?.Specification;
        if (specification is null)
        {
            AddUnavailable(features, FeatureNames.SpreadPoints, "points", "symbol specification unavailable");
            AddUnavailable(features, FeatureNames.TickSize, "price", "symbol specification unavailable");
            AddUnavailable(features, FeatureNames.TickValue, "moneyPerTick", "symbol specification unavailable");
            AddUnavailable(features, FeatureNames.MinStopDistance, "price", "symbol specification unavailable");
        }
        else
        {
            double point = decimal.ToDouble(specification.Point);
            AddAvailable(features, FeatureNames.SpreadPoints, spreadPrice / point, "points", asOfUtc);
            AddAvailable(features, FeatureNames.TickSize, decimal.ToDouble(specification.TickSize), "price", _symbolSpecificationEvent!.TimestampUtc);
            AddAvailable(features, FeatureNames.TickValue, decimal.ToDouble(specification.TickValue), "moneyPerTick", _symbolSpecificationEvent!.TimestampUtc);
            AddAvailable(features, FeatureNames.MinStopDistance, decimal.ToDouble(specification.MinStopDistance), "price", _symbolSpecificationEvent!.TimestampUtc);
        }

        if (external?.AtrM1 is double atrM1)
        {
            AddAvailable(features, FeatureNames.SpreadAtrRatio, spreadPrice / atrM1, "ratio", external.ObservedAtUtc);
        }
        else
        {
            AddUnavailable(features, FeatureNames.SpreadAtrRatio, "ratio", "causal M1 ATR unavailable");
        }

        AddExternalOrUnavailable(
            features,
            FeatureNames.EstimatedLatencyMs,
            external?.EstimatedLatencyMs,
            "ms",
            external,
            "latency estimate unavailable");

        AddExternalOrUnavailable(
            features,
            FeatureNames.EstimatedSlippagePoints,
            external?.EstimatedSlippagePoints,
            "points",
            external,
            "slippage estimate unavailable");

        int? sessionCode = _options.SessionSchedule?.ResolveCode(normalizedTime.BrokerLocalDateTime);
        if (sessionCode is int code)
        {
            AddAvailable(features, FeatureNames.SessionCode, code, "code", asOfUtc);
        }
        else
        {
            AddUnavailable(features, FeatureNames.SessionCode, "code", "session schedule or matching segment unavailable");
        }

        int minuteOfDay = normalizedTime.BrokerLocalDateTime.Hour * 60 + normalizedTime.BrokerLocalDateTime.Minute;
        AddAvailable(features, FeatureNames.MinuteOfDay, minuteOfDay, "minute", asOfUtc);
        AddAvailable(features, FeatureNames.DayOfWeek, (int)normalizedTime.BrokerLocalDateTime.DayOfWeek, "dayOfWeek", asOfUtc);

        AddExternalOrUnavailable(
            features,
            FeatureNames.NewsDistanceBeforeSec,
            external?.NewsDistanceBeforeSec,
            "seconds",
            external,
            "news context unavailable");

        AddExternalOrUnavailable(
            features,
            FeatureNames.NewsDistanceAfterSec,
            external?.NewsDistanceAfterSec,
            "seconds",
            external,
            "news context unavailable");

        if (external?.IsHighImpactNewsWindow is bool highImpact)
        {
            AddAvailable(
                features,
                FeatureNames.IsHighImpactNewsWindow,
                highImpact ? 1 : 0,
                "boolean",
                external.ObservedAtUtc);
        }
        else
        {
            AddUnavailable(
                features,
                FeatureNames.IsHighImpactNewsWindow,
                "boolean",
                "news context unavailable");
        }
    }

    private static void AddLiveM1Features(
        List<NumericFeatureValue> features,
        FormingBarSnapshot m1,
        TickEvent tick,
        DateTimeOffset asOfUtc,
        FeatureExternalContext? external)
    {
        FormingBarState bar = m1.State;
        double open = decimal.ToDouble(bar.Open);
        double high = decimal.ToDouble(bar.High);
        double low = decimal.ToDouble(bar.Low);
        double live = decimal.ToDouble(tick.Bid);
        double range = high - low;
        double body = live - open;
        double upperWick = high - Math.Max(open, live);
        double lowerWick = Math.Min(open, live) - low;

        AddAvailable(features, FeatureNames.M1BarAgeMs, m1.Age.TotalMilliseconds, "ms", asOfUtc);
        AddAvailable(features, FeatureNames.M1BarProgressPct, m1.Progress01 * 100, "pct", asOfUtc);
        AddAvailable(features, FeatureNames.M1Open, open, "price", asOfUtc);
        AddAvailable(features, FeatureNames.M1LiveHigh, high, "price", asOfUtc);
        AddAvailable(features, FeatureNames.M1LiveLow, low, "price", asOfUtc);
        AddAvailable(features, FeatureNames.M1LivePrice, live, "price", asOfUtc);
        AddAvailable(features, FeatureNames.M1LiveRangePrice, range, "price", asOfUtc);
        AddAvailable(features, FeatureNames.M1LiveBodyPrice, body, "price", asOfUtc);
        AddAvailable(features, FeatureNames.M1LiveBodyRatio, range > 0 ? Math.Abs(body) / range : 0, "ratio", asOfUtc);
        AddAvailable(features, FeatureNames.M1UpperWickRatio, range > 0 ? upperWick / range : 0, "ratio", asOfUtc);
        AddAvailable(features, FeatureNames.M1LowerWickRatio, range > 0 ? lowerWick / range : 0, "ratio", asOfUtc);
        AddAvailable(features, FeatureNames.M1DistanceFromOpen, live - open, "price", asOfUtc);
        AddAvailable(features, FeatureNames.M1DistanceFromHigh, high - live, "price", asOfUtc);
        AddAvailable(features, FeatureNames.M1DistanceFromLow, live - low, "price", asOfUtc);

        if (external?.AtrM1 is double atrM1)
        {
            AddAvailable(features, FeatureNames.M1RangeAtrRatio, range / atrM1, "ratio", external.ObservedAtUtc);
        }
        else
        {
            AddUnavailable(features, FeatureNames.M1RangeAtrRatio, "ratio", "causal M1 ATR unavailable");
        }
    }

    private void AddMicrostructureFeatures(List<NumericFeatureValue> features, DateTimeOffset asOfUtc)
    {
        AddReturn(features, FeatureNames.Return250ms, asOfUtc, Window250Ms);
        AddReturn(features, FeatureNames.Return500ms, asOfUtc, Window500Ms);
        AddReturn(features, FeatureNames.Return1s, asOfUtc, Window1S);
        AddReturn(features, FeatureNames.Return2s, asOfUtc, Window2S);
        AddReturn(features, FeatureNames.Return3s, asOfUtc, Window3S);
        AddReturn(features, FeatureNames.Return5s, asOfUtc, Window5S);
        AddReturn(features, FeatureNames.Return10s, asOfUtc, Window10S);

        AddVelocity(features, FeatureNames.Velocity500ms, asOfUtc, Window500Ms);
        AddVelocity(features, FeatureNames.Velocity1s, asOfUtc, Window1S);
        AddVelocity(features, FeatureNames.Velocity3s, asOfUtc, Window3S);
        AddVelocity(features, FeatureNames.Velocity5s, asOfUtc, Window5S);

        AddAcceleration(features, FeatureNames.Acceleration1s, asOfUtc, Window1S);
        AddAcceleration(features, FeatureNames.Acceleration3s, asOfUtc, Window3S);

        AddPeakAndDeceleration(features, asOfUtc);
        AddDirectionFlip(features, asOfUtc);

        AddTickCountAndRate(features, FeatureNames.TickCount250ms, FeatureNames.TickRate250ms, asOfUtc, Window250Ms);
        AddTickCountAndRate(features, FeatureNames.TickCount1s, FeatureNames.TickRate1s, asOfUtc, Window1S);
        AddTickCountAndRate(features, FeatureNames.TickCount5s, FeatureNames.TickRate5s, asOfUtc, Window5S);

        if (_ticks.TryIntervalStatistics(asOfUtc, Window5S, out double meanMs, out double stdMs))
        {
            AddAvailable(features, FeatureNames.MeanTickIntervalMs, meanMs, "ms", asOfUtc);
            AddAvailable(features, FeatureNames.TickIntervalStdMs, stdMs, "ms", asOfUtc);
        }
        else
        {
            AddUnavailable(features, FeatureNames.MeanTickIntervalMs, "ms", "5s tick interval window not ready");
            AddUnavailable(features, FeatureNames.TickIntervalStdMs, "ms", "5s tick interval window not ready");
        }

        if (_ticks.TryDirectionRatios(asOfUtc, Window5S, out double upRatio, out double downRatio))
        {
            AddAvailable(features, FeatureNames.UpTickRatio, upRatio, "ratio", asOfUtc);
            AddAvailable(features, FeatureNames.DownTickRatio, downRatio, "ratio", asOfUtc);
        }
        else
        {
            AddUnavailable(features, FeatureNames.UpTickRatio, "ratio", "5s directional tick window not ready");
            AddUnavailable(features, FeatureNames.DownTickRatio, "ratio", "5s directional tick window not ready");
        }

        AddMicroRange(features, FeatureNames.MicroRange1s, asOfUtc, Window1S);
        AddMicroRange(features, FeatureNames.MicroRange5s, asOfUtc, Window5S);

        if (_ticks.TryBurstZScore(asOfUtc, out double zScore))
        {
            AddAvailable(features, FeatureNames.BurstZScore, zScore, "zscore", asOfUtc);
        }
        else
        {
            AddUnavailable(
                features,
                FeatureNames.BurstZScore,
                "zscore",
                "15 completed seconds or non-zero baseline variance unavailable");
        }
    }

    private void AddReturn(
        List<NumericFeatureValue> features,
        string name,
        DateTimeOffset asOfUtc,
        TimeSpan window)
    {
        if (_ticks.TryReturn(asOfUtc, window, out double value))
        {
            AddAvailable(features, name, value, "price", asOfUtc);
        }
        else
        {
            AddUnavailable(features, name, "price", $"{window.TotalMilliseconds:0}ms tick window not ready");
        }
    }

    private void AddVelocity(
        List<NumericFeatureValue> features,
        string name,
        DateTimeOffset asOfUtc,
        TimeSpan window)
    {
        if (_ticks.TryVelocity(asOfUtc, window, out double value))
        {
            AddAvailable(features, name, value, "pricePerSecond", asOfUtc);
        }
        else
        {
            AddUnavailable(features, name, "pricePerSecond", $"{window.TotalMilliseconds:0}ms velocity window not ready");
        }
    }

    private void AddAcceleration(
        List<NumericFeatureValue> features,
        string name,
        DateTimeOffset asOfUtc,
        TimeSpan window)
    {
        if (_ticks.TryAcceleration(asOfUtc, window, out double value))
        {
            AddAvailable(features, name, value, "pricePerSecond2", asOfUtc);
        }
        else
        {
            AddUnavailable(features, name, "pricePerSecond2", $"{window.TotalMilliseconds:0}ms acceleration window not ready");
        }
    }

    private void AddTickCountAndRate(
        List<NumericFeatureValue> features,
        string countName,
        string rateName,
        DateTimeOffset asOfUtc,
        TimeSpan window)
    {
        if (_ticks.TryTickCount(asOfUtc, window, out int count))
        {
            AddAvailable(features, countName, count, "ticks", asOfUtc);
            AddAvailable(features, rateName, count / window.TotalSeconds, "ticksPerSecond", asOfUtc);
        }
        else
        {
            string reason = $"{window.TotalMilliseconds:0}ms tick-count window not ready";
            AddUnavailable(features, countName, "ticks", reason);
            AddUnavailable(features, rateName, "ticksPerSecond", reason);
        }
    }

    private void AddMicroRange(
        List<NumericFeatureValue> features,
        string name,
        DateTimeOffset asOfUtc,
        TimeSpan window)
    {
        if (_ticks.TryMicroRange(asOfUtc, window, out double range))
        {
            AddAvailable(features, name, range, "price", asOfUtc);
        }
        else
        {
            AddUnavailable(features, name, "price", $"{window.TotalMilliseconds:0}ms micro-range window not ready");
        }
    }

    private void AddPeakAndDeceleration(List<NumericFeatureValue> features, DateTimeOffset asOfUtc)
    {
        bool warm = _ticks.IsWarm(asOfUtc, PeakWindow);
        VelocitySample[] samples = _velocity500Ms
            .Where(sample => sample.TimestampUtc > asOfUtc - PeakWindow && sample.TimestampUtc <= asOfUtc)
            .ToArray();

        if (!warm || samples.Length == 0)
        {
            AddUnavailable(features, FeatureNames.PeakVelocityUp, "pricePerSecond", "5s peak-velocity window not ready");
            AddUnavailable(features, FeatureNames.PeakVelocityDown, "pricePerSecond", "5s peak-velocity window not ready");
            AddUnavailable(features, FeatureNames.DecelerationRatio, "ratio", "5s peak-velocity window not ready");
            return;
        }

        double peakUp = samples.Max(static sample => Math.Max(sample.Velocity, 0));
        double peakDown = samples.Max(static sample => Math.Max(-sample.Velocity, 0));
        AddAvailable(features, FeatureNames.PeakVelocityUp, peakUp, "pricePerSecond", asOfUtc);
        AddAvailable(features, FeatureNames.PeakVelocityDown, peakDown, "pricePerSecond", asOfUtc);

        double dominantPeak = Math.Max(peakUp, peakDown);
        if (dominantPeak <= 0)
        {
            AddAvailable(features, FeatureNames.DecelerationRatio, 0, "ratio", asOfUtc);
            return;
        }

        double currentVelocity = samples[^1].Velocity;
        double alignedCurrent = peakUp >= peakDown
            ? Math.Max(currentVelocity, 0)
            : Math.Max(-currentVelocity, 0);

        double ratio = Math.Clamp((dominantPeak - alignedCurrent) / dominantPeak, 0, 1);
        AddAvailable(features, FeatureNames.DecelerationRatio, ratio, "ratio", asOfUtc);
    }

    private void AddDirectionFlip(List<NumericFeatureValue> features, DateTimeOffset asOfUtc)
    {
        if (_lastDirectionFlipAtUtc is DateTimeOffset flipAt)
        {
            AddAvailable(
                features,
                FeatureNames.DirectionFlipAgeMs,
                Math.Max(0, (asOfUtc - flipAt).TotalMilliseconds),
                "ms",
                asOfUtc);
        }
        else
        {
            AddUnavailable(features, FeatureNames.DirectionFlipAgeMs, "ms", "no 500ms velocity direction flip observed");
        }
    }

    private void UpdateLiquidityState(TickEvent tick)
    {
        FeatureExternalContext? external = GetFreshExternalContext(tick.TimestampUtc);

        double? velocity500Ms = _ticks.TryVelocity(
            tick.TimestampUtc,
            Window500Ms,
            out double currentVelocity)
            ? currentVelocity
            : null;

        double? peakUp = null;
        double? peakDown = null;
        double? deceleration = null;

        bool peakWarm = _ticks.IsWarm(tick.TimestampUtc, PeakWindow);
        VelocitySample[] samples = _velocity500Ms
            .Where(sample => sample.TimestampUtc > tick.TimestampUtc - PeakWindow
                && sample.TimestampUtc <= tick.TimestampUtc)
            .ToArray();

        if (peakWarm && samples.Length > 0)
        {
            peakUp = samples.Max(static sample => Math.Max(sample.Velocity, 0));
            peakDown = samples.Max(static sample => Math.Max(-sample.Velocity, 0));

            double dominantPeak = Math.Max(peakUp.Value, peakDown.Value);
            if (dominantPeak <= 0)
            {
                deceleration = 0;
            }
            else
            {
                double alignedCurrent = peakUp >= peakDown
                    ? Math.Max(samples[^1].Velocity, 0)
                    : Math.Max(-samples[^1].Velocity, 0);
                deceleration = Math.Clamp(
                    (dominantPeak - alignedCurrent) / dominantPeak,
                    0,
                    1);
            }
        }

        double? flipAgeMs = _lastDirectionFlipAtUtc is DateTimeOffset flipAt
            ? Math.Max(0, (tick.TimestampUtc - flipAt).TotalMilliseconds)
            : null;

        FormingBarSnapshot? m1 = _barAggregator.GetFormingSnapshot(
            BarTimeframe.M1,
            tick.TimestampUtc);

        double wickBodyRatio = 0;
        if (m1 is not null)
        {
            double open = decimal.ToDouble(m1.State.Open);
            double high = decimal.ToDouble(m1.State.High);
            double low = decimal.ToDouble(m1.State.Low);
            double live = decimal.ToDouble(tick.Bid);
            double totalWick =
                Math.Max(0, high - Math.Max(open, live))
                + Math.Max(0, Math.Min(open, live) - low);
            double body = Math.Abs(live - open);
            wickBodyRatio = body > 1e-12
                ? totalWick / body
                : totalWick > 0 ? 10 : 0;
            wickBodyRatio = Math.Clamp(wickBodyRatio, 0, 10);
        }

        _liquidity.UpdateTick(
            tick,
            new LiquidityTickInputs(
                external?.AtrM1,
                velocity500Ms,
                peakUp,
                peakDown,
                deceleration,
                flipAgeMs,
                wickBodyRatio));
    }

    private void AddLiquidityFeatures(
        List<NumericFeatureValue> features,
        TickEvent tick,
        DateTimeOffset asOfUtc,
        FeatureExternalContext? external)
    {
        decimal mid = (tick.Bid + tick.Ask) / 2m;
        IReadOnlyList<LiquidityFeatureMetric> metrics = _liquidity.Snapshot(
            asOfUtc,
            mid,
            external?.AtrM1);

        foreach (LiquidityFeatureMetric metric in metrics)
        {
            if (metric.IsAvailable)
            {
                AddAvailable(
                    features,
                    metric.Name,
                    metric.Value!.Value,
                    metric.Unit,
                    metric.ObservedAtUtc ?? asOfUtc);
            }
            else
            {
                AddUnavailable(
                    features,
                    metric.Name,
                    metric.Unit,
                    metric.UnavailableReason ?? "liquidity metric unavailable");
            }
        }
    }

    private void AddStructureRegimeFeatures(
        List<NumericFeatureValue> features,
        TickEvent tick,
        FormingBarState formingM1,
        DateTimeOffset asOfUtc)
    {
        decimal mid = (tick.Bid + tick.Ask) / 2m;
        IReadOnlyList<StructureFeatureMetric> metrics = _structureRegime.Snapshot(
            asOfUtc,
            mid,
            formingM1);

        foreach (StructureFeatureMetric metric in metrics)
        {
            if (metric.IsAvailable)
            {
                AddAvailable(
                    features,
                    metric.Name,
                    metric.Value!.Value,
                    metric.Unit,
                    metric.ObservedAtUtc ?? asOfUtc);
            }
            else
            {
                AddUnavailable(
                    features,
                    metric.Name,
                    metric.Unit,
                    metric.UnavailableReason ?? "structure/regime metric unavailable");
            }
        }
    }

    private void UpdateVelocityState(DateTimeOffset asOfUtc)
    {
        if (!_ticks.TryVelocity(asOfUtc, Window500Ms, out double velocity))
        {
            return;
        }

        _velocity500Ms.Add(new VelocitySample(asOfUtc, velocity));
        DateTimeOffset cutoff = asOfUtc - PeakWindow;
        _velocity500Ms.RemoveAll(sample => sample.TimestampUtc < cutoff);

        int direction = Math.Sign(velocity);
        if (direction == 0)
        {
            return;
        }

        if (_lastVelocityDirection is int previous && previous != direction)
        {
            _lastDirectionFlipAtUtc = asOfUtc;
        }

        _lastVelocityDirection = direction;
    }

    private FeatureExternalContext? ComposeExternalContext(
        DateTimeOffset asOfUtc)
    {
        FeatureExternalContext? external = GetFreshExternalContext(asOfUtc);

        if (_newsContextEvent is null)
        {
            return external;
        }

        NewsContextEvent news = _newsContextEvent;
        bool fresh = news.TimestampUtc <= asOfUtc
            && asOfUtc - news.TimestampUtc <= _options.ExternalContextMaxAge;

        if (!fresh || !news.IsAvailable)
        {
            if (external is null)
            {
                return null;
            }

            return new FeatureExternalContext(
                external.ObservedAtUtc,
                external.AtrM1,
                external.EstimatedLatencyMs,
                external.EstimatedSlippagePoints);
        }

        double before = news.NewsDistanceBeforeSec
            ?? throw new InvalidDataException(
                "Available news context is missing upcoming-event distance.");
        double after = news.NewsDistanceAfterSec
            ?? throw new InvalidDataException(
                "Available news context is missing past-event distance.");

        bool highImpact =
            before <= _options.HighImpactNewsFeatureWindowBeforeSec
            || after <= _options.HighImpactNewsFeatureWindowAfterSec;

        DateTimeOffset observedAt = external?.ObservedAtUtc
            ?? news.TimestampUtc;

        return new FeatureExternalContext(
            observedAt,
            external?.AtrM1,
            external?.EstimatedLatencyMs,
            external?.EstimatedSlippagePoints,
            before,
            after,
            highImpact);
    }

    private FeatureExternalContext? GetFreshExternalContext(DateTimeOffset asOfUtc)
    {
        if (_externalContext is null
            || _externalContext.ObservedAtUtc > asOfUtc
            || asOfUtc - _externalContext.ObservedAtUtc > _options.ExternalContextMaxAge)
        {
            return null;
        }

        return _externalContext;
    }

    private string[] DetermineMissingRequirements(
        DateTimeOffset asOfUtc,
        FeatureExternalContext? external)
    {
        var missing = new List<string>();

        if (_symbolSpecificationEvent is null)
        {
            missing.Add("symbol specification");
        }

        if (!_ticks.IsWarm(asOfUtc, TimeSpan.FromSeconds(15)))
        {
            missing.Add("15s tick-history warmup");
        }

        if (external?.AtrM1 is null)
        {
            missing.Add("causal M1 ATR");
        }

        if (external?.EstimatedLatencyMs is null)
        {
            missing.Add("estimated latency");
        }

        if (external?.EstimatedSlippagePoints is null)
        {
            missing.Add("estimated slippage");
        }

        if (external?.NewsDistanceBeforeSec is null
            || external.NewsDistanceAfterSec is null
            || external.IsHighImpactNewsWindow is null)
        {
            missing.Add("news context");
        }

        if (_options.SessionSchedule is null)
        {
            missing.Add("session schedule");
        }

        if (_connectionState != MarketConnectionState.Connected)
        {
            missing.Add("market data connection");
        }

        if (_lastTick?.BrokerTimestamp is not DateTimeOffset brokerTimestamp)
        {
            missing.Add("broker tick timestamp");
        }
        else
        {
            DateTimeOffset brokerUtc = brokerTimestamp.ToUniversalTime();
            if (brokerUtc > asOfUtc
                || asOfUtc - brokerUtc > _options.MaxBrokerTickAge)
            {
                missing.Add("stale broker tick");
            }
        }

        if (_feedGapDetected)
        {
            missing.Add("unresolved feed gap");
        }

        return missing.ToArray();
    }

    private void ValidateSymbolContinuity(TickEvent tick)
    {
        if (_lastTick is not null
            && (!string.Equals(_lastTick.Symbol, tick.Symbol, StringComparison.Ordinal)
                || !string.Equals(_lastTick.BrokerSymbol, tick.BrokerSymbol, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("A single feature-engine instance cannot mix symbols.");
        }

        if (_symbolSpecificationEvent is not null
            && !string.Equals(
                _symbolSpecificationEvent.BrokerSymbol,
                tick.BrokerSymbol,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Tick broker symbol does not match the observed symbol specification.");
        }
    }

    private static Guid CreateDeterministicStateId(
        string symbol,
        string brokerSymbol,
        string dataSourceId,
        long sequenceId,
        DateTimeOffset timestampUtc)
    {
        string key = string.Join(
            "|",
            EngineVersion,
            ContractVersions.FeatureSchemaV1,
            symbol,
            brokerSymbol,
            dataSourceId,
            sequenceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            timestampUtc.UtcDateTime.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static void AddExternalOrUnavailable(
        List<NumericFeatureValue> features,
        string name,
        double? value,
        string unit,
        FeatureExternalContext? external,
        string unavailableReason)
    {
        if (value is double numeric && external is not null)
        {
            AddAvailable(features, name, numeric, unit, external.ObservedAtUtc);
        }
        else
        {
            AddUnavailable(features, name, unit, unavailableReason);
        }
    }

    private static void AddAvailable(
        List<NumericFeatureValue> features,
        string name,
        double value,
        string unit,
        DateTimeOffset observedAtUtc)
    {
        features.Add(new NumericFeatureValue(name, value, unit, true, observedAtUtc, null));
    }

    private static void AddUnavailable(
        List<NumericFeatureValue> features,
        string name,
        string unit,
        string reason)
    {
        features.Add(new NumericFeatureValue(name, null, unit, false, null, reason));
    }

    private sealed record VelocitySample(DateTimeOffset TimestampUtc, double Velocity);
}
