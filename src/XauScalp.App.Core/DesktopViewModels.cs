using System.ComponentModel;
using System.Runtime.CompilerServices;
using XauScalp.Domain;

namespace XauScalp.App.Core;

public enum NativeDevicePreference
{
    Auto = 0,
    Cpu = 1,
    Gpu = 2,
}

public sealed record DecisionModelChoice(
    DecisionModelType Type,
    string DisplayName);

public sealed class DesktopSettingsViewModel
{
    public DesktopSettingsViewModel()
    {
        DecisionModelOptions =
        [
            new DecisionModelChoice(DecisionModelType.Jev, "JEV"),
            new DecisionModelChoice(DecisionModelType.XauNative, "XAU Native AI"),
        ];

        JevFailurePolicies = Enum.GetValues<JevFailurePolicy>();
        NativeDeviceOptions = Enum.GetValues<NativeDevicePreference>();
        PrimaryDecisionModel = DecisionModelOptions[0];
        ShadowDecisionModel = DecisionModelOptions[1];
    }

    public IReadOnlyList<DecisionModelChoice> DecisionModelOptions { get; }

    public IReadOnlyList<JevFailurePolicy> JevFailurePolicies { get; }

    public IReadOnlyList<NativeDevicePreference> NativeDeviceOptions { get; }

    public DecisionModelChoice PrimaryDecisionModel { get; set; }

    public bool ShadowComparisonEnabled { get; set; }

    public DecisionModelChoice ShadowDecisionModel { get; set; }

    public string JevSecretReference { get; set; } = "env:XAUSCALP_JEV_API_KEY";

    public string JevPinnedModelVersion { get; set; } = "configure-me";

    public int JevTimeoutMs { get; set; } = 1_000;

    public JevFailurePolicy JevFailurePolicy { get; set; } = JevFailurePolicy.StopNewTrades;

    public string XauNativeArtifactPath { get; set; } = string.Empty;

    public string XauNativeModelVersion { get; set; } = string.Empty;

    public NativeDevicePreference XauNativeDevice { get; set; } = NativeDevicePreference.Auto;

    public bool XauNativeReady { get; set; }

    public string BrokerSymbol { get; set; } = "XAUUSD";

    public string DataSourceId { get; set; } = "mt5";

    public string SettingsVersion { get; set; } = "desktop-settings-v1";

    public double MaxRiskPerTradePct { get; set; } = 0.50;

    public double MaxDailyLossPct { get; set; } = 2.00;

    public decimal? MaxDailyLossMoney { get; set; } = 500m;

    public int MaxTradesPerDay { get; set; } = 20;

    public int MaxConcurrentPositions { get; set; } = 1;

    public decimal MaxSpreadPrice { get; set; } = 0.80m;

    public double MaxSpreadAtrRatio { get; set; } = 0.25;

    public int MaxSlippagePoints { get; set; } = 30;

    public int MaxDecisionAgeMs { get; set; } = 1_000;

    public int MaxFeatureAgeMs { get; set; } = 1_000;

    public double MinFreeMarginPct { get; set; } = 20;

    public int CooldownAfterLossSec { get; set; } = 60;

    public int CooldownAfterExecutionFailureSec { get; set; } = 30;

    public int HighImpactNewsBlockBeforeSec { get; set; } = 900;

    public int HighImpactNewsBlockAfterSec { get; set; } = 900;

    public bool AllowLong { get; set; } = true;

    public bool AllowShort { get; set; } = true;

    public bool LiveTradingAuthorized => false;

    public string OperatingModeStatus =>
        "Research / Demo only";

    public string LiveTradingStatus =>
        "LIVE MONEY DISABLED — requires separate explicit Product Owner authorization.";

    public XauScalpSettings BuildDomainSettings()
    {
        ArgumentNullException.ThrowIfNull(PrimaryDecisionModel);
        ArgumentNullException.ThrowIfNull(ShadowDecisionModel);

        if (DecisionModelOptions.Count != 2
            || DecisionModelOptions.Select(static option => option.Type).Distinct().Count() != 2)
        {
            throw new InvalidOperationException("Desktop settings must expose exactly JEV and XAU Native AI.");
        }

        if (JevTimeoutMs <= 0)
        {
            throw new InvalidOperationException("JEV timeout must be positive.");
        }

        if (string.IsNullOrWhiteSpace(JevSecretReference))
        {
            throw new InvalidOperationException("JEV secret reference is required. Store only a reference, never the secret value.");
        }

        DecisionModelType? shadowModel = ShadowComparisonEnabled
            ? ShadowDecisionModel.Type
            : null;

        var decisionModels = new DecisionModelSettings(
            PrimaryDecisionModel.Type,
            JevFailurePolicy,
            new ShadowComparisonSettings(
                ShadowComparisonEnabled,
                shadowModel));

        var risk = new RiskSettings(
            MaxRiskPerTradePct,
            MaxDailyLossPct,
            MaxDailyLossMoney,
            MaxTradesPerDay,
            MaxConcurrentPositions,
            MaxSpreadPrice,
            MaxSpreadAtrRatio,
            MaxSlippagePoints,
            MaxDecisionAgeMs,
            MaxFeatureAgeMs,
            MinFreeMarginPct,
            CooldownAfterLossSec,
            CooldownAfterExecutionFailureSec,
            HighImpactNewsBlockBeforeSec,
            HighImpactNewsBlockAfterSec,
            AllowLong,
            AllowShort);

        return new XauScalpSettings(
            ContractVersions.SettingsV1,
            SettingsVersion,
            decisionModels,
            risk);
    }
}

public sealed class OperationalStatusViewModel : INotifyPropertyChanged
{
    private string _feedHealth = "Unknown";
    private string _featureReadiness = "Not ready";
    private string _selectedModel = "JEV";
    private string _modelVersion = "Unknown";
    private string _modelLatency = "n/a";
    private string _modelError = "None";
    private string _riskLockState = "Locked / not evaluated";
    private string _brokerConnection = "Disconnected";
    private string _currentPosition = "None";
    private string _dataStaleness = "Unknown";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FeedHealth
    {
        get => _feedHealth;
        private set => SetField(ref _feedHealth, value);
    }

    public string FeatureReadiness
    {
        get => _featureReadiness;
        private set => SetField(ref _featureReadiness, value);
    }

    public string SelectedModel
    {
        get => _selectedModel;
        private set => SetField(ref _selectedModel, value);
    }

    public string ModelVersion
    {
        get => _modelVersion;
        private set => SetField(ref _modelVersion, value);
    }

    public string ModelLatency
    {
        get => _modelLatency;
        private set => SetField(ref _modelLatency, value);
    }

    public string ModelError
    {
        get => _modelError;
        private set => SetField(ref _modelError, value);
    }

    public string RiskLockState
    {
        get => _riskLockState;
        private set => SetField(ref _riskLockState, value);
    }

    public string BrokerConnection
    {
        get => _brokerConnection;
        private set => SetField(ref _brokerConnection, value);
    }

    public string CurrentPosition
    {
        get => _currentPosition;
        private set => SetField(ref _currentPosition, value);
    }

    public string DataStaleness
    {
        get => _dataStaleness;
        private set => SetField(ref _dataStaleness, value);
    }

    public void Apply(OperationalStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        FeedHealth = Required(snapshot.FeedHealth, nameof(snapshot.FeedHealth));
        FeatureReadiness = Required(snapshot.FeatureReadiness, nameof(snapshot.FeatureReadiness));
        SelectedModel = Required(snapshot.SelectedModel, nameof(snapshot.SelectedModel));
        ModelVersion = Required(snapshot.ModelVersion, nameof(snapshot.ModelVersion));
        ModelLatency = Required(snapshot.ModelLatency, nameof(snapshot.ModelLatency));
        ModelError = Required(snapshot.ModelError, nameof(snapshot.ModelError));
        RiskLockState = Required(snapshot.RiskLockState, nameof(snapshot.RiskLockState));
        BrokerConnection = Required(snapshot.BrokerConnection, nameof(snapshot.BrokerConnection));
        CurrentPosition = Required(snapshot.CurrentPosition, nameof(snapshot.CurrentPosition));
        DataStaleness = Required(snapshot.DataStaleness, nameof(snapshot.DataStaleness));
    }

    private void SetField(
        ref string field,
        string value,
        [CallerMemberName] string? propertyName = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }

    private static string Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Status value is required.", field);
        }

        return value;
    }
}

public sealed record OperationalStatusSnapshot(
    string FeedHealth,
    string FeatureReadiness,
    string SelectedModel,
    string ModelVersion,
    string ModelLatency,
    string ModelError,
    string RiskLockState,
    string BrokerConnection,
    string CurrentPosition,
    string DataStaleness);

public sealed class MainWindowViewModel
{
    public DesktopSettingsViewModel Settings { get; } = new();

    public OperationalStatusViewModel Status { get; } = new();
}
