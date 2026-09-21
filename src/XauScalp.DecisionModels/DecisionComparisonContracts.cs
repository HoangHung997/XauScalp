using XauScalp.Domain;

namespace XauScalp.DecisionModels;

public enum DecisionAuthorityRole
{
    Primary = 0,
    Shadow = 1,
}

public sealed record ModelDecisionRecord(
    DecisionAuthorityRole Role,
    DecisionModelType ConfiguredModel,
    DecisionModelType? ActualModel,
    XauDecision? Decision,
    bool Succeeded,
    bool IsAuthoritative,
    bool IsFallback,
    string? FailureCode,
    TimeSpan EvaluationLatency);

public sealed record DecisionComparisonRecord(
    Guid ComparisonId,
    Guid MarketStateId,
    DateTimeOffset MarketStateTimestampUtc,
    DecisionModelType ConfiguredPrimaryModel,
    bool ShadowEnabled,
    ModelDecisionRecord Primary,
    ModelDecisionRecord? Shadow,
    DateTimeOffset RecordedAtUtc);

public sealed record DecisionFutureLabels(
    Guid MarketStateId,
    string LabelRunId,
    TimeSpan Horizon,
    bool Censored,
    bool? Up5First,
    bool? Down5First,
    bool? Up10First,
    bool? Down10First);

public sealed record ExecutedTradeOutcome(
    Guid DecisionId,
    decimal NetPnlPrice,
    DateTimeOffset ClosedAtUtc);

public sealed record DecisionComparisonBundle(
    DecisionComparisonRecord Comparison,
    DecisionFutureLabels? FutureLabels,
    ExecutedTradeOutcome? ExecutionOutcome);

public interface IDecisionComparisonStore
{
    ValueTask AppendComparisonAsync(
        DecisionComparisonRecord comparison,
        CancellationToken cancellationToken);

    ValueTask AttachFutureLabelsAsync(
        Guid comparisonId,
        DecisionFutureLabels labels,
        CancellationToken cancellationToken);

    ValueTask AttachExecutionOutcomeAsync(
        Guid comparisonId,
        ExecutedTradeOutcome outcome,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DecisionComparisonBundle>> ReadAllAsync(
        CancellationToken cancellationToken);
}

public sealed class InMemoryDecisionComparisonStore : IDecisionComparisonStore
{
    private readonly object _sync = new();
    private readonly List<Guid> _order = [];
    private readonly Dictionary<Guid, DecisionComparisonBundle> _bundles = [];

    public ValueTask AppendComparisonAsync(
        DecisionComparisonRecord comparison,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(comparison);

        lock (_sync)
        {
            if (_bundles.ContainsKey(comparison.ComparisonId))
            {
                throw new InvalidOperationException(
                    $"Comparison {comparison.ComparisonId} already exists.");
            }

            _order.Add(comparison.ComparisonId);
            _bundles.Add(
                comparison.ComparisonId,
                new DecisionComparisonBundle(
                    comparison,
                    FutureLabels: null,
                    ExecutionOutcome: null));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask AttachFutureLabelsAsync(
        Guid comparisonId,
        DecisionFutureLabels labels,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(labels);

        lock (_sync)
        {
            DecisionComparisonBundle existing = Get(comparisonId);
            if (existing.Comparison.MarketStateId != labels.MarketStateId)
            {
                throw new InvalidDataException(
                    "Future labels reference a different MarketStateId.");
            }

            _bundles[comparisonId] = existing with { FutureLabels = labels };
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask AttachExecutionOutcomeAsync(
        Guid comparisonId,
        ExecutedTradeOutcome outcome,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(outcome);

        lock (_sync)
        {
            DecisionComparisonBundle existing = Get(comparisonId);
            Guid? authoritativeDecisionId = existing.Comparison.Primary.Decision?.DecisionId;

            if (authoritativeDecisionId is null
                || authoritativeDecisionId != outcome.DecisionId
                || !existing.Comparison.Primary.IsAuthoritative)
            {
                throw new InvalidDataException(
                    "Execution outcome must reference the authoritative primary decision.");
            }

            _bundles[comparisonId] = existing with { ExecutionOutcome = outcome };
        }

        return ValueTask.CompletedTask;
    }

    public Task<IReadOnlyList<DecisionComparisonBundle>> ReadAllAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            IReadOnlyList<DecisionComparisonBundle> snapshot = _order
                .Select(id => _bundles[id])
                .ToArray();

            return Task.FromResult(snapshot);
        }
    }

    private DecisionComparisonBundle Get(Guid comparisonId)
    {
        if (!_bundles.TryGetValue(comparisonId, out DecisionComparisonBundle? bundle))
        {
            throw new KeyNotFoundException(
                $"Comparison {comparisonId} was not found.");
        }

        return bundle;
    }
}

public sealed record AuthoritativeTradeDecisionEnvelope(
    Guid ComparisonId,
    XauDecision Decision,
    bool IsFallback);

public interface IAuthoritativeTradeDecisionSink
{
    ValueTask AcceptAsync(
        AuthoritativeTradeDecisionEnvelope envelope,
        CancellationToken cancellationToken);
}

public sealed class NullAuthoritativeTradeDecisionSink : IAuthoritativeTradeDecisionSink
{
    public static NullAuthoritativeTradeDecisionSink Instance { get; } = new();

    private NullAuthoritativeTradeDecisionSink()
    {
    }

    public ValueTask AcceptAsync(
        AuthoritativeTradeDecisionEnvelope envelope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(envelope);
        return ValueTask.CompletedTask;
    }
}

public sealed record PrimaryShadowEvaluationResult(
    Guid ComparisonId,
    XauDecision? AuthoritativeDecision,
    DecisionModelType? AuthoritativeModel,
    bool StopNewTrades,
    bool UsedFallback);
