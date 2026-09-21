using System.Text.Json;
using System.Text.Json.Serialization;
using XauScalp.Domain;
using XauScalp.Features;

namespace XauScalp.DemoRunner;

public sealed record DemoRunnerConfiguration
{
    public string CodeCommit { get; init; } = string.Empty;

    public string CiRunUrl { get; init; } = string.Empty;

    public string Mt5CommonFilesPath { get; init; } = string.Empty;

    public string DataDirectory { get; init; } = string.Empty;

    public string CanonicalSymbol { get; init; } = "XAUUSD";

    public string BrokerSymbol { get; init; } = string.Empty;

    public string DataSourceId { get; init; } = "mt5-demo";

    public long MagicNumber { get; init; } = 991188;

    public string RuntimeInstanceId { get; init; } = "xauscalp-demo-v1";

    public string StrategyId { get; init; } = "xauscalp";

    public int BrokerUtcOffsetMinutes { get; init; }

    public string SessionDayStartBrokerTime { get; init; } = "00:00";

    public SessionSegmentConfiguration[] Sessions { get; init; } =
    [
        new(0, "00-08", "00:00", "08:00"),
        new(1, "08-13", "08:00", "13:00"),
        new(2, "13-17", "13:00", "17:00"),
        new(3, "17-24", "17:00", "00:00"),
    ];

    public string NativeArtifactPath { get; init; } = string.Empty;

    public string NativeArtifactManifestPath { get; init; } = string.Empty;

    public JevRunnerConfiguration Jev { get; init; } = new();

    public DecisionModelType PrimaryDecisionModel { get; init; } =
        DecisionModelType.XauNative;

    public bool ShadowComparisonEnabled { get; init; } = true;

    public JevFailurePolicy JevFailurePolicy { get; init; } =
        JevFailurePolicy.StopNewTrades;

    public DemoRiskConfiguration Risk { get; init; } = new();

    public DemoTradeConfiguration Trade { get; init; } = new();

    public DemoCostConfiguration CostAssumptions { get; init; } = new();

    public DemoEvaluationConfiguration Evaluation { get; init; } = new();

    public int ExternalContextMaxAgeSec { get; init; } = 60;

    public bool StopAfterFirstExecution { get; init; }

    public static DemoRunnerConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        byte[] bytes = File.ReadAllBytes(fullPath);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));

        DemoRunnerConfiguration configuration =
            JsonSerializer.Deserialize<DemoRunnerConfiguration>(
                bytes,
                options)
            ?? throw new InvalidDataException(
                "Demo runner configuration is empty.");

        configuration.Validate(fullPath);
        return configuration;
    }

    public void Validate(string? sourcePath = null)
    {
        Required(CodeCommit, nameof(CodeCommit));
        if (CodeCommit.Length != 40
            || CodeCommit.Any(
                static ch => !Uri.IsHexDigit(ch)))
        {
            throw new InvalidDataException(
                "CodeCommit must be an exact 40-hex git SHA.");
        }

        Required(CiRunUrl, nameof(CiRunUrl));
        if (!Uri.TryCreate(
                CiRunUrl,
                UriKind.Absolute,
                out Uri? ciUri)
            || !string.Equals(
                ciUri.Host,
                "github.com",
                StringComparison.OrdinalIgnoreCase)
            || !ciUri.AbsolutePath.Contains(
                "/actions/runs/",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "CiRunUrl must be a GitHub Actions run URL.");
        }

        Required(Mt5CommonFilesPath, nameof(Mt5CommonFilesPath));
        Required(DataDirectory, nameof(DataDirectory));
        Required(CanonicalSymbol, nameof(CanonicalSymbol));
        Required(BrokerSymbol, nameof(BrokerSymbol));
        Required(DataSourceId, nameof(DataSourceId));
        Required(RuntimeInstanceId, nameof(RuntimeInstanceId));
        Required(StrategyId, nameof(StrategyId));
        Required(NativeArtifactPath, nameof(NativeArtifactPath));
        Required(
            NativeArtifactManifestPath,
            nameof(NativeArtifactManifestPath));

        if (!string.Equals(
                CanonicalSymbol,
                "XAUUSD",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "XSP-017 demo runner canonical symbol must be XAUUSD.");
        }

        if (MagicNumber < 0)
        {
            throw new InvalidDataException(
                "MagicNumber must be non-negative.");
        }

        if (BrokerUtcOffsetMinutes is < -14 * 60 or > 14 * 60)
        {
            throw new InvalidDataException(
                "Broker UTC offset must be between -14h and +14h.");
        }

        _ = ParseTime(
            SessionDayStartBrokerTime,
            nameof(SessionDayStartBrokerTime));

        if (Sessions is null || Sessions.Length == 0)
        {
            throw new InvalidDataException(
                "At least one broker-local session segment is required.");
        }

        _ = BuildSessionSchedule();

        if (ExternalContextMaxAgeSec <= 0)
        {
            throw new InvalidDataException(
                "ExternalContextMaxAgeSec must be positive.");
        }

        Jev.Validate();
        Risk.Validate();
        Trade.Validate();
        CostAssumptions.Validate();
        Evaluation.Validate();

        if (ShadowComparisonEnabled
            && PrimaryDecisionModel is not (
                DecisionModelType.Jev
                or DecisionModelType.XauNative))
        {
            throw new InvalidDataException(
                "Only JEV and XAU Native are permitted decision models.");
        }

        string fullSource = string.IsNullOrWhiteSpace(sourcePath)
            ? string.Empty
            : Path.GetFullPath(sourcePath);

        string[] sensitiveNames =
        [
            "password",
            "apiKey",
            "api_key",
            "token",
            "secretValue",
            "credentialValue",
        ];

        if (!string.IsNullOrEmpty(fullSource))
        {
            string text = File.ReadAllText(fullSource);
            foreach (string sensitive in sensitiveNames)
            {
                if (text.Contains(
                        "\"" + sensitive + "\"",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Demo runner config must not contain credential field '{sensitive}'.");
                }
            }
        }
    }

    public PositionOwnership BuildOwnership()
    {
        return new PositionOwnership(
            MagicNumber,
            RuntimeInstanceId,
            StrategyId);
    }

    public DecisionModelSettings BuildDecisionSettings()
    {
        DecisionModelType? shadow = !ShadowComparisonEnabled
            ? null
            : PrimaryDecisionModel == DecisionModelType.Jev
                ? DecisionModelType.XauNative
                : DecisionModelType.Jev;

        return new DecisionModelSettings(
            PrimaryDecisionModel,
            JevFailurePolicy,
            new ShadowComparisonSettings(
                ShadowComparisonEnabled,
                shadow));
    }

    public RiskSettings BuildRiskSettings()
    {
        return Risk.Build();
    }

    public BrokerClockConfiguration BuildBrokerClock()
    {
        return new BrokerClockConfiguration(
            "demo-runner-clock-v1",
            ParseTime(
                SessionDayStartBrokerTime,
                nameof(SessionDayStartBrokerTime)),
            [
                new BrokerClockSegment(
                    DateTimeOffset.MinValue,
                    TimeSpan.FromMinutes(
                        BrokerUtcOffsetMinutes)),
            ]);
    }

    public MarketSessionSchedule BuildSessionSchedule()
    {
        return new MarketSessionSchedule(
            Sessions.Select(
                segment => new MarketSessionSegment(
                    segment.Code,
                    segment.Name,
                    ParseTime(
                        segment.StartInclusive,
                        nameof(segment.StartInclusive)),
                    ParseTime(
                        segment.EndExclusive,
                        nameof(segment.EndExclusive)))));
    }

    public string ResolvePath(string value)
    {
        string expanded = Environment.ExpandEnvironmentVariables(value);
        return Path.GetFullPath(expanded);
    }

    private static TimeOnly ParseTime(
        string value,
        string field)
    {
        if (!TimeOnly.TryParseExact(
                value,
                "HH:mm",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out TimeOnly result))
        {
            throw new InvalidDataException(
                $"{field} must use HH:mm.");
        }

        return result;
    }

    private static void Required(
        string? value,
        string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"{field} is required.");
        }
    }
}

public sealed record SessionSegmentConfiguration(
    int Code,
    string Name,
    string StartInclusive,
    string EndExclusive);

public sealed record JevRunnerConfiguration
{
    public string Endpoint { get; init; } = string.Empty;

    public string SecretReference { get; init; } =
        "env:XAUSCALP_JEV_API_KEY";

    public string ProviderModelId { get; init; } = "jev";

    public string ProviderModelVersion { get; init; } = string.Empty;

    public string AuthorizationScheme { get; init; } = "Bearer";

    public int TimeoutMs { get; init; } = 1_000;

    public int MaxResponseAgeMs { get; init; } = 2_000;

    public int MaxRetries { get; init; } = 1;

    public int CircuitBreakerFailureThreshold { get; init; } = 3;

    public int CircuitBreakerOpenSec { get; init; } = 30;

    public bool AllowInsecureHttp { get; init; }

    public void Validate()
    {
        if (!Uri.TryCreate(
                Endpoint,
                UriKind.Absolute,
                out Uri? endpoint))
        {
            throw new InvalidDataException(
                "Jev.Endpoint must be an absolute URI.");
        }

        _ = new JevHttpProviderClientOptions(
            endpoint,
            AuthorizationScheme,
            allowInsecureHttp: AllowInsecureHttp);

        if (string.IsNullOrWhiteSpace(SecretReference))
        {
            throw new InvalidDataException(
                "JEV secret reference is required.");
        }

        if (!SecretReference.StartsWith(
                "env:",
                StringComparison.Ordinal)
            && !SecretReference.StartsWith(
                "cred:",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "JEV secret reference must use env: or cred:.");
        }

        if (string.IsNullOrWhiteSpace(ProviderModelId)
            || string.IsNullOrWhiteSpace(ProviderModelVersion))
        {
            throw new InvalidDataException(
                "Pinned JEV provider model id/version are required.");
        }

        if (TimeoutMs <= 0
            || MaxResponseAgeMs <= 0
            || MaxRetries < 0
            || CircuitBreakerFailureThreshold <= 0
            || CircuitBreakerOpenSec <= 0)
        {
            throw new InvalidDataException(
                "JEV timeout/retry/circuit settings are invalid.");
        }
    }
}

public sealed record DemoRiskConfiguration
{
    public double MaxRiskPerTradePct { get; init; } = 0.5;

    public double MaxDailyLossPct { get; init; } = 2;

    public decimal? MaxDailyLossMoney { get; init; } = 500m;

    public int MaxTradesPerDay { get; init; } = 20;

    public int MaxConcurrentPositions { get; init; } = 1;

    public decimal MaxSpreadPrice { get; init; } = 0.8m;

    public double MaxSpreadAtrRatio { get; init; } = 0.25;

    public int MaxSlippagePoints { get; init; } = 30;

    public int MaxDecisionAgeMs { get; init; } = 2_000;

    public int MaxFeatureAgeMs { get; init; } = 2_000;

    public double MinFreeMarginPct { get; init; } = 20;

    public int CooldownAfterLossSec { get; init; } = 60;

    public int CooldownAfterExecutionFailureSec { get; init; } = 30;

    public int HighImpactNewsBlockBeforeSec { get; init; } = 900;

    public int HighImpactNewsBlockAfterSec { get; init; } = 900;

    public bool AllowLong { get; init; } = true;

    public bool AllowShort { get; init; } = true;

    public void Validate() => _ = Build();

    public RiskSettings Build()
    {
        return new RiskSettings(
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
    }
}

public sealed record DemoTradeConfiguration
{
    public string RiskPolicyVersion { get; init; } =
        "demo-risk-v1";

    public string SettingsVersion { get; init; } =
        "demo-settings-v1";

    public decimal ProtectiveStopDistancePrice { get; init; } = 3m;

    public decimal TakeProfitDistancePrice { get; init; } = 5m;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RiskPolicyVersion)
            || string.IsNullOrWhiteSpace(SettingsVersion)
            || ProtectiveStopDistancePrice <= 0
            || TakeProfitDistancePrice <= 0)
        {
            throw new InvalidDataException(
                "Demo trade policy/settings versions and stop/target distances must be positive.");
        }
    }
}

public sealed record DemoCostConfiguration
{
    public double EstimatedLatencyMs { get; init; } = 100;

    public double EstimatedSlippagePoints { get; init; } = 30;

    public decimal CommissionPerLot { get; init; }

    public void Validate()
    {
        if (!double.IsFinite(EstimatedLatencyMs)
            || EstimatedLatencyMs < 0
            || !double.IsFinite(EstimatedSlippagePoints)
            || EstimatedSlippagePoints < 0
            || CommissionPerLot < 0)
        {
            throw new InvalidDataException(
                "Cost assumptions must be finite and non-negative.");
        }
    }
}

public sealed record DemoEvaluationConfiguration
{
    public int MinimumIntervalMs { get; init; } = 500;

    public int MaximumIntervalMs { get; init; } = 10_000;

    public double DecelerationThreshold { get; init; } = 0.6;

    public double DirectionFlipMaxAgeMs { get; init; } = 500;

    public void Validate()
    {
        if (MinimumIntervalMs <= 0
            || MaximumIntervalMs < MinimumIntervalMs
            || !double.IsFinite(DecelerationThreshold)
            || DecelerationThreshold is < 0 or > 1
            || !double.IsFinite(DirectionFlipMaxAgeMs)
            || DirectionFlipMaxAgeMs < 0)
        {
            throw new InvalidDataException(
                "Demo evaluation trigger settings are invalid.");
        }
    }
}
