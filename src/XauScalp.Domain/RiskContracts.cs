using System.Text.Json.Serialization;

namespace XauScalp.Domain;

public enum RiskDecisionOutcome
{
    Rejected = 0,
    Authorized = 1,
}

public sealed record RiskDecision
{
    [JsonConstructor]
    public RiskDecision(
        string contractVersion,
        Guid riskDecisionId,
        Guid marketStateId,
        Guid decisionId,
        RiskDecisionOutcome outcome,
        string reasonCode,
        string? reason,
        decimal? authorizedVolumeLots,
        decimal? authorizedRiskMoney,
        decimal? protectiveStopPrice,
        string riskPolicyVersion,
        DateTimeOffset evaluatedAtUtc)
    {
        ContractVersion = ContractGuard.ExactVersion(contractVersion, ContractVersions.RiskDecisionV1, nameof(contractVersion));
        RiskDecisionId = ContractGuard.NonEmpty(riskDecisionId, nameof(riskDecisionId));
        MarketStateId = ContractGuard.NonEmpty(marketStateId, nameof(marketStateId));
        DecisionId = ContractGuard.NonEmpty(decisionId, nameof(decisionId));
        Outcome = outcome;
        ReasonCode = ContractGuard.Required(reasonCode, nameof(reasonCode));
        Reason = reason;
        RiskPolicyVersion = ContractGuard.Required(riskPolicyVersion, nameof(riskPolicyVersion));
        EvaluatedAtUtc = ContractGuard.Utc(evaluatedAtUtc, nameof(evaluatedAtUtc));

        if (outcome == RiskDecisionOutcome.Authorized)
        {
            if (authorizedVolumeLots is null || authorizedVolumeLots <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(authorizedVolumeLots), authorizedVolumeLots, "Authorized risk decisions require positive volume.");
            }

            if (authorizedRiskMoney is null || authorizedRiskMoney < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(authorizedRiskMoney), authorizedRiskMoney, "Authorized risk decisions require non-negative risk money.");
            }

            if (protectiveStopPrice is null || protectiveStopPrice <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(protectiveStopPrice), protectiveStopPrice, "Authorized risk decisions require a positive protective stop.");
            }
        }
        else if (authorizedVolumeLots is not null || authorizedRiskMoney is not null || protectiveStopPrice is not null)
        {
            throw new ArgumentException("Rejected risk decisions cannot carry authorization values.");
        }

        AuthorizedVolumeLots = authorizedVolumeLots;
        AuthorizedRiskMoney = authorizedRiskMoney;
        ProtectiveStopPrice = protectiveStopPrice;
    }

    public string ContractVersion { get; }

    public Guid RiskDecisionId { get; }

    public Guid MarketStateId { get; }

    public Guid DecisionId { get; }

    public RiskDecisionOutcome Outcome { get; }

    public string ReasonCode { get; }

    public string? Reason { get; }

    public decimal? AuthorizedVolumeLots { get; }

    public decimal? AuthorizedRiskMoney { get; }

    public decimal? ProtectiveStopPrice { get; }

    public string RiskPolicyVersion { get; }

    public DateTimeOffset EvaluatedAtUtc { get; }
}
