namespace XauScalp.Domain.Tests;

internal static class ContractTestFactory
{
    public static readonly DateTimeOffset UtcNow = new(2026, 9, 19, 16, 0, 0, TimeSpan.Zero);

    public static XauDecision CreateDecision(double actionProbability = 0.70, double? pHold = null)
    {
        return new XauDecision(
            ContractVersions.DecisionV1,
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            DecisionModelType.XauNative,
            TradeAction.Long,
            actionProbability,
            0.70,
            0.30,
            0.55,
            0.45,
            0.20,
            0.68,
            0.32,
            0.15,
            0.72,
            pHold,
            null,
            null,
            null,
            "xau-native",
            "0.1.0",
            ContractVersions.FeatureSchemaV1,
            UtcNow,
            TimeSpan.FromMilliseconds(4));
    }

    public static RiskSettings CreateRiskSettings()
    {
        return new RiskSettings(
            maxRiskPerTradePct: 0.5,
            maxDailyLossPct: 2.0,
            maxDailyLossMoney: 500m,
            maxTradesPerDay: 20,
            maxConcurrentPositions: 1,
            maxSpreadPrice: 0.8m,
            maxSpreadAtrRatio: 0.25,
            maxSlippagePoints: 30,
            maxDecisionAgeMs: 1000,
            maxFeatureAgeMs: 1000,
            minFreeMarginPct: 20,
            cooldownAfterLossSec: 60,
            cooldownAfterExecutionFailureSec: 30,
            highImpactNewsBlockBeforeSec: 900,
            highImpactNewsBlockAfterSec: 900,
            allowLong: true,
            allowShort: true);
    }
}
