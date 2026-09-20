using XauScalp.Domain;
using XauScalp.Features;

namespace XauScalp.Replay;

public enum EntryResearchMode
{
    M1Close = 0,
    IntrabarImmediate = 1,
    IntrabarDeceleration = 2,
    IntrabarDirectionFlip = 3,
    IntrabarMicroRetest = 4,
}

public enum EntryTradeOutcomeKind
{
    NoEntry = 0,
    TargetFirst = 1,
    AdverseFirst = 2,
    Censored = 3,
}

public sealed record EntryExperimentCandidate(
    Guid CandidateId,
    Guid MarketStateId,
    long SourceSequenceId,
    DateTimeOffset SetupAtUtc,
    TradeSide IntendedSide);

public sealed class EntryExperimentSettings
{
    public EntryExperimentSettings(
        IEnumerable<decimal> targetDistancesPrice,
        IEnumerable<decimal> adverseBarriersPrice,
        IEnumerable<TimeSpan> holdingHorizons,
        TimeSpan triggerSearchHorizon,
        double decelerationRatioThreshold,
        TimeSpan directionFlipMaxAge,
        decimal microRetestAdvancePrice,
        decimal microRetestDepthPrice,
        decimal microRetestResumePrice)
    {
        TargetDistancesPrice = ValidateDistances(
            targetDistancesPrice,
            nameof(targetDistancesPrice));
        AdverseBarriersPrice = ValidateDistances(
            adverseBarriersPrice,
            nameof(adverseBarriersPrice));

        ArgumentNullException.ThrowIfNull(holdingHorizons);
        TimeSpan[] horizons = holdingHorizons
            .Distinct()
            .Order()
            .ToArray();

        if (horizons.Length == 0 || horizons.Any(static value => value <= TimeSpan.Zero))
        {
            throw new ArgumentException(
                "At least one positive holding horizon is required.",
                nameof(holdingHorizons));
        }

        if (triggerSearchHorizon <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(triggerSearchHorizon),
                triggerSearchHorizon,
                "Trigger search horizon must be positive.");
        }

        if (!double.IsFinite(decelerationRatioThreshold)
            || decelerationRatioThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(decelerationRatioThreshold),
                decelerationRatioThreshold,
                "Deceleration threshold must be in [0,1].");
        }

        if (directionFlipMaxAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(directionFlipMaxAge),
                directionFlipMaxAge,
                "Direction-flip maximum age must be non-negative.");
        }

        MicroRetestAdvancePrice = Positive(
            microRetestAdvancePrice,
            nameof(microRetestAdvancePrice));
        MicroRetestDepthPrice = Positive(
            microRetestDepthPrice,
            nameof(microRetestDepthPrice));
        MicroRetestResumePrice = Positive(
            microRetestResumePrice,
            nameof(microRetestResumePrice));

        HoldingHorizons = horizons;
        TriggerSearchHorizon = triggerSearchHorizon;
        DecelerationRatioThreshold = decelerationRatioThreshold;
        DirectionFlipMaxAge = directionFlipMaxAge;
    }

    public IReadOnlyList<decimal> TargetDistancesPrice { get; }

    public IReadOnlyList<decimal> AdverseBarriersPrice { get; }

    public IReadOnlyList<TimeSpan> HoldingHorizons { get; }

    public TimeSpan TriggerSearchHorizon { get; }

    public double DecelerationRatioThreshold { get; }

    public TimeSpan DirectionFlipMaxAge { get; }

    public decimal MicroRetestAdvancePrice { get; }

    public decimal MicroRetestDepthPrice { get; }

    public decimal MicroRetestResumePrice { get; }

    public static EntryExperimentSettings XauScalpV1 { get; } = new(
        targetDistancesPrice: [5m, 10m],
        adverseBarriersPrice: [2m, 3m, 4m, 5m],
        holdingHorizons:
        [
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(3),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(15),
        ],
        triggerSearchHorizon: TimeSpan.FromMinutes(2),
        decelerationRatioThreshold: 0.70,
        directionFlipMaxAge: TimeSpan.FromSeconds(1),
        microRetestAdvancePrice: 0.50m,
        microRetestDepthPrice: 0.20m,
        microRetestResumePrice: 0.15m);

    private static decimal[] ValidateDistances(
        IEnumerable<decimal> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);

        decimal[] result = values
            .Distinct()
            .Order()
            .ToArray();

        if (result.Length == 0 || result.Any(static value => value <= 0))
        {
            throw new ArgumentException(
                "At least one positive price distance is required.",
                parameterName);
        }

        return result;
    }

    private static decimal Positive(decimal value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Value must be positive.");
        }

        return value;
    }
}

public sealed record EntryTrigger(
    EntryResearchMode Mode,
    long ReplayOrdinal,
    Guid MarketStateId,
    DateTimeOffset TriggerAtUtc,
    long SequenceId,
    decimal Bid,
    decimal Ask);

public sealed record EntryEvaluationKey(
    EntryResearchMode Mode,
    decimal TargetDistancePrice,
    decimal AdverseBarrierPrice,
    TimeSpan HoldingHorizon);

public sealed record EntryTradeOutcome(
    Guid CandidateId,
    TradeSide Side,
    EntryEvaluationKey Key,
    EntryTradeOutcomeKind Outcome,
    string CostScenarioName,
    EntryTrigger? Trigger,
    decimal? EntryExecutionPrice,
    decimal? NetResultPrice,
    decimal? MfePrice,
    decimal? MaePrice,
    TimeSpan? TimeToTarget,
    TimeSpan? TimeToAdverse,
    DateTimeOffset? ExitAtUtc,
    long? ExitSequenceId);

public sealed record EntryModeSummary(
    EntryEvaluationKey Key,
    int CandidateCount,
    int EntryCount,
    int NoEntryCount,
    int TargetFirstCount,
    int AdverseFirstCount,
    int CensoredCount,
    double? PTargetFirst,
    double? FalseEntryRate,
    decimal? ExpectedValueAfterCostsPrice,
    decimal? AverageMfePrice,
    decimal? AverageMaePrice,
    TimeSpan? MedianTimeToTarget,
    decimal? ProfitFactor,
    decimal? AverageWinPrice,
    decimal? AverageLossPrice,
    decimal? MaxDrawdownPrice);

public sealed record EntryExperimentResult(
    IReadOnlyList<EntryTradeOutcome> Outcomes,
    IReadOnlyList<EntryModeSummary> Summaries);

public sealed class EntryModeExperiment
{
    private static readonly EntryResearchMode[] Modes =
    [
        EntryResearchMode.M1Close,
        EntryResearchMode.IntrabarImmediate,
        EntryResearchMode.IntrabarDeceleration,
        EntryResearchMode.IntrabarDirectionFlip,
        EntryResearchMode.IntrabarMicroRetest,
    ];

    public EntryExperimentResult Run(
        IReadOnlyList<ReplayOutputRecord> records,
        IReadOnlyList<EntryExperimentCandidate> candidates,
        EntryExperimentSettings settings,
        ReplayCostScenario costScenario)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(costScenario);

        ValidateOrdered(records);

        var outcomes = new List<EntryTradeOutcome>();

        foreach (EntryExperimentCandidate candidate in candidates)
        {
            int setupIndex = FindSetupIndex(records, candidate);

            foreach (EntryResearchMode mode in Modes)
            {
                EntryTrigger? trigger = SelectTrigger(
                    records,
                    setupIndex,
                    candidate,
                    settings,
                    mode);

                foreach (TimeSpan horizon in settings.HoldingHorizons)
                {
                    foreach (decimal target in settings.TargetDistancesPrice)
                    {
                        foreach (decimal barrier in settings.AdverseBarriersPrice)
                        {
                            var key = new EntryEvaluationKey(
                                mode,
                                target,
                                barrier,
                                horizon);

                            outcomes.Add(
                                trigger is null
                                    ? NoEntry(candidate, key, costScenario)
                                    : EvaluateEntry(
                                        records,
                                        trigger,
                                        candidate,
                                        key,
                                        costScenario));
                        }
                    }
                }
            }
        }

        EntryModeSummary[] summaries = outcomes
            .GroupBy(static outcome => outcome.Key)
            .Select(MakeSummary)
            .OrderBy(static summary => summary.Key.Mode)
            .ThenBy(static summary => summary.Key.HoldingHorizon)
            .ThenBy(static summary => summary.Key.TargetDistancePrice)
            .ThenBy(static summary => summary.Key.AdverseBarrierPrice)
            .ToArray();

        return new EntryExperimentResult(outcomes, summaries);
    }

    private static EntryTradeOutcome EvaluateEntry(
        IReadOnlyList<ReplayOutputRecord> records,
        EntryTrigger trigger,
        EntryExperimentCandidate candidate,
        EntryEvaluationKey key,
        ReplayCostScenario costScenario)
    {
        int entryIndex = checked((int)trigger.ReplayOrdinal);
        SymbolSpecification specification = FindSpecification(records, entryIndex);

        decimal slippagePrice =
            specification.Point * (decimal)(costScenario.EstimatedSlippagePoints ?? 0);
        decimal commissionPrice = costScenario.CommissionPerLot == 0
            ? 0
            : costScenario.CommissionPerLot / specification.TickValue * specification.TickSize;

        decimal entryExecutionPrice = candidate.IntendedSide == TradeSide.Long
            ? trigger.Ask + slippagePrice
            : trigger.Bid - slippagePrice;

        DateTimeOffset horizonEnd = trigger.TriggerAtUtc + key.HoldingHorizon;
        decimal mfe = 0;
        decimal mae = 0;
        decimal finalNet = NetAtTick(
            candidate.IntendedSide,
            trigger.Bid,
            trigger.Ask,
            entryExecutionPrice,
            slippagePrice,
            commissionPrice);

        EntryTradeOutcomeKind outcome = EntryTradeOutcomeKind.Censored;
        TimeSpan? timeToTarget = null;
        TimeSpan? timeToAdverse = null;
        DateTimeOffset? exitAt = null;
        long? exitSequence = null;

        ApplyExcursion(finalNet, ref mfe, ref mae);

        if (finalNet >= key.TargetDistancePrice)
        {
            outcome = EntryTradeOutcomeKind.TargetFirst;
            timeToTarget = TimeSpan.Zero;
            exitAt = trigger.TriggerAtUtc;
            exitSequence = trigger.SequenceId;
        }
        else if (finalNet <= -key.AdverseBarrierPrice)
        {
            outcome = EntryTradeOutcomeKind.AdverseFirst;
            timeToAdverse = TimeSpan.Zero;
            exitAt = trigger.TriggerAtUtc;
            exitSequence = trigger.SequenceId;
        }
        else
        {
            for (int index = entryIndex + 1; index < records.Count; index++)
            {
                if (records[index].MarketEvent is not TickEvent tick)
                {
                    continue;
                }

                if (tick.TimestampUtc > horizonEnd)
                {
                    break;
                }

                finalNet = NetAtTick(
                    candidate.IntendedSide,
                    tick.Bid,
                    tick.Ask,
                    entryExecutionPrice,
                    slippagePrice,
                    commissionPrice);

                ApplyExcursion(finalNet, ref mfe, ref mae);

                TimeSpan elapsed = tick.TimestampUtc - trigger.TriggerAtUtc;
                if (finalNet >= key.TargetDistancePrice)
                {
                    outcome = EntryTradeOutcomeKind.TargetFirst;
                    timeToTarget = elapsed;
                    exitAt = tick.TimestampUtc;
                    exitSequence = tick.SequenceId;
                    break;
                }

                if (finalNet <= -key.AdverseBarrierPrice)
                {
                    outcome = EntryTradeOutcomeKind.AdverseFirst;
                    timeToAdverse = elapsed;
                    exitAt = tick.TimestampUtc;
                    exitSequence = tick.SequenceId;
                    break;
                }
            }
        }

        return new EntryTradeOutcome(
            candidate.CandidateId,
            candidate.IntendedSide,
            key,
            outcome,
            costScenario.Name,
            trigger,
            entryExecutionPrice,
            finalNet,
            mfe,
            mae,
            timeToTarget,
            timeToAdverse,
            exitAt,
            exitSequence);
    }

    private static EntryTradeOutcome NoEntry(
        EntryExperimentCandidate candidate,
        EntryEvaluationKey key,
        ReplayCostScenario costScenario)
    {
        return new EntryTradeOutcome(
            candidate.CandidateId,
            candidate.IntendedSide,
            key,
            EntryTradeOutcomeKind.NoEntry,
            costScenario.Name,
            Trigger: null,
            EntryExecutionPrice: null,
            NetResultPrice: null,
            MfePrice: null,
            MaePrice: null,
            TimeToTarget: null,
            TimeToAdverse: null,
            ExitAtUtc: null,
            ExitSequenceId: null);
    }

    private static EntryTrigger? SelectTrigger(
        IReadOnlyList<ReplayOutputRecord> records,
        int setupIndex,
        EntryExperimentCandidate candidate,
        EntryExperimentSettings settings,
        EntryResearchMode mode)
    {
        DateTimeOffset searchEnd = candidate.SetupAtUtc + settings.TriggerSearchHorizon;

        return mode switch
        {
            EntryResearchMode.IntrabarImmediate =>
                TriggerFromRecord(records[setupIndex], mode),

            EntryResearchMode.M1Close =>
                FindM1CloseTrigger(records, setupIndex, candidate, searchEnd),

            EntryResearchMode.IntrabarDeceleration =>
                FindDecelerationTrigger(records, setupIndex, candidate, settings, searchEnd),

            EntryResearchMode.IntrabarDirectionFlip =>
                FindDirectionFlipTrigger(records, setupIndex, candidate, settings, searchEnd),

            EntryResearchMode.IntrabarMicroRetest =>
                FindMicroRetestTrigger(records, setupIndex, candidate, settings, searchEnd),

            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported entry research mode."),
        };
    }

    private static EntryTrigger? FindM1CloseTrigger(
        IReadOnlyList<ReplayOutputRecord> records,
        int setupIndex,
        EntryExperimentCandidate candidate,
        DateTimeOffset searchEnd)
    {
        DateTimeOffset barOpen = FloorToMinute(candidate.SetupAtUtc);
        DateTimeOffset closeBoundary = barOpen.AddMinutes(1);

        for (int index = setupIndex + 1; index < records.Count; index++)
        {
            ReplayOutputRecord record = records[index];
            if (record.MarketEvent.TimestampUtc > searchEnd)
            {
                break;
            }

            if (record.MarketEvent.TimestampUtc >= closeBoundary
                && record.MarketEvent is TickEvent
                && record.FeatureState is not null)
            {
                return TriggerFromRecord(record, EntryResearchMode.M1Close);
            }
        }

        return null;
    }

    private static EntryTrigger? FindDecelerationTrigger(
        IReadOnlyList<ReplayOutputRecord> records,
        int setupIndex,
        EntryExperimentCandidate candidate,
        EntryExperimentSettings settings,
        DateTimeOffset searchEnd)
    {
        string opposingPeakName = candidate.IntendedSide == TradeSide.Long
            ? FeatureNames.PeakVelocityDown
            : FeatureNames.PeakVelocityUp;

        for (int index = setupIndex; index < records.Count; index++)
        {
            ReplayOutputRecord record = records[index];
            if (record.MarketEvent.TimestampUtc > searchEnd)
            {
                break;
            }

            if (record.FeatureState is not XauMarketState state
                || !TryFeature(state, FeatureNames.DecelerationRatio, out double deceleration)
                || !TryFeature(state, opposingPeakName, out double opposingPeak))
            {
                continue;
            }

            if (opposingPeak > 0 && deceleration >= settings.DecelerationRatioThreshold)
            {
                return TriggerFromRecord(
                    record,
                    EntryResearchMode.IntrabarDeceleration);
            }
        }

        return null;
    }

    private static EntryTrigger? FindDirectionFlipTrigger(
        IReadOnlyList<ReplayOutputRecord> records,
        int setupIndex,
        EntryExperimentCandidate candidate,
        EntryExperimentSettings settings,
        DateTimeOffset searchEnd)
    {
        for (int index = setupIndex; index < records.Count; index++)
        {
            ReplayOutputRecord record = records[index];
            if (record.MarketEvent.TimestampUtc > searchEnd)
            {
                break;
            }

            if (record.FeatureState is not XauMarketState state
                || !TryFeature(state, FeatureNames.DirectionFlipAgeMs, out double flipAgeMs)
                || !TryFeature(state, FeatureNames.Velocity500ms, out double velocity))
            {
                continue;
            }

            bool intendedVelocity = candidate.IntendedSide == TradeSide.Long
                ? velocity > 0
                : velocity < 0;

            if (intendedVelocity
                && flipAgeMs <= settings.DirectionFlipMaxAge.TotalMilliseconds)
            {
                return TriggerFromRecord(
                    record,
                    EntryResearchMode.IntrabarDirectionFlip);
            }
        }

        return null;
    }

    private static EntryTrigger? FindMicroRetestTrigger(
        IReadOnlyList<ReplayOutputRecord> records,
        int setupIndex,
        EntryExperimentCandidate candidate,
        EntryExperimentSettings settings,
        DateTimeOffset searchEnd)
    {
        ReplayOutputRecord setupRecord = records[setupIndex];
        if (setupRecord.MarketEvent is not TickEvent setupTick)
        {
            return null;
        }

        decimal anchor = Mid(setupTick);
        decimal favorableExtreme = anchor;
        decimal retestExtreme = anchor;
        int stage = 0;

        for (int index = setupIndex + 1; index < records.Count; index++)
        {
            ReplayOutputRecord record = records[index];
            if (record.MarketEvent.TimestampUtc > searchEnd)
            {
                break;
            }

            if (record.MarketEvent is not TickEvent tick || record.FeatureState is null)
            {
                continue;
            }

            decimal mid = Mid(tick);

            if (candidate.IntendedSide == TradeSide.Long)
            {
                if (stage == 0)
                {
                    favorableExtreme = Math.Max(favorableExtreme, mid);
                    if (favorableExtreme - anchor >= settings.MicroRetestAdvancePrice)
                    {
                        stage = 1;
                    }

                    continue;
                }

                if (stage == 1)
                {
                    favorableExtreme = Math.Max(favorableExtreme, mid);
                    if (favorableExtreme - mid >= settings.MicroRetestDepthPrice)
                    {
                        retestExtreme = mid;
                        stage = 2;
                    }

                    continue;
                }

                retestExtreme = Math.Min(retestExtreme, mid);
                if (mid - retestExtreme >= settings.MicroRetestResumePrice)
                {
                    return TriggerFromRecord(
                        record,
                        EntryResearchMode.IntrabarMicroRetest);
                }
            }
            else
            {
                if (stage == 0)
                {
                    favorableExtreme = Math.Min(favorableExtreme, mid);
                    if (anchor - favorableExtreme >= settings.MicroRetestAdvancePrice)
                    {
                        stage = 1;
                    }

                    continue;
                }

                if (stage == 1)
                {
                    favorableExtreme = Math.Min(favorableExtreme, mid);
                    if (mid - favorableExtreme >= settings.MicroRetestDepthPrice)
                    {
                        retestExtreme = mid;
                        stage = 2;
                    }

                    continue;
                }

                retestExtreme = Math.Max(retestExtreme, mid);
                if (retestExtreme - mid >= settings.MicroRetestResumePrice)
                {
                    return TriggerFromRecord(
                        record,
                        EntryResearchMode.IntrabarMicroRetest);
                }
            }
        }

        return null;
    }

    private static EntryTrigger TriggerFromRecord(
        ReplayOutputRecord record,
        EntryResearchMode mode)
    {
        if (record.MarketEvent is not TickEvent tick
            || record.FeatureState is not XauMarketState state)
        {
            throw new InvalidDataException(
                "Entry triggers require a tick record with its causal feature state.");
        }

        return new EntryTrigger(
            mode,
            record.Ordinal,
            state.MarketStateId,
            tick.TimestampUtc,
            tick.SequenceId,
            tick.Bid,
            tick.Ask);
    }

    private static int FindSetupIndex(
        IReadOnlyList<ReplayOutputRecord> records,
        EntryExperimentCandidate candidate)
    {
        for (int index = 0; index < records.Count; index++)
        {
            ReplayOutputRecord record = records[index];
            if (record.FeatureState is XauMarketState state
                && state.MarketStateId == candidate.MarketStateId)
            {
                if (state.SequenceId != candidate.SourceSequenceId
                    || state.TimestampUtc != candidate.SetupAtUtc)
                {
                    throw new InvalidDataException(
                        "Candidate identity does not match the referenced market state.");
                }

                return index;
            }
        }

        throw new InvalidDataException(
            $"Candidate market state {candidate.MarketStateId} was not found in replay records.");
    }

    private static SymbolSpecification FindSpecification(
        IReadOnlyList<ReplayOutputRecord> records,
        int entryIndex)
    {
        for (int index = entryIndex; index >= 0; index--)
        {
            if (records[index].MarketEvent is SymbolSpecificationEvent specificationEvent)
            {
                return specificationEvent.Specification;
            }
        }

        throw new InvalidDataException(
            "Cost-inclusive entry evaluation requires symbol specification before entry.");
    }

    private static EntryModeSummary MakeSummary(
        IGrouping<EntryEvaluationKey, EntryTradeOutcome> group)
    {
        EntryTradeOutcome[] all = group.ToArray();
        EntryTradeOutcome[] entered = all
            .Where(static outcome => outcome.Outcome != EntryTradeOutcomeKind.NoEntry)
            .OrderBy(static outcome => outcome.Trigger!.TriggerAtUtc)
            .ThenBy(static outcome => outcome.Trigger!.SequenceId)
            .ThenBy(static outcome => outcome.CandidateId)
            .ToArray();

        int targetCount = entered.Count(
            static outcome => outcome.Outcome == EntryTradeOutcomeKind.TargetFirst);
        int adverseCount = entered.Count(
            static outcome => outcome.Outcome == EntryTradeOutcomeKind.AdverseFirst);
        int censoredCount = entered.Count(
            static outcome => outcome.Outcome == EntryTradeOutcomeKind.Censored);
        int noEntry = all.Length - entered.Length;

        decimal[] net = entered
            .Select(static outcome => outcome.NetResultPrice!.Value)
            .ToArray();
        decimal[] wins = net.Where(static value => value > 0).ToArray();
        decimal[] losses = net.Where(static value => value < 0).ToArray();

        decimal? profitFactor = losses.Length == 0
            ? null
            : wins.Sum() / Math.Abs(losses.Sum());

        TimeSpan? medianTarget = Median(
            entered
                .Where(static outcome => outcome.TimeToTarget is not null)
                .Select(static outcome => outcome.TimeToTarget!.Value)
                .Order()
                .ToArray());

        return new EntryModeSummary(
            group.Key,
            CandidateCount: all.Length,
            EntryCount: entered.Length,
            NoEntryCount: noEntry,
            TargetFirstCount: targetCount,
            AdverseFirstCount: adverseCount,
            CensoredCount: censoredCount,
            PTargetFirst: entered.Length == 0 ? null : (double)targetCount / entered.Length,
            FalseEntryRate: entered.Length == 0 ? null : (double)(adverseCount + censoredCount) / entered.Length,
            ExpectedValueAfterCostsPrice: Average(net),
            AverageMfePrice: Average(
                entered.Select(static outcome => outcome.MfePrice!.Value).ToArray()),
            AverageMaePrice: Average(
                entered.Select(static outcome => outcome.MaePrice!.Value).ToArray()),
            MedianTimeToTarget: medianTarget,
            ProfitFactor: profitFactor,
            AverageWinPrice: Average(wins),
            AverageLossPrice: Average(losses),
            MaxDrawdownPrice: MaxDrawdown(net));
    }

    private static decimal? Average(decimal[] values)
    {
        return values.Length == 0 ? null : values.Average();
    }

    private static TimeSpan? Median(TimeSpan[] values)
    {
        if (values.Length == 0)
        {
            return null;
        }

        int middle = values.Length / 2;
        if (values.Length % 2 == 1)
        {
            return values[middle];
        }

        return TimeSpan.FromTicks(
            checked((values[middle - 1].Ticks + values[middle].Ticks) / 2));
    }

    private static decimal? MaxDrawdown(decimal[] netResults)
    {
        if (netResults.Length == 0)
        {
            return null;
        }

        decimal equity = 0;
        decimal peak = 0;
        decimal maxDrawdown = 0;

        foreach (decimal result in netResults)
        {
            equity += result;
            peak = Math.Max(peak, equity);
            maxDrawdown = Math.Max(maxDrawdown, peak - equity);
        }

        return maxDrawdown;
    }

    private static decimal NetAtTick(
        TradeSide side,
        decimal bid,
        decimal ask,
        decimal entryExecutionPrice,
        decimal slippagePrice,
        decimal commissionPrice)
    {
        if (side == TradeSide.Long)
        {
            decimal exitExecution = bid - slippagePrice;
            return exitExecution - entryExecutionPrice - commissionPrice;
        }

        decimal shortExitExecution = ask + slippagePrice;
        return entryExecutionPrice - shortExitExecution - commissionPrice;
    }

    private static void ApplyExcursion(
        decimal net,
        ref decimal mfe,
        ref decimal mae)
    {
        if (net > mfe)
        {
            mfe = net;
        }

        decimal adverse = -net;
        if (adverse > mae)
        {
            mae = adverse;
        }
    }

    private static bool TryFeature(
        XauMarketState state,
        string name,
        out double value)
    {
        NumericFeatureValue? feature = state.Features.FirstOrDefault(
            feature => string.Equals(feature.Name, name, StringComparison.Ordinal));

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

    private static decimal Mid(TickEvent tick)
    {
        return (tick.Bid + tick.Ask) / 2m;
    }

    private static DateTimeOffset FloorToMinute(DateTimeOffset timestampUtc)
    {
        DateTimeOffset utc = timestampUtc.ToUniversalTime();
        return new DateTimeOffset(
            utc.Year,
            utc.Month,
            utc.Day,
            utc.Hour,
            utc.Minute,
            0,
            TimeSpan.Zero);
    }

    private static void ValidateOrdered(IReadOnlyList<ReplayOutputRecord> records)
    {
        DateTimeOffset? previous = null;

        for (int index = 0; index < records.Count; index++)
        {
            ReplayOutputRecord record = records[index];

            if (record.Ordinal != index)
            {
                throw new InvalidDataException(
                    "Entry experiment requires contiguous source-order replay ordinals.");
            }

            if (previous is DateTimeOffset prior
                && record.MarketEvent.TimestampUtc < prior)
            {
                throw new InvalidDataException(
                    "Entry experiment requires ordered replay records.");
            }

            previous = record.MarketEvent.TimestampUtc;
        }
    }
}
