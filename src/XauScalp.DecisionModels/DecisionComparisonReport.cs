using XauScalp.Domain;

namespace XauScalp.DecisionModels;

public sealed record AllStateModelMetrics(
    DecisionAuthorityRole Role,
    DecisionModelType Model,
    int SampleCount,
    double? BrierUp5,
    double? BrierDown5,
    double? BrierUp10,
    double? BrierDown10);

public sealed record ExecutedTradeModelMetrics(
    DecisionModelType Model,
    int TradeCount,
    double? WinRate,
    decimal? AverageNetPnlPrice,
    decimal? MaxDrawdownPrice);

public sealed record DecisionComparisonReport(
    IReadOnlyList<AllStateModelMetrics> AllState,
    IReadOnlyList<ExecutedTradeModelMetrics> ExecutedTrades);

public static class DecisionComparisonReportBuilder
{
    public static DecisionComparisonReport Build(
        IEnumerable<DecisionComparisonBundle> bundles)
    {
        ArgumentNullException.ThrowIfNull(bundles);
        DecisionComparisonBundle[] snapshot = bundles.ToArray();

        var labeled = new List<LabeledDecision>();

        foreach (DecisionComparisonBundle bundle in snapshot)
        {
            if (bundle.FutureLabels is null)
            {
                continue;
            }

            AddLabeled(labeled, bundle.Comparison.Primary, bundle.FutureLabels);
            if (bundle.Comparison.Shadow is not null)
            {
                AddLabeled(labeled, bundle.Comparison.Shadow, bundle.FutureLabels);
            }
        }

        AllStateModelMetrics[] allState = labeled
            .GroupBy(item => (item.Record.Role, item.Record.ActualModel!.Value))
            .Select(
                group => new AllStateModelMetrics(
                    group.Key.Role,
                    group.Key.Value,
                    group.Count(),
                    Brier(group, static labels => labels.Up5First, static decision => decision.PUp5First),
                    Brier(group, static labels => labels.Down5First, static decision => decision.PDown5First),
                    Brier(group, static labels => labels.Up10First, static decision => decision.PUp10First),
                    Brier(group, static labels => labels.Down10First, static decision => decision.PDown10First)))
            .OrderBy(static metrics => metrics.Role)
            .ThenBy(static metrics => metrics.Model)
            .ToArray();

        ExecutedTradeModelMetrics[] executed = snapshot
            .Where(
                static bundle => bundle.ExecutionOutcome is not null
                    && bundle.Comparison.Primary.Decision is not null
                    && bundle.Comparison.Primary.ActualModel is not null)
            .GroupBy(bundle => bundle.Comparison.Primary.ActualModel!.Value)
            .Select(MakeExecutedMetrics)
            .OrderBy(static metrics => metrics.Model)
            .ToArray();

        return new DecisionComparisonReport(allState, executed);
    }

    private static void AddLabeled(
        List<LabeledDecision> output,
        ModelDecisionRecord record,
        DecisionFutureLabels labels)
    {
        if (record.Succeeded
            && record.Decision is not null
            && record.ActualModel is not null)
        {
            output.Add(new LabeledDecision(record, labels));
        }
    }

    private static double? Brier(
        IEnumerable<LabeledDecision> items,
        Func<DecisionFutureLabels, bool?> labelSelector,
        Func<XauDecision, double> probabilitySelector)
    {
        double[] errors = items
            .Select(
                item =>
                {
                    bool? label = labelSelector(item.Labels);
                    if (label is null)
                    {
                        return (double?)null;
                    }

                    double actual = label.Value ? 1 : 0;
                    double difference = probabilitySelector(item.Record.Decision!) - actual;
                    return difference * difference;
                })
            .Where(static value => value is not null)
            .Select(static value => value!.Value)
            .ToArray();

        return errors.Length == 0 ? null : errors.Average();
    }

    private static ExecutedTradeModelMetrics MakeExecutedMetrics(
        IGrouping<DecisionModelType, DecisionComparisonBundle> group)
    {
        DecisionComparisonBundle[] ordered = group
            .OrderBy(static bundle => bundle.ExecutionOutcome!.ClosedAtUtc)
            .ToArray();

        decimal[] pnl = ordered
            .Select(static bundle => bundle.ExecutionOutcome!.NetPnlPrice)
            .ToArray();

        int wins = pnl.Count(static value => value > 0);

        return new ExecutedTradeModelMetrics(
            group.Key,
            pnl.Length,
            pnl.Length == 0 ? null : (double)wins / pnl.Length,
            pnl.Length == 0 ? null : pnl.Average(),
            MaxDrawdown(pnl));
    }

    private static decimal? MaxDrawdown(decimal[] values)
    {
        if (values.Length == 0)
        {
            return null;
        }

        decimal equity = 0;
        decimal peak = 0;
        decimal maximum = 0;

        foreach (decimal value in values)
        {
            equity += value;
            peak = Math.Max(peak, equity);
            maximum = Math.Max(maximum, peak - equity);
        }

        return maximum;
    }

    private sealed record LabeledDecision(
        ModelDecisionRecord Record,
        DecisionFutureLabels Labels);
}
