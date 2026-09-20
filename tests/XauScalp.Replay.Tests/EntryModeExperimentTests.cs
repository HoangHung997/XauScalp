using System.Text;
using XauScalp.Domain;
using XauScalp.Features;

namespace XauScalp.Replay.Tests;

public sealed class EntryModeExperimentTests
{
    [Fact]
    public void AllFiveModes_UseSameCandidateAndProduceDistinctCausalTriggers()
    {
        DateTimeOffset start = Utc(12, 0, 10);
        ReplayOutputRecord[] records = BuildTriggerDataset(start);
        XauMarketState setupState = records[1].FeatureState!;
        var candidate = new EntryExperimentCandidate(
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            setupState.MarketStateId,
            setupState.SequenceId,
            setupState.TimestampUtc,
            TradeSide.Long);

        var settings = new EntryExperimentSettings(
            targetDistancesPrice: [1m],
            adverseBarriersPrice: [1m],
            holdingHorizons: [TimeSpan.FromMinutes(2)],
            triggerSearchHorizon: TimeSpan.FromMinutes(2),
            decelerationRatioThreshold: 0.70,
            directionFlipMaxAge: TimeSpan.FromSeconds(1),
            microRetestAdvancePrice: 0.50m,
            microRetestDepthPrice: 0.20m,
            microRetestResumePrice: 0.15m);

        EntryExperimentResult result = new EntryModeExperiment().Run(
            records,
            [candidate],
            settings,
            ReplayCostScenario.Ideal);

        EntryTradeOutcome[] onePerMode = result.Outcomes.ToArray();
        Assert.Equal(5, onePerMode.Length);

        Assert.Equal(
            start,
            Outcome(onePerMode, EntryResearchMode.IntrabarImmediate).Trigger!.TriggerAtUtc);

        Assert.Equal(
            start.AddSeconds(2),
            Outcome(onePerMode, EntryResearchMode.IntrabarDeceleration).Trigger!.TriggerAtUtc);

        Assert.Equal(
            start.AddSeconds(3),
            Outcome(onePerMode, EntryResearchMode.IntrabarDirectionFlip).Trigger!.TriggerAtUtc);

        Assert.Equal(
            start.AddSeconds(6),
            Outcome(onePerMode, EntryResearchMode.IntrabarMicroRetest).Trigger!.TriggerAtUtc);

        Assert.Equal(
            Utc(12, 1, 0),
            Outcome(onePerMode, EntryResearchMode.M1Close).Trigger!.TriggerAtUtc);

        Assert.All(
            onePerMode,
            outcome => Assert.Equal("ideal", outcome.CostScenarioName));
    }

    [Fact]
    public void CostInclusiveEvaluation_UsesBidAskSlippageAndCommission()
    {
        DateTimeOffset start = Utc(12, 0, 10);
        ReplayOutputRecord[] records =
        [
            SpecRecord(0, start.AddSeconds(-1)),
            TickRecord(1, start, 100m, BasicState(1, start, 100m)),
            TickRecord(2, start.AddSeconds(1), 102m, BasicState(2, start.AddSeconds(1), 102m)),
        ];

        XauMarketState setup = records[1].FeatureState!;
        var candidate = new EntryExperimentCandidate(
            Guid.NewGuid(),
            setup.MarketStateId,
            setup.SequenceId,
            setup.TimestampUtc,
            TradeSide.Long);

        var settings = new EntryExperimentSettings(
            targetDistancesPrice: [1m],
            adverseBarriersPrice: [5m],
            holdingHorizons: [TimeSpan.FromSeconds(5)],
            triggerSearchHorizon: TimeSpan.FromSeconds(1),
            decelerationRatioThreshold: 0.5,
            directionFlipMaxAge: TimeSpan.FromSeconds(1),
            microRetestAdvancePrice: 0.5m,
            microRetestDepthPrice: 0.2m,
            microRetestResumePrice: 0.1m);

        var cost = new ReplayCostScenario(
            "costed",
            estimatedLatencyMs: 10,
            estimatedSlippagePoints: 2,
            commissionPerLot: 1.25m);

        EntryTradeOutcome immediate = Outcome(
            new EntryModeExperiment()
                .Run(records, [candidate], settings, cost)
                .Outcomes,
            EntryResearchMode.IntrabarImmediate);

        // point=.01 => .02 slippage each side.
        // commission 1.25 / tickValue 1.25 * tickSize .01 => .01 price.
        // Long entry = ask 100.20 + .02 = 100.22.
        // Future exit = bid 102.00 - .02 = 101.98.
        // Net = 101.98 - 100.22 - .01 = 1.75.
        Assert.Equal(100.22m, immediate.EntryExecutionPrice);
        Assert.Equal(EntryTradeOutcomeKind.TargetFirst, immediate.Outcome);
        Assert.Equal(1.75m, immediate.NetResultPrice);
        Assert.Equal(TimeSpan.FromSeconds(1), immediate.TimeToTarget);
    }

    [Fact]
    public void Summary_ReportsDenominatorsExpectedValueFalseEntryAndDrawdown()
    {
        DateTimeOffset start = Utc(12, 0, 10);
        ReplayOutputRecord[] records =
        [
            SpecRecord(0, start.AddSeconds(-1)),
            TickRecord(1, start, 100m, BasicState(1, start, 100m)),
            TickRecord(2, start.AddSeconds(1), 102m, BasicState(2, start.AddSeconds(1), 102m)),
            TickRecord(3, start.AddSeconds(10), 100m, BasicState(3, start.AddSeconds(10), 100m)),
            TickRecord(4, start.AddSeconds(11), 98m, BasicState(4, start.AddSeconds(11), 98m)),
        ];

        XauMarketState first = records[1].FeatureState!;
        XauMarketState second = records[3].FeatureState!;

        EntryExperimentCandidate[] candidates =
        [
            new(
                Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
                first.MarketStateId,
                first.SequenceId,
                first.TimestampUtc,
                TradeSide.Long),
            new(
                Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
                second.MarketStateId,
                second.SequenceId,
                second.TimestampUtc,
                TradeSide.Long),
        ];

        var settings = new EntryExperimentSettings(
            targetDistancesPrice: [1m],
            adverseBarriersPrice: [1m],
            holdingHorizons: [TimeSpan.FromSeconds(5)],
            triggerSearchHorizon: TimeSpan.FromSeconds(2),
            decelerationRatioThreshold: 0.5,
            directionFlipMaxAge: TimeSpan.FromSeconds(1),
            microRetestAdvancePrice: 0.5m,
            microRetestDepthPrice: 0.2m,
            microRetestResumePrice: 0.1m);

        EntryExperimentResult result = new EntryModeExperiment().Run(
            records,
            candidates,
            settings,
            ReplayCostScenario.Ideal);

        EntryModeSummary immediate = Assert.Single(
            result.Summaries,
            summary => summary.Key.Mode == EntryResearchMode.IntrabarImmediate);

        Assert.Equal(2, immediate.CandidateCount);
        Assert.Equal(2, immediate.EntryCount);
        Assert.Equal(1, immediate.TargetFirstCount);
        Assert.Equal(1, immediate.AdverseFirstCount);
        Assert.Equal(0.5, immediate.PTargetFirst);
        Assert.Equal(0.5, immediate.FalseEntryRate);
        Assert.NotNull(immediate.ExpectedValueAfterCostsPrice);
        Assert.NotNull(immediate.MaxDrawdownPrice);
        Assert.True(immediate.MaxDrawdownPrice > 0);
    }

    [Fact]
    public async Task ResearchExporter_WritesLabelsOutcomesAndSummariesAsJsonl()
    {
        var label = new FirstPassageStateLabels(
            Guid.NewGuid(),
            Utc(12, 0, 0),
            1,
            100m,
            []);

        var outcome = new EntryTradeOutcome(
            Guid.NewGuid(),
            TradeSide.Long,
            new EntryEvaluationKey(
                EntryResearchMode.IntrabarImmediate,
                5m,
                3m,
                TimeSpan.FromMinutes(1)),
            EntryTradeOutcomeKind.Censored,
            "baseline",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        var summary = new EntryModeSummary(
            outcome.Key,
            1,
            0,
            1,
            0,
            0,
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        await using var labelsStream = new MemoryStream();
        await ResearchResultExporter.WriteFirstPassageLabelsJsonlAsync(
            labelsStream,
            [label]);

        await using var outcomeStream = new MemoryStream();
        await ResearchResultExporter.WriteEntryOutcomesJsonlAsync(
            outcomeStream,
            [outcome]);

        await using var summaryStream = new MemoryStream();
        await ResearchResultExporter.WriteEntrySummariesJsonlAsync(
            summaryStream,
            [summary]);

        Assert.NotEmpty(Encoding.UTF8.GetString(labelsStream.ToArray()));
        Assert.NotEmpty(Encoding.UTF8.GetString(outcomeStream.ToArray()));
        Assert.NotEmpty(Encoding.UTF8.GetString(summaryStream.ToArray()));
    }

    private static ReplayOutputRecord[] BuildTriggerDataset(DateTimeOffset start)
    {
        return
        [
            SpecRecord(0, start.AddSeconds(-1)),
            TickRecord(1, start, 100m, StateWithFeatures(1, start, 100m)),
            TickRecord(
                2,
                start.AddSeconds(1),
                100.60m,
                StateWithFeatures(2, start.AddSeconds(1), 100.60m)),
            TickRecord(
                3,
                start.AddSeconds(2),
                101m,
                StateWithFeatures(
                    3,
                    start.AddSeconds(2),
                    101m,
                    (FeatureNames.PeakVelocityDown, 4),
                    (FeatureNames.DecelerationRatio, 0.80))),
            TickRecord(
                4,
                start.AddSeconds(3),
                100.70m,
                StateWithFeatures(
                    4,
                    start.AddSeconds(3),
                    100.70m,
                    (FeatureNames.DirectionFlipAgeMs, 100),
                    (FeatureNames.Velocity500ms, 0.50))),
            TickRecord(
                5,
                start.AddSeconds(4),
                100.40m,
                StateWithFeatures(5, start.AddSeconds(4), 100.40m)),
            TickRecord(
                6,
                start.AddSeconds(5),
                100.45m,
                StateWithFeatures(6, start.AddSeconds(5), 100.45m)),
            TickRecord(
                7,
                start.AddSeconds(6),
                100.65m,
                StateWithFeatures(7, start.AddSeconds(6), 100.65m)),
            TickRecord(
                8,
                Utc(12, 1, 0),
                101.50m,
                StateWithFeatures(8, Utc(12, 1, 0), 101.50m)),
        ];
    }

    private static EntryTradeOutcome Outcome(
        IEnumerable<EntryTradeOutcome> outcomes,
        EntryResearchMode mode)
    {
        return Assert.Single(outcomes, outcome => outcome.Key.Mode == mode);
    }

    private static ReplayOutputRecord SpecRecord(
        long ordinal,
        DateTimeOffset timestamp)
    {
        var specification = new SymbolSpecificationEvent(
            ContractVersions.MarketEventV1,
            timestamp,
            timestamp,
            0,
            "entry-test",
            "XAUUSD",
            "XAUUSD",
            new SymbolSpecification(
                digits: 2,
                point: 0.01m,
                tickSize: 0.01m,
                tickValue: 1.25m,
                contractSize: 100m,
                minVolume: 0.01m,
                maxVolume: 100m,
                volumeStep: 0.01m,
                minStopDistance: 0.50m));

        return new ReplayOutputRecord(
            ordinal,
            specification,
            null,
            ReplayCostScenario.Ideal);
    }

    private static ReplayOutputRecord TickRecord(
        long ordinal,
        DateTimeOffset timestamp,
        decimal bid,
        XauMarketState state)
    {
        TickEvent tick = Tick(ordinal, timestamp, bid);
        return new ReplayOutputRecord(
            ordinal,
            tick,
            state,
            ReplayCostScenario.Ideal);
    }

    private static TickEvent Tick(
        long sequence,
        DateTimeOffset timestamp,
        decimal bid)
    {
        return new TickEvent(
            ContractVersions.MarketEventV1,
            timestamp,
            timestamp,
            sequence,
            "entry-test",
            "XAUUSD",
            "XAUUSD",
            bid,
            bid + 0.20m,
            null,
            1,
            TickFlags.Bid | TickFlags.Ask);
    }

    private static XauMarketState BasicState(
        long sequence,
        DateTimeOffset timestamp,
        decimal bid)
    {
        return StateWithFeatures(sequence, timestamp, bid);
    }

    private static XauMarketState StateWithFeatures(
        long sequence,
        DateTimeOffset timestamp,
        decimal bid,
        params (string Name, double Value)[] features)
    {
        NumericFeatureValue[] values = features
            .Select(
                pair => new NumericFeatureValue(
                    pair.Name,
                    pair.Value,
                    "test",
                    isAvailable: true,
                    timestamp,
                    unavailableReason: null))
            .ToArray();

        Guid id = Guid.ParseExact(
            sequence.ToString("x").PadLeft(32, '0'),
            "N");

        return new XauMarketState(
            ContractVersions.MarketStateV1,
            id,
            timestamp,
            timestamp,
            sequence,
            "XAUUSD",
            "XAUUSD",
            bid,
            bid + 0.20m,
            bid + 0.10m,
            ContractVersions.FeatureSchemaV1,
            "entry-test",
            LiquiditySource.None,
            new DataReadiness(
                requiredP0Ready: true,
                tickHistoryReady: true,
                barHistoryReady: true,
                newsDataAvailable: true,
                missingRequirements: []),
            values);
    }

    private static DateTimeOffset Utc(int hour, int minute, int second)
    {
        return new DateTimeOffset(2026, 9, 19, hour, minute, second, TimeSpan.Zero);
    }
}
