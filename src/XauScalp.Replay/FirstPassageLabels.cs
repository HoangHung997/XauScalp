using XauScalp.Domain;

namespace XauScalp.Replay;

public enum FirstPassageOutcome
{
    TargetFirst = 0,
    AdverseFirst = 1,
    Censored = 2,
}

public sealed record PassageHit(
    DateTimeOffset HitAtUtc,
    long SequenceId,
    decimal Price,
    TimeSpan Elapsed);

public sealed record DirectionalExcursion(
    TradeSide Side,
    decimal MfePrice,
    decimal MaePrice,
    TimeSpan TimeToMfe,
    TimeSpan TimeToMae);

public sealed record DistanceHitPair(
    decimal DistancePrice,
    PassageHit? UpHit,
    PassageHit? DownHit);

public sealed record BarrierFirstLabel(
    TradeSide Side,
    decimal TargetDistancePrice,
    decimal AdverseBarrierPrice,
    FirstPassageOutcome Outcome,
    PassageHit? TargetHit,
    PassageHit? AdverseHit);

public sealed record FirstPassageHorizonLabel(
    TimeSpan Horizon,
    IReadOnlyList<DistanceHitPair> DistanceHits,
    IReadOnlyList<BarrierFirstLabel> BarrierFirst,
    DirectionalExcursion LongExcursion,
    DirectionalExcursion ShortExcursion,
    bool IsCensoredByDatasetEnd);

public sealed record FirstPassageStateLabels(
    Guid MarketStateId,
    DateTimeOffset StateTimestampUtc,
    long StateSequenceId,
    decimal ReferenceMidPrice,
    IReadOnlyList<FirstPassageHorizonLabel> Horizons);

public sealed class FirstPassageLabelSettings
{
    public FirstPassageLabelSettings(
        IEnumerable<decimal> targetDistancesPrice,
        IEnumerable<decimal> adverseBarriersPrice,
        IEnumerable<TimeSpan> holdingHorizons)
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

        if (horizons.Length == 0 || horizons.Any(static horizon => horizon <= TimeSpan.Zero))
        {
            throw new ArgumentException(
                "At least one positive holding horizon is required.",
                nameof(holdingHorizons));
        }

        HoldingHorizons = horizons;
    }

    public IReadOnlyList<decimal> TargetDistancesPrice { get; }

    public IReadOnlyList<decimal> AdverseBarriersPrice { get; }

    public IReadOnlyList<TimeSpan> HoldingHorizons { get; }

    public static FirstPassageLabelSettings XauScalpV1 { get; } = new(
        targetDistancesPrice: [5m, 10m],
        adverseBarriersPrice: [2m, 3m, 4m, 5m],
        holdingHorizons:
        [
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(3),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(15),
        ]);

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
}
