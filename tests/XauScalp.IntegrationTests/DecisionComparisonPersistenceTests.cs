using XauScalp.DecisionModels;
using XauScalp.Domain;
using XauScalp.Persistence;

namespace XauScalp.IntegrationTests;

public sealed class DecisionComparisonPersistenceTests
{
    [Fact]
    public async Task JsonlStorePersistsPrimaryShadowLabelsAndExecutedOutcome()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "xauscalp-tests",
            Guid.NewGuid().ToString("N"),
            "decision-comparison.jsonl");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            Guid comparisonId = Guid.NewGuid();
            Guid marketStateId = Guid.NewGuid();
            XauDecision primaryDecision = Decision(
                marketStateId,
                DecisionModelType.Jev,
                TradeAction.Long,
                "jev");
            XauDecision shadowDecision = Decision(
                marketStateId,
                DecisionModelType.XauNative,
                TradeAction.Short,
                "xau-native");

            var comparison = new DecisionComparisonRecord(
                comparisonId,
                marketStateId,
                primaryDecision.EvaluatedAtUtc,
                DecisionModelType.Jev,
                ShadowEnabled: true,
                new ModelDecisionRecord(
                    DecisionAuthorityRole.Primary,
                    DecisionModelType.Jev,
                    DecisionModelType.Jev,
                    primaryDecision,
                    Succeeded: true,
                    IsAuthoritative: true,
                    IsFallback: false,
                    FailureCode: null,
                    TimeSpan.FromMilliseconds(10)),
                new ModelDecisionRecord(
                    DecisionAuthorityRole.Shadow,
                    DecisionModelType.XauNative,
                    DecisionModelType.XauNative,
                    shadowDecision,
                    Succeeded: true,
                    IsAuthoritative: false,
                    IsFallback: false,
                    FailureCode: null,
                    TimeSpan.FromMilliseconds(2)),
                primaryDecision.EvaluatedAtUtc);

            await using (var store = new DecisionComparisonJsonlStore(path))
            {
                await store.AppendComparisonAsync(
                    comparison,
                    CancellationToken.None);

                await store.AttachFutureLabelsAsync(
                    comparisonId,
                    new DecisionFutureLabels(
                        marketStateId,
                        "labels-run-1",
                        TimeSpan.FromMinutes(5),
                        Censored: false,
                        Up5First: true,
                        Down5First: false,
                        Up10First: true,
                        Down10First: false),
                    CancellationToken.None);

                await store.AttachExecutionOutcomeAsync(
                    comparisonId,
                    new ExecutedTradeOutcome(
                        primaryDecision.DecisionId,
                        2.5m,
                        primaryDecision.EvaluatedAtUtc.AddMinutes(1)),
                    CancellationToken.None);

                DecisionComparisonBundle bundle = Assert.Single(
                    await store.ReadAllAsync(CancellationToken.None));

                Assert.Equal(primaryDecision.DecisionId, bundle.Comparison.Primary.Decision!.DecisionId);
                Assert.Equal(shadowDecision.DecisionId, bundle.Comparison.Shadow!.Decision!.DecisionId);
                Assert.True(bundle.FutureLabels!.Up5First);
                Assert.Equal(2.5m, bundle.ExecutionOutcome!.NetPnlPrice);
            }

            await using var reopened = new DecisionComparisonJsonlStore(path);
            DecisionComparisonBundle restored = Assert.Single(
                await reopened.ReadAllAsync(CancellationToken.None));

            Assert.Equal(comparisonId, restored.Comparison.ComparisonId);
            Assert.Equal("labels-run-1", restored.FutureLabels!.LabelRunId);
            Assert.Equal(
                primaryDecision.DecisionId,
                restored.ExecutionOutcome!.DecisionId);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            string? directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static XauDecision Decision(
        Guid marketStateId,
        DecisionModelType modelType,
        TradeAction action,
        string modelId)
    {
        DateTimeOffset now = new(
            2026,
            9,
            21,
            0,
            0,
            0,
            TimeSpan.Zero);

        return new XauDecision(
            ContractVersions.DecisionV1,
            Guid.NewGuid(),
            marketStateId,
            modelType,
            action,
            0.7,
            0.7,
            0.3,
            0.6,
            0.4,
            0.2,
            0.6,
            0.4,
            0.1,
            0.7,
            null,
            null,
            null,
            null,
            modelId,
            "test-v1",
            ContractVersions.FeatureSchemaV1,
            now,
            TimeSpan.Zero);
    }
}
