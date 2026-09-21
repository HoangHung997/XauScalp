using System.Reflection;
using XauScalp.App.Core;
using XauScalp.Domain;

namespace XauScalp.IntegrationTests;

public sealed class DesktopUiContractTests
{
    [Fact]
    public void SettingsExposeExactlyJevAndXauNative()
    {
        var viewModel = new DesktopSettingsViewModel();

        Assert.Collection(
            viewModel.DecisionModelOptions,
            option =>
            {
                Assert.Equal(DecisionModelType.Jev, option.Type);
                Assert.Equal("JEV", option.DisplayName);
            },
            option =>
            {
                Assert.Equal(DecisionModelType.XauNative, option.Type);
                Assert.Equal("XAU Native AI", option.DisplayName);
            });
    }

    [Fact]
    public void SettingsBuildExistingDomainContractsWithoutRiskBypass()
    {
        var viewModel = new DesktopSettingsViewModel
        {
            PrimaryDecisionModel = new DecisionModelChoice(
                DecisionModelType.XauNative,
                "XAU Native AI"),
            ShadowComparisonEnabled = true,
            ShadowDecisionModel = new DecisionModelChoice(
                DecisionModelType.Jev,
                "JEV"),
            JevFailurePolicy = JevFailurePolicy.StopNewTrades,
            MaxRiskPerTradePct = 0.25,
            MaxDailyLossPct = 1.5,
            MaxConcurrentPositions = 1,
            AllowLong = true,
            AllowShort = false,
        };

        XauScalpSettings settings = viewModel.BuildDomainSettings();

        Assert.Equal(
            DecisionModelType.XauNative,
            settings.DecisionModels.PrimaryDecisionModel);
        Assert.True(settings.DecisionModels.ShadowComparison.Enabled);
        Assert.Equal(
            DecisionModelType.Jev,
            settings.DecisionModels.ShadowComparison.ShadowModel);
        Assert.Equal(
            JevFailurePolicy.StopNewTrades,
            settings.DecisionModels.JevFailurePolicy);
        Assert.Equal(0.25, settings.Risk.MaxRiskPerTradePct);
        Assert.Equal(1.5, settings.Risk.MaxDailyLossPct);
        Assert.Equal(1, settings.Risk.MaxConcurrentPositions);
        Assert.True(settings.Risk.AllowLong);
        Assert.False(settings.Risk.AllowShort);
    }

    [Fact]
    public void SamePrimaryAndShadowModelFailsClosed()
    {
        var viewModel = new DesktopSettingsViewModel
        {
            PrimaryDecisionModel = new DecisionModelChoice(
                DecisionModelType.Jev,
                "JEV"),
            ShadowComparisonEnabled = true,
            ShadowDecisionModel = new DecisionModelChoice(
                DecisionModelType.Jev,
                "JEV"),
        };

        Assert.Throws<ArgumentException>(
            () => viewModel.BuildDomainSettings());
    }

    [Fact]
    public void LiveMoneyAuthorizationIsReadOnlyAndDisabled()
    {
        var viewModel = new DesktopSettingsViewModel();

        Assert.False(viewModel.LiveTradingAuthorized);
        Assert.Contains(
            "DISABLED",
            viewModel.LiveTradingStatus,
            StringComparison.OrdinalIgnoreCase);

        PropertyInfo property = Assert.Single(
            typeof(DesktopSettingsViewModel)
                .GetProperties()
                .Where(
                    property => string.Equals(
                        property.Name,
                        nameof(DesktopSettingsViewModel.LiveTradingAuthorized),
                        StringComparison.Ordinal)));

        Assert.False(property.CanWrite);
    }

    [Fact]
    public void UiStoresOnlyASecretReferenceNotAnApiKeyOrPasswordField()
    {
        var viewModel = new DesktopSettingsViewModel();

        Assert.StartsWith(
            "env:",
            viewModel.JevSecretReference,
            StringComparison.OrdinalIgnoreCase);

        PropertyInfo[] properties = typeof(DesktopSettingsViewModel)
            .GetProperties();

        Assert.DoesNotContain(
            properties,
            property => property.Name.Contains(
                "ApiKey",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            properties,
            property => property.Name.Contains(
                "Password",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OperationalStatusRaisesPropertyChangedForRuntimeRefresh()
    {
        var status = new OperationalStatusViewModel();
        var changed = new List<string>();

        status.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                changed.Add(args.PropertyName);
            }
        };

        status.Apply(
            new OperationalStatusSnapshot(
                FeedHealth: "Healthy",
                FeatureReadiness: "Ready",
                SelectedModel: "XAU Native AI",
                ModelVersion: "native-v0",
                ModelLatency: "3 ms",
                ModelError: "None",
                RiskLockState: "Unlocked",
                BrokerConnection: "Connected",
                CurrentPosition: "None",
                DataStaleness: "12 ms"));

        Assert.Contains(nameof(OperationalStatusViewModel.FeedHealth), changed);
        Assert.Contains(nameof(OperationalStatusViewModel.FeatureReadiness), changed);
        Assert.Contains(nameof(OperationalStatusViewModel.SelectedModel), changed);
        Assert.Contains(nameof(OperationalStatusViewModel.BrokerConnection), changed);
        Assert.Contains(nameof(OperationalStatusViewModel.DataStaleness), changed);
    }

    [Fact]
    public void OperationalStatusExposesRequiredRuntimeHealthFields()
    {
        var status = new OperationalStatusViewModel();
        status.Apply(
            new OperationalStatusSnapshot(
                FeedHealth: "Healthy",
                FeatureReadiness: "Ready",
                SelectedModel: "XAU Native AI",
                ModelVersion: "native-v0",
                ModelLatency: "3 ms",
                ModelError: "None",
                RiskLockState: "Unlocked",
                BrokerConnection: "Connected",
                CurrentPosition: "None",
                DataStaleness: "12 ms"));

        Assert.Equal("Healthy", status.FeedHealth);
        Assert.Equal("Ready", status.FeatureReadiness);
        Assert.Equal("XAU Native AI", status.SelectedModel);
        Assert.Equal("native-v0", status.ModelVersion);
        Assert.Equal("3 ms", status.ModelLatency);
        Assert.Equal("None", status.ModelError);
        Assert.Equal("Unlocked", status.RiskLockState);
        Assert.Equal("Connected", status.BrokerConnection);
        Assert.Equal("None", status.CurrentPosition);
        Assert.Equal("12 ms", status.DataStaleness);
    }
}
