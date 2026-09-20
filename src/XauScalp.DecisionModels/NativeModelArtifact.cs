using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XauScalp.DecisionModels;

public enum NativeHeadKind
{
    Up5First = 0,
    Down5First = 1,
    Up10First = 2,
    Down10First = 3,
    LongAdverseFirst = 4,
    ShortAdverseFirst = 5,
    Continuation = 6,
    Reversal = 7,
    FalseBreak = 8,
}

public enum NativeHeadMode
{
    Logistic = 0,
    Constant = 1,
}

public sealed record NativeFeatureNormalization
{
    public string FeatureName { get; init; } = string.Empty;

    public double Mean { get; init; }

    public double StdDev { get; init; }
}

public sealed record NativeOutputHead
{
    public NativeHeadKind Name { get; init; }

    public NativeHeadMode Mode { get; init; }

    public double Intercept { get; init; }

    public double[] Weights { get; init; } = [];

    public double CalibrationA { get; init; } = 1;

    public double CalibrationB { get; init; }

    public double? ConstantProbability { get; init; }

    public string TrainingStatus { get; init; } = string.Empty;
}

public sealed record NativeActionPolicy
{
    public double MinimumEdge { get; init; } = 0.05;
}

public sealed record NativeModelArtifact
{
    public const string FormatVersionV1 = "xau-native-linear-v1";

    public string ArtifactFormatVersion { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string ModelVersion { get; init; } = string.Empty;

    public string FeatureSchemaVersion { get; init; } = string.Empty;

    public string[] FeatureNames { get; init; } = [];

    public NativeFeatureNormalization[] Normalization { get; init; } = [];

    public NativeOutputHead[] Heads { get; init; } = [];

    public NativeActionPolicy ActionPolicy { get; init; } = new();
}

public sealed record NativeArtifactManifest
{
    public const string ManifestVersionV1 = "xau-native-artifact-manifest-v1";

    public string ArtifactManifestVersion { get; init; } = string.Empty;

    public string ArtifactFileName { get; init; } = string.Empty;

    public string ArtifactSha256 { get; init; } = string.Empty;

    public string ArtifactFormatVersion { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string ModelVersion { get; init; } = string.Empty;

    public string FeatureSchemaVersion { get; init; } = string.Empty;

    public string TrainingManifestFileName { get; init; } = string.Empty;
}

public sealed record LoadedNativeArtifact(
    NativeModelArtifact Artifact,
    NativeArtifactManifest Manifest,
    string ArtifactSha256);

public static class NativeArtifactLoader
{
    private static readonly NativeHeadKind[] RequiredHeads =
    [
        NativeHeadKind.Up5First,
        NativeHeadKind.Down5First,
        NativeHeadKind.Up10First,
        NativeHeadKind.Down10First,
        NativeHeadKind.LongAdverseFirst,
        NativeHeadKind.ShortAdverseFirst,
        NativeHeadKind.Continuation,
        NativeHeadKind.Reversal,
        NativeHeadKind.FalseBreak,
    ];

    public static LoadedNativeArtifact Load(
        string artifactPath,
        string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        byte[] artifactBytes = File.ReadAllBytes(artifactPath);
        byte[] manifestBytes = File.ReadAllBytes(manifestPath);
        string artifactSha256 = Convert.ToHexString(
            SHA256.HashData(artifactBytes)).ToLowerInvariant();

        JsonSerializerOptions options = CreateJsonOptions();
        NativeArtifactManifest manifest =
            JsonSerializer.Deserialize<NativeArtifactManifest>(
                manifestBytes,
                options)
            ?? throw new InvalidDataException("Native artifact manifest is empty.");

        NativeModelArtifact artifact =
            JsonSerializer.Deserialize<NativeModelArtifact>(
                artifactBytes,
                options)
            ?? throw new InvalidDataException("Native model artifact is empty.");

        ValidateManifest(manifest, artifact, artifactSha256);
        ValidateArtifact(artifact);

        return new LoadedNativeArtifact(
            artifact,
            manifest,
            artifactSha256);
    }

    internal static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
        };

        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));

        return options;
    }

    private static void ValidateManifest(
        NativeArtifactManifest manifest,
        NativeModelArtifact artifact,
        string actualSha256)
    {
        Required(
            manifest.ArtifactManifestVersion,
            nameof(manifest.ArtifactManifestVersion));

        if (!string.Equals(
            manifest.ArtifactManifestVersion,
            NativeArtifactManifest.ManifestVersionV1,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported native artifact manifest version: {manifest.ArtifactManifestVersion}.");
        }

        Required(manifest.ArtifactFileName, nameof(manifest.ArtifactFileName));
        Required(manifest.ArtifactSha256, nameof(manifest.ArtifactSha256));
        Required(
            manifest.TrainingManifestFileName,
            nameof(manifest.TrainingManifestFileName));

        if (!string.Equals(
            manifest.ArtifactSha256,
            actualSha256,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Native model artifact SHA-256 does not match its manifest.");
        }

        if (!string.Equals(
                manifest.ArtifactFormatVersion,
                artifact.ArtifactFormatVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.ModelId,
                artifact.ModelId,
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.ModelVersion,
                artifact.ModelVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.FeatureSchemaVersion,
                artifact.FeatureSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Native model artifact identity does not match its manifest.");
        }
    }

    private static void ValidateArtifact(NativeModelArtifact artifact)
    {
        if (!string.Equals(
            artifact.ArtifactFormatVersion,
            NativeModelArtifact.FormatVersionV1,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported native artifact format: {artifact.ArtifactFormatVersion}.");
        }

        Required(artifact.ModelId, nameof(artifact.ModelId));
        Required(artifact.ModelVersion, nameof(artifact.ModelVersion));
        Required(
            artifact.FeatureSchemaVersion,
            nameof(artifact.FeatureSchemaVersion));

        if (artifact.FeatureNames.Length == 0
            || artifact.FeatureNames.Any(string.IsNullOrWhiteSpace)
            || artifact.FeatureNames.Distinct(StringComparer.Ordinal).Count()
                != artifact.FeatureNames.Length)
        {
            throw new InvalidDataException(
                "Native artifact feature names must be non-empty and unique.");
        }

        if (artifact.Normalization.Length != artifact.FeatureNames.Length)
        {
            throw new InvalidDataException(
                "Native artifact normalization must align one-to-one with features.");
        }

        for (int index = 0; index < artifact.FeatureNames.Length; index++)
        {
            NativeFeatureNormalization normalization =
                artifact.Normalization[index];

            if (!string.Equals(
                    artifact.FeatureNames[index],
                    normalization.FeatureName,
                    StringComparison.Ordinal)
                || !double.IsFinite(normalization.Mean)
                || !double.IsFinite(normalization.StdDev)
                || normalization.StdDev <= 0)
            {
                throw new InvalidDataException(
                    "Native artifact normalization is invalid or out of order.");
            }
        }

        if (artifact.Heads.Length != RequiredHeads.Length
            || artifact.Heads.Select(static head => head.Name).Distinct().Count()
                != RequiredHeads.Length
            || RequiredHeads.Any(
                required => artifact.Heads.All(head => head.Name != required)))
        {
            throw new InvalidDataException(
                "Native artifact must define each required output head exactly once.");
        }

        foreach (NativeOutputHead head in artifact.Heads)
        {
            Required(head.TrainingStatus, nameof(head.TrainingStatus));

            if (!double.IsFinite(head.Intercept)
                || !double.IsFinite(head.CalibrationA)
                || !double.IsFinite(head.CalibrationB))
            {
                throw new InvalidDataException(
                    $"Native output head {head.Name} contains non-finite parameters.");
            }

            if (head.Mode == NativeHeadMode.Logistic)
            {
                if (head.Weights.Length != artifact.FeatureNames.Length
                    || head.Weights.Any(static weight => !double.IsFinite(weight))
                    || head.ConstantProbability is not null)
                {
                    throw new InvalidDataException(
                        $"Logistic head {head.Name} has invalid weights or constant probability.");
                }
            }
            else if (head.Mode == NativeHeadMode.Constant)
            {
                if (head.Weights.Length != 0
                    || head.ConstantProbability is not double probability
                    || !double.IsFinite(probability)
                    || probability is < 0 or > 1)
                {
                    throw new InvalidDataException(
                        $"Constant head {head.Name} has invalid probability.");
                }
            }
            else
            {
                throw new InvalidDataException(
                    $"Unsupported native head mode: {head.Mode}.");
            }
        }

        if (!double.IsFinite(artifact.ActionPolicy.MinimumEdge)
            || artifact.ActionPolicy.MinimumEdge is < 0 or > 1)
        {
            throw new InvalidDataException(
                "Native action policy minimum edge must be in [0,1].");
        }
    }

    private static void Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"Native artifact field {field} is required.");
        }
    }
}
