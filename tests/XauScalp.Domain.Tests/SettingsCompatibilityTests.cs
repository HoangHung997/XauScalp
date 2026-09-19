using System.Text.Json;

namespace XauScalp.Domain.Tests;

public sealed class SettingsCompatibilityTests
{
    [Fact]
    public void DecisionModelType_ContainsExactlyTwoModels()
    {
        DecisionModelType[] values = Enum.GetValues<DecisionModelType>();

        Assert.Equal([DecisionModelType.Jev, DecisionModelType.XauNative], values);
    }

    [Fact]
    public void EnabledShadow_CannotEqualPrimary()
    {
        var shadow = new ShadowComparisonSettings(true, DecisionModelType.Jev);

        Assert.Throws<ArgumentException>(
            () => new DecisionModelSettings(DecisionModelType.Jev, JevFailurePolicy.StopNewTrades, shadow));
    }

    [Fact]
    public void EnabledShadow_RequiresModel()
    {
        Assert.Throws<ArgumentException>(() => new ShadowComparisonSettings(true, null));
    }

    [Fact]
    public void UnknownDecisionModelString_FailsClosed()
    {
        var settings = new XauScalpSettings(
            ContractVersions.SettingsV1,
            "settings-001",
            new DecisionModelSettings(
                DecisionModelType.Jev,
                JevFailurePolicy.FallbackToXauNative,
                new ShadowComparisonSettings(true, DecisionModelType.XauNative)),
            ContractTestFactory.CreateRiskSettings());

        JsonSerializerOptions options = XauJson.CreateOptions();
        string json = JsonSerializer.Serialize(settings, options);
        string invalidJson = json.Replace(
            "\"primaryDecisionModel\":\"jev\"",
            "\"primaryDecisionModel\":\"thirdModel\"",
            StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<XauScalpSettings>(invalidJson, options));
    }
}
