using System.Text.Json.Serialization;

namespace XauScalp.Domain;

public enum JevFailurePolicy
{
    StopNewTrades = 0,
    FallbackToXauNative = 1,
}

public sealed record ShadowComparisonSettings
{
    [JsonConstructor]
    public ShadowComparisonSettings(bool enabled, DecisionModelType? shadowModel)
    {
        if (enabled && shadowModel is null)
        {
            throw new ArgumentException("Enabled shadow comparison requires a shadow model.", nameof(shadowModel));
        }

        Enabled = enabled;
        ShadowModel = shadowModel;
    }

    public bool Enabled { get; }

    public DecisionModelType? ShadowModel { get; }
}

public sealed record DecisionModelSettings
{
    [JsonConstructor]
    public DecisionModelSettings(
        DecisionModelType primaryDecisionModel,
        JevFailurePolicy jevFailurePolicy,
        ShadowComparisonSettings shadowComparison)
    {
        PrimaryDecisionModel = primaryDecisionModel;
        JevFailurePolicy = jevFailurePolicy;
        ShadowComparison = shadowComparison ?? throw new ArgumentNullException(nameof(shadowComparison));

        if (shadowComparison.Enabled && shadowComparison.ShadowModel == primaryDecisionModel)
        {
            throw new ArgumentException("Primary and shadow decision models must be different.", nameof(shadowComparison));
        }
    }

    public DecisionModelType PrimaryDecisionModel { get; }

    public JevFailurePolicy JevFailurePolicy { get; }

    public ShadowComparisonSettings ShadowComparison { get; }
}

public sealed record RiskSettings
{
    [JsonConstructor]
    public RiskSettings(
        double maxRiskPerTradePct,
        double maxDailyLossPct,
        decimal? maxDailyLossMoney,
        int maxTradesPerDay,
        int maxConcurrentPositions,
        decimal maxSpreadPrice,
        double maxSpreadAtrRatio,
        int maxSlippagePoints,
        int maxDecisionAgeMs,
        int maxFeatureAgeMs,
        double minFreeMarginPct,
        int cooldownAfterLossSec,
        int cooldownAfterExecutionFailureSec,
        int highImpactNewsBlockBeforeSec,
        int highImpactNewsBlockAfterSec,
        bool allowLong,
        bool allowShort)
    {
        MaxRiskPerTradePct = ContractGuard.Percentage(maxRiskPerTradePct, nameof(maxRiskPerTradePct));
        MaxDailyLossPct = ContractGuard.Percentage(maxDailyLossPct, nameof(maxDailyLossPct));
        if (maxDailyLossMoney is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDailyLossMoney), maxDailyLossMoney, "Max daily loss money must be positive when supplied.");
        }

        MaxDailyLossMoney = maxDailyLossMoney;
        MaxTradesPerDay = ContractGuard.Positive(maxTradesPerDay, nameof(maxTradesPerDay));
        MaxConcurrentPositions = ContractGuard.Positive(maxConcurrentPositions, nameof(maxConcurrentPositions));
        MaxSpreadPrice = ContractGuard.Positive(maxSpreadPrice, nameof(maxSpreadPrice));
        MaxSpreadAtrRatio = ContractGuard.NonNegative(maxSpreadAtrRatio, nameof(maxSpreadAtrRatio));
        MaxSlippagePoints = ContractGuard.NonNegative(maxSlippagePoints, nameof(maxSlippagePoints));
        MaxDecisionAgeMs = ContractGuard.Positive(maxDecisionAgeMs, nameof(maxDecisionAgeMs));
        MaxFeatureAgeMs = ContractGuard.Positive(maxFeatureAgeMs, nameof(maxFeatureAgeMs));
        MinFreeMarginPct = ContractGuard.Percentage(minFreeMarginPct, nameof(minFreeMarginPct), allowZero: true);
        CooldownAfterLossSec = ContractGuard.NonNegative(cooldownAfterLossSec, nameof(cooldownAfterLossSec));
        CooldownAfterExecutionFailureSec = ContractGuard.NonNegative(cooldownAfterExecutionFailureSec, nameof(cooldownAfterExecutionFailureSec));
        HighImpactNewsBlockBeforeSec = ContractGuard.NonNegative(highImpactNewsBlockBeforeSec, nameof(highImpactNewsBlockBeforeSec));
        HighImpactNewsBlockAfterSec = ContractGuard.NonNegative(highImpactNewsBlockAfterSec, nameof(highImpactNewsBlockAfterSec));
        AllowLong = allowLong;
        AllowShort = allowShort;
    }

    public double MaxRiskPerTradePct { get; }

    public double MaxDailyLossPct { get; }

    public decimal? MaxDailyLossMoney { get; }

    public int MaxTradesPerDay { get; }

    public int MaxConcurrentPositions { get; }

    public decimal MaxSpreadPrice { get; }

    public double MaxSpreadAtrRatio { get; }

    public int MaxSlippagePoints { get; }

    public int MaxDecisionAgeMs { get; }

    public int MaxFeatureAgeMs { get; }

    public double MinFreeMarginPct { get; }

    public int CooldownAfterLossSec { get; }

    public int CooldownAfterExecutionFailureSec { get; }

    public int HighImpactNewsBlockBeforeSec { get; }

    public int HighImpactNewsBlockAfterSec { get; }

    public bool AllowLong { get; }

    public bool AllowShort { get; }
}

public sealed record XauScalpSettings
{
    [JsonConstructor]
    public XauScalpSettings(
        string contractVersion,
        string settingsVersion,
        DecisionModelSettings decisionModels,
        RiskSettings risk)
    {
        ContractVersion = ContractGuard.ExactVersion(contractVersion, ContractVersions.SettingsV1, nameof(contractVersion));
        SettingsVersion = ContractGuard.Required(settingsVersion, nameof(settingsVersion));
        DecisionModels = decisionModels ?? throw new ArgumentNullException(nameof(decisionModels));
        Risk = risk ?? throw new ArgumentNullException(nameof(risk));
    }

    public string ContractVersion { get; }

    public string SettingsVersion { get; }

    public DecisionModelSettings DecisionModels { get; }

    public RiskSettings Risk { get; }
}
