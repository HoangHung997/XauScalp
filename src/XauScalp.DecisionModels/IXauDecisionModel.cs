using XauScalp.Domain;

namespace XauScalp.DecisionModels;

public interface IXauDecisionModel
{
    string ModelId { get; }

    string ModelVersion { get; }

    Task<XauDecision> EvaluateAsync(
        XauMarketState state,
        CancellationToken cancellationToken);
}
