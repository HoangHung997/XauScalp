using System.Security.Cryptography;
using System.Text;
using XauScalp.Domain;

namespace XauScalp.Execution;

public sealed record TradePlannerConfiguration(
    string SettingsVersion,
    decimal? TakeProfitDistancePrice = null)
{
    public string SettingsVersion { get; } = string.IsNullOrWhiteSpace(SettingsVersion)
        ? throw new ArgumentException(
            "Settings version is required.",
            nameof(SettingsVersion))
        : SettingsVersion;

    public decimal? TakeProfitDistancePrice { get; } =
        TakeProfitDistancePrice is null or > 0
            ? TakeProfitDistancePrice
            : throw new ArgumentOutOfRangeException(
                nameof(TakeProfitDistancePrice),
                TakeProfitDistancePrice,
                "Take-profit distance must be positive when supplied.");
}

public interface ITradePlanner
{
    TradePlan Create(
        XauMarketState state,
        XauDecision decision,
        RiskDecision riskDecision);
}

public sealed class TradePlanner : ITradePlanner
{
    private const string PlannerVersion = "trade-planner-v1";

    private readonly TradePlannerConfiguration _configuration;

    public TradePlanner(TradePlannerConfiguration configuration)
    {
        _configuration = configuration
            ?? throw new ArgumentNullException(nameof(configuration));
    }

    public TradePlan Create(
        XauMarketState state,
        XauDecision decision,
        RiskDecision riskDecision)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(riskDecision);

        ValidateLinkage(state, decision, riskDecision);

        if (riskDecision.Outcome != RiskDecisionOutcome.Authorized)
        {
            throw new InvalidOperationException(
                "TradePlan can only be created from an authorized hard-risk decision.");
        }

        if (decision.Action is not TradeAction.Long and not TradeAction.Short)
        {
            throw new InvalidOperationException(
                "TradePlan requires an actionable Long or Short decision.");
        }

        decimal volume = riskDecision.AuthorizedVolumeLots
            ?? throw new InvalidDataException(
                "Authorized risk decision is missing volume.");

        decimal stop = riskDecision.ProtectiveStopPrice
            ?? throw new InvalidDataException(
                "Authorized risk decision is missing protective stop.");

        TradeSide side = decision.Action == TradeAction.Long
            ? TradeSide.Long
            : TradeSide.Short;

        decimal entry = side == TradeSide.Long
            ? state.Ask
            : state.Bid;

        ValidateProtection(side, entry, stop);

        decimal? takeProfit = _configuration.TakeProfitDistancePrice is decimal distance
            ? side == TradeSide.Long
                ? entry + distance
                : entry - distance
            : null;

        if (takeProfit is <= 0)
        {
            throw new InvalidOperationException(
                "Configured take-profit distance produces a non-positive price.");
        }

        return new TradePlan(
            ContractVersions.TradePlanV1,
            DeterministicTradeIntentId(
                state.MarketStateId,
                decision.DecisionId,
                riskDecision.RiskDecisionId,
                riskDecision.RiskPolicyVersion,
                _configuration.SettingsVersion),
            state.MarketStateId,
            decision.DecisionId,
            riskDecision.RiskDecisionId,
            state.Symbol,
            state.BrokerSymbol,
            side,
            volume,
            entry,
            stop,
            takeProfit,
            riskDecision.RiskPolicyVersion,
            _configuration.SettingsVersion,
            riskDecision.EvaluatedAtUtc);
    }

    private static void ValidateLinkage(
        XauMarketState state,
        XauDecision decision,
        RiskDecision riskDecision)
    {
        if (decision.MarketStateId != state.MarketStateId
            || riskDecision.MarketStateId != state.MarketStateId
            || riskDecision.DecisionId != decision.DecisionId)
        {
            throw new InvalidDataException(
                "Market state, model decision, and hard-risk authorization are not linked to the same decision path.");
        }

        if (!string.Equals(
                decision.FeatureSchemaVersion,
                state.FeatureSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Decision feature schema does not match market state.");
        }
    }

    private static void ValidateProtection(
        TradeSide side,
        decimal entry,
        decimal stop)
    {
        bool valid = side switch
        {
            TradeSide.Long => stop < entry,
            TradeSide.Short => stop > entry,
            _ => false,
        };

        if (!valid)
        {
            throw new InvalidDataException(
                "Protective stop is on the unsafe side of the planned entry.");
        }
    }

    private static Guid DeterministicTradeIntentId(
        Guid marketStateId,
        Guid decisionId,
        Guid riskDecisionId,
        string riskPolicyVersion,
        string settingsVersion)
    {
        string identity = string.Join(
            "|",
            PlannerVersion,
            marketStateId.ToString("D"),
            decisionId.ToString("D"),
            riskDecisionId.ToString("D"),
            riskPolicyVersion,
            settingsVersion);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new Guid(hash.AsSpan(0, 16));
    }
}
