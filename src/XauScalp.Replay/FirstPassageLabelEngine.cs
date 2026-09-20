using XauScalp.Domain;

namespace XauScalp.Replay;

public sealed class FirstPassageLabelEngine
{
    public IReadOnlyList<FirstPassageStateLabels> Generate(
        IReadOnlyList<ReplayOutputRecord> records,
        FirstPassageLabelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(settings);

        ValidateOrdered(records);

        var labels = new List<FirstPassageStateLabels>();

        for (int stateIndex = 0; stateIndex < records.Count; stateIndex++)
        {
            ReplayOutputRecord stateRecord = records[stateIndex];
            if (stateRecord.MarketEvent is not TickEvent stateTick
                || stateRecord.FeatureState is not XauMarketState state)
            {
                continue;
            }

            decimal referenceMid = state.Mid;
            var horizons = new List<FirstPassageHorizonLabel>(
                settings.HoldingHorizons.Count);

            foreach (TimeSpan horizon in settings.HoldingHorizons)
            {
                horizons.Add(
                    EvaluateHorizon(
                        records,
                        stateIndex,
                        stateTick,
                        referenceMid,
                        horizon,
                        settings));
            }

            labels.Add(
                new FirstPassageStateLabels(
                    state.MarketStateId,
                    state.TimestampUtc,
                    state.SequenceId,
                    referenceMid,
                    horizons));
        }

        return labels;
    }

    private static FirstPassageHorizonLabel EvaluateHorizon(
        IReadOnlyList<ReplayOutputRecord> records,
        int stateIndex,
        TickEvent stateTick,
        decimal referenceMid,
        TimeSpan horizon,
        FirstPassageLabelSettings settings)
    {
        DateTimeOffset horizonEnd = stateTick.TimestampUtc + horizon;

        var upHits = settings.TargetDistancesPrice
            .ToDictionary(static distance => distance, static _ => (PassageHit?)null);
        var downHits = settings.TargetDistancesPrice
            .ToDictionary(static distance => distance, static _ => (PassageHit?)null);

        decimal[] allDownDistances = settings.TargetDistancesPrice
            .Concat(settings.AdverseBarriersPrice)
            .Distinct()
            .ToArray();
        decimal[] allUpDistances = allDownDistances;

        var anyUpHits = allUpDistances
            .ToDictionary(static distance => distance, static _ => (PassageHit?)null);
        var anyDownHits = allDownDistances
            .ToDictionary(static distance => distance, static _ => (PassageHit?)null);

        decimal longMfe = 0;
        decimal longMae = 0;
        TimeSpan longMfeTime = TimeSpan.Zero;
        TimeSpan longMaeTime = TimeSpan.Zero;

        bool sawTickAtOrBeyondHorizon = false;
        DateTimeOffset lastTickTimestamp = stateTick.TimestampUtc;

        for (int index = stateIndex + 1; index < records.Count; index++)
        {
            if (records[index].MarketEvent is not TickEvent tick)
            {
                continue;
            }

            if (tick.TimestampUtc > horizonEnd)
            {
                sawTickAtOrBeyondHorizon = true;
                break;
            }

            lastTickTimestamp = tick.TimestampUtc;
            decimal mid = Mid(tick);
            decimal delta = mid - referenceMid;
            TimeSpan elapsed = tick.TimestampUtc - stateTick.TimestampUtc;

            if (delta > longMfe)
            {
                longMfe = delta;
                longMfeTime = elapsed;
            }

            decimal adverse = -delta;
            if (adverse > longMae)
            {
                longMae = adverse;
                longMaeTime = elapsed;
            }

            foreach (decimal distance in allUpDistances)
            {
                if (anyUpHits[distance] is null && delta >= distance)
                {
                    anyUpHits[distance] = new PassageHit(
                        tick.TimestampUtc,
                        tick.SequenceId,
                        mid,
                        elapsed);
                }

                if (anyDownHits[distance] is null && delta <= -distance)
                {
                    anyDownHits[distance] = new PassageHit(
                        tick.TimestampUtc,
                        tick.SequenceId,
                        mid,
                        elapsed);
                }
            }
        }

        foreach (decimal distance in settings.TargetDistancesPrice)
        {
            upHits[distance] = anyUpHits[distance];
            downHits[distance] = anyDownHits[distance];
        }

        DistanceHitPair[] distanceHits = settings.TargetDistancesPrice
            .Select(
                distance => new DistanceHitPair(
                    distance,
                    upHits[distance],
                    downHits[distance]))
            .ToArray();

        var barrierLabels = new List<BarrierFirstLabel>(
            2 * settings.TargetDistancesPrice.Count * settings.AdverseBarriersPrice.Count);

        foreach (decimal target in settings.TargetDistancesPrice)
        {
            foreach (decimal barrier in settings.AdverseBarriersPrice)
            {
                barrierLabels.Add(
                    MakeBarrierLabel(
                        TradeSide.Long,
                        target,
                        barrier,
                        anyUpHits[target],
                        anyDownHits[barrier]));

                barrierLabels.Add(
                    MakeBarrierLabel(
                        TradeSide.Short,
                        target,
                        barrier,
                        anyDownHits[target],
                        anyUpHits[barrier]));
            }
        }

        bool datasetEndedBeforeHorizon =
            !sawTickAtOrBeyondHorizon && lastTickTimestamp < horizonEnd;

        return new FirstPassageHorizonLabel(
            horizon,
            distanceHits,
            barrierLabels,
            new DirectionalExcursion(
                TradeSide.Long,
                longMfe,
                longMae,
                longMfeTime,
                longMaeTime),
            new DirectionalExcursion(
                TradeSide.Short,
                longMae,
                longMfe,
                longMaeTime,
                longMfeTime),
            datasetEndedBeforeHorizon);
    }

    private static BarrierFirstLabel MakeBarrierLabel(
        TradeSide side,
        decimal target,
        decimal barrier,
        PassageHit? targetHit,
        PassageHit? adverseHit)
    {
        FirstPassageOutcome outcome;

        if (targetHit is null && adverseHit is null)
        {
            outcome = FirstPassageOutcome.Censored;
        }
        else if (targetHit is null)
        {
            outcome = FirstPassageOutcome.AdverseFirst;
        }
        else if (adverseHit is null)
        {
            outcome = FirstPassageOutcome.TargetFirst;
        }
        else
        {
            outcome = ComesBeforeOrAt(targetHit, adverseHit)
                ? FirstPassageOutcome.TargetFirst
                : FirstPassageOutcome.AdverseFirst;
        }

        return new BarrierFirstLabel(
            side,
            target,
            barrier,
            outcome,
            targetHit,
            adverseHit);
    }

    private static bool ComesBeforeOrAt(PassageHit left, PassageHit right)
    {
        if (left.HitAtUtc != right.HitAtUtc)
        {
            return left.HitAtUtc < right.HitAtUtc;
        }

        return left.SequenceId <= right.SequenceId;
    }

    private static decimal Mid(TickEvent tick)
    {
        return (tick.Bid + tick.Ask) / 2m;
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
                    "Replay output ordinals must be contiguous and source ordered.");
            }

            if (previous is DateTimeOffset prior
                && record.MarketEvent.TimestampUtc < prior)
            {
                throw new InvalidDataException(
                    "First-passage labeling requires source-ordered replay records.");
            }

            previous = record.MarketEvent.TimestampUtc;
        }
    }
}
