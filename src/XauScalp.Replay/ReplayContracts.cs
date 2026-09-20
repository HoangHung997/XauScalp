using XauScalp.Domain;
using XauScalp.Features;

namespace XauScalp.Replay;

public sealed record ReplayCostScenario
{
    public ReplayCostScenario(
        string name,
        double? estimatedLatencyMs,
        double? estimatedSlippagePoints,
        decimal commissionPerLot)
    {
        Name = Required(name, nameof(name));
        EstimatedLatencyMs = NonNegativeFiniteOptional(estimatedLatencyMs, nameof(estimatedLatencyMs));
        EstimatedSlippagePoints = NonNegativeFiniteOptional(estimatedSlippagePoints, nameof(estimatedSlippagePoints));

        if (commissionPerLot < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commissionPerLot),
                commissionPerLot,
                "Commission per lot must be non-negative.");
        }

        CommissionPerLot = commissionPerLot;
    }

    public string Name { get; }

    public double? EstimatedLatencyMs { get; }

    public double? EstimatedSlippagePoints { get; }

    public decimal CommissionPerLot { get; }

    public static ReplayCostScenario Ideal { get; } =
        new("ideal", estimatedLatencyMs: 0, estimatedSlippagePoints: 0, commissionPerLot: 0);

    private static double? NonNegativeFiniteOptional(double? value, string parameterName)
    {
        if (value is not null && (!double.IsFinite(value.Value) || value.Value < 0))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Value must be finite and non-negative when supplied.");
        }

        return value;
    }

    private static string Required(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", parameterName);
        }

        return value;
    }
}

public sealed record ReplayModelIdentity
{
    public ReplayModelIdentity(string modelId, string modelVersion, string artifactHash)
    {
        ModelId = Required(modelId, nameof(modelId));
        ModelVersion = Required(modelVersion, nameof(modelVersion));
        ArtifactHash = Required(artifactHash, nameof(artifactHash));
    }

    public string ModelId { get; }

    public string ModelVersion { get; }

    public string ArtifactHash { get; }

    public static ReplayModelIdentity None { get; } = new("none", "none", "none");

    private static string Required(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", parameterName);
        }

        return value;
    }
}

public sealed record ReplayRunOptions
{
    public ReplayRunOptions(
        string codeCommit,
        string settingsVersion,
        string settingsHash,
        ReplayModelIdentity model,
        ReplayCostScenario costScenario,
        ReplayTimingOptions timing,
        int randomSeed = 0)
    {
        CodeCommit = Required(codeCommit, nameof(codeCommit));
        SettingsVersion = Required(settingsVersion, nameof(settingsVersion));
        SettingsHash = Required(settingsHash, nameof(settingsHash));
        Model = model ?? throw new ArgumentNullException(nameof(model));
        CostScenario = costScenario ?? throw new ArgumentNullException(nameof(costScenario));
        Timing = timing ?? throw new ArgumentNullException(nameof(timing));
        RandomSeed = randomSeed;
    }

    public string CodeCommit { get; }

    public string SettingsVersion { get; }

    public string SettingsHash { get; }

    public ReplayModelIdentity Model { get; }

    public ReplayCostScenario CostScenario { get; }

    public ReplayTimingOptions Timing { get; }

    public int RandomSeed { get; }

    private static string Required(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", parameterName);
        }

        return value;
    }
}

public sealed record ReplayOutputRecord(
    long Ordinal,
    MarketEvent MarketEvent,
    XauMarketState? FeatureState,
    ReplayCostScenario CostScenario);

public sealed record ReplayRunManifest(
    string RunId,
    string DataSetId,
    string DataSetSha256,
    string FeatureSchemaVersion,
    string FeatureEngineVersion,
    ReplayModelIdentity Model,
    string SettingsVersion,
    string SettingsHash,
    ReplayCostScenario CostScenario,
    ReplayTimingMode TimingMode,
    double AccelerationFactor,
    string CodeCommit,
    DateTimeOffset StartTimestampUtc,
    DateTimeOffset EndTimestampUtc,
    long EventCount,
    long TickCount,
    int RandomSeed);

public sealed record ReplayRunResult(
    ReplayRunManifest Manifest,
    string OutputSha256,
    long RecordsWritten);

public interface IReplayFeatureContextProvider
{
    FeatureExternalContext? GetContext(
        MarketEvent marketEvent,
        ReplayCostScenario costScenario);
}

public interface IReplayRecordSink
{
    ValueTask WriteAsync(
        ReplayOutputRecord record,
        CancellationToken cancellationToken);

    ValueTask CompleteAsync(CancellationToken cancellationToken);
}
