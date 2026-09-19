using System.Text.Json.Serialization;

namespace XauScalp.Domain;

public enum DecisionModelType
{
    Jev = 0,
    XauNative = 1,
}

public enum TradeAction
{
    Wait = 0,
    Long = 1,
    Short = -1,
}

public sealed record XauDecision
{
    [JsonConstructor]
    public XauDecision(
        string contractVersion,
        Guid decisionId,
        Guid marketStateId,
        DecisionModelType modelType,
        TradeAction action,
        double actionProbability,
        double pUp5First,
        double pDown5First,
        double pUp10First,
        double pDown10First,
        double pAdverseBarrierFirst,
        double pContinuation,
        double pReversal,
        double pFalseBreak,
        double confidence,
        double? pHold,
        double? pExitNow,
        double? pTp5FromHere,
        double? pTp10FromHere,
        string modelId,
        string modelVersion,
        string featureSchemaVersion,
        DateTimeOffset evaluatedAtUtc,
        TimeSpan evaluationLatency)
    {
        ContractVersion = ContractGuard.ExactVersion(contractVersion, ContractVersions.DecisionV1, nameof(contractVersion));
        DecisionId = ContractGuard.NonEmpty(decisionId, nameof(decisionId));
        MarketStateId = ContractGuard.NonEmpty(marketStateId, nameof(marketStateId));
        ModelType = modelType;
        Action = action;
        ActionProbability = ContractGuard.Probability(actionProbability, nameof(actionProbability));
        PUp5First = ContractGuard.Probability(pUp5First, nameof(pUp5First));
        PDown5First = ContractGuard.Probability(pDown5First, nameof(pDown5First));
        PUp10First = ContractGuard.Probability(pUp10First, nameof(pUp10First));
        PDown10First = ContractGuard.Probability(pDown10First, nameof(pDown10First));
        PAdverseBarrierFirst = ContractGuard.Probability(pAdverseBarrierFirst, nameof(pAdverseBarrierFirst));
        PContinuation = ContractGuard.Probability(pContinuation, nameof(pContinuation));
        PReversal = ContractGuard.Probability(pReversal, nameof(pReversal));
        PFalseBreak = ContractGuard.Probability(pFalseBreak, nameof(pFalseBreak));
        Confidence = ContractGuard.Probability(confidence, nameof(confidence));
        PHold = ContractGuard.OptionalProbability(pHold, nameof(pHold));
        PExitNow = ContractGuard.OptionalProbability(pExitNow, nameof(pExitNow));
        PTp5FromHere = ContractGuard.OptionalProbability(pTp5FromHere, nameof(pTp5FromHere));
        PTp10FromHere = ContractGuard.OptionalProbability(pTp10FromHere, nameof(pTp10FromHere));
        ModelId = ContractGuard.Required(modelId, nameof(modelId));
        ModelVersion = ContractGuard.Required(modelVersion, nameof(modelVersion));
        FeatureSchemaVersion = ContractGuard.Required(featureSchemaVersion, nameof(featureSchemaVersion));
        EvaluatedAtUtc = ContractGuard.Utc(evaluatedAtUtc, nameof(evaluatedAtUtc));

        if (evaluationLatency < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(evaluationLatency), evaluationLatency, "Evaluation latency must be non-negative.");
        }

        EvaluationLatency = evaluationLatency;
    }

    public string ContractVersion { get; }

    public Guid DecisionId { get; }

    public Guid MarketStateId { get; }

    public DecisionModelType ModelType { get; }

    public TradeAction Action { get; }

    public double ActionProbability { get; }

    public double PUp5First { get; }

    public double PDown5First { get; }

    public double PUp10First { get; }

    public double PDown10First { get; }

    public double PAdverseBarrierFirst { get; }

    public double PContinuation { get; }

    public double PReversal { get; }

    public double PFalseBreak { get; }

    public double Confidence { get; }

    public double? PHold { get; }

    public double? PExitNow { get; }

    public double? PTp5FromHere { get; }

    public double? PTp10FromHere { get; }

    public string ModelId { get; }

    public string ModelVersion { get; }

    public string FeatureSchemaVersion { get; }

    public DateTimeOffset EvaluatedAtUtc { get; }

    public TimeSpan EvaluationLatency { get; }
}
