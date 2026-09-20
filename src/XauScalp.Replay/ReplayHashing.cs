using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XauScalp.Domain;

namespace XauScalp.Replay;

internal sealed class CanonicalJsonLineHasher : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly JsonSerializerOptions _jsonOptions = XauJson.CreateOptions();
    private bool _completed;
    private bool _disposed;

    public void AppendMarketEvent(MarketEvent marketEvent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(marketEvent);

        string json = JsonSerializer.Serialize<MarketEvent>(marketEvent, _jsonOptions);
        AppendUtf8Line(json);
    }

    public void AppendOutputRecord(ReplayOutputRecord record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(record);

        string json = JsonSerializer.Serialize(record, _jsonOptions);
        AppendUtf8Line(json);
    }

    public string Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_completed)
        {
            throw new InvalidOperationException("Hash has already been completed.");
        }

        _completed = true;
        byte[] hash = _hash.GetHashAndReset();
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _hash.Dispose();
        _disposed = true;
    }

    private void AppendUtf8Line(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
        _hash.AppendData(bytes);
    }
}

internal static class ReplayManifestFactory
{
    public static ReplayRunManifest Create(
        ReplayRunOptions options,
        string dataSetSha256,
        string featureSchemaVersion,
        string featureEngineVersion,
        DateTimeOffset startTimestampUtc,
        DateTimeOffset endTimestampUtc,
        long eventCount,
        long tickCount)
    {
        ArgumentNullException.ThrowIfNull(options);

        string identity = string.Join(
            "|",
            dataSetSha256,
            featureSchemaVersion,
            featureEngineVersion,
            options.Model.ModelId,
            options.Model.ModelVersion,
            options.Model.ArtifactHash,
            options.SettingsVersion,
            options.SettingsHash,
            options.CostScenario.Name,
            OptionalDouble(options.CostScenario.EstimatedLatencyMs),
            OptionalDouble(options.CostScenario.EstimatedSlippagePoints),
            options.CostScenario.CommissionPerLot.ToString(CultureInfo.InvariantCulture),
            options.Timing.Mode.ToString(),
            options.Timing.AccelerationFactor.ToString("R", CultureInfo.InvariantCulture),
            options.CodeCommit,
            options.RandomSeed.ToString(CultureInfo.InvariantCulture));

        byte[] runHash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        string runId = "replay-" + Convert.ToHexString(runHash).ToLowerInvariant()[..24];

        return new ReplayRunManifest(
            runId,
            "sha256:" + dataSetSha256,
            dataSetSha256,
            featureSchemaVersion,
            featureEngineVersion,
            options.Model,
            options.SettingsVersion,
            options.SettingsHash,
            options.CostScenario,
            options.Timing.Mode,
            options.Timing.AccelerationFactor,
            options.CodeCommit,
            startTimestampUtc,
            endTimestampUtc,
            eventCount,
            tickCount,
            options.RandomSeed);
    }

    private static string OptionalDouble(double? value)
    {
        return value?.ToString("R", CultureInfo.InvariantCulture) ?? "null";
    }
}
