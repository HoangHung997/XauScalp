using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XauScalp.Domain;
using XauScalp.Replay;

namespace XauScalp.DemoRunner;

public sealed record LiveFeatureSnapshot(
    long SequenceId,
    Guid MarketStateId,
    string StateSha256);

public sealed class LiveFeatureSnapshotJsonlStore :
    IAsyncDisposable
{
    private readonly string _path;
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions =
        XauJson.CreateOptions();
    private bool _disposed;

    public LiveFeatureSnapshotJsonlStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _stream = new FileStream(
            _path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous
                | FileOptions.SequentialScan);

        _writer = new StreamWriter(
            _stream,
            new UTF8Encoding(false),
            64 * 1024,
            leaveOpen: false);
    }

    public async ValueTask AppendAsync(
        XauMarketState state,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(state);

        var snapshot = new LiveFeatureSnapshot(
            state.SequenceId,
            state.MarketStateId,
            ComputeStateSha256(state));

        string json = JsonSerializer.Serialize(
            snapshot,
            _jsonOptions);

        await _gate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(
                json.AsMemory(),
                cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async IAsyncEnumerable<LiveFeatureSnapshot> ReadAllAsync(
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await _writer.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            64 * 1024,
            FileOptions.Asynchronous
                | FileOptions.SequentialScan);

        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false),
            true,
            64 * 1024,
            leaveOpen: false);

        while (true)
        {
            string? line = await reader.ReadLineAsync(
                cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidDataException(
                    "Live feature snapshot journal contains a blank line.");
            }

            LiveFeatureSnapshot? snapshot;
            try
            {
                snapshot = JsonSerializer
                    .Deserialize<LiveFeatureSnapshot>(
                        line,
                        _jsonOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "Live feature snapshot journal contains invalid JSON.",
                    exception);
            }

            yield return snapshot
                ?? throw new InvalidDataException(
                    "Live feature snapshot journal contains a null record.");
        }
    }

    public static string ComputeStateSha256(
        XauMarketState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string json = JsonSerializer.Serialize(
            state,
            XauJson.CreateOptions());
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash)
            .ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.FlushAsync().ConfigureAwait(false);
            await _writer.DisposeAsync().ConfigureAwait(false);
            _disposed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

public sealed class ReplayLiveParitySink :
    IReplayRecordSink,
    IAsyncDisposable
{
    private readonly IReplayRecordSink _inner;
    private readonly IAsyncEnumerator<LiveFeatureSnapshot>
        _expected;
    private long _matched;
    private bool _completed;

    public ReplayLiveParitySink(
        IReplayRecordSink inner,
        IAsyncEnumerable<LiveFeatureSnapshot> expected,
        CancellationToken cancellationToken)
    {
        _inner = inner
            ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(expected);
        _expected = expected.GetAsyncEnumerator(
            cancellationToken);
    }

    public long MatchedTickStates => _matched;

    public async ValueTask WriteAsync(
        ReplayOutputRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.FeatureState is XauMarketState state)
        {
            bool hasExpected = await _expected
                .MoveNextAsync().ConfigureAwait(false);

            if (!hasExpected)
            {
                throw new InvalidDataException(
                    $"Replay produced tick state sequence {state.SequenceId}, but live parity evidence ended early.");
            }

            LiveFeatureSnapshot expected = _expected.Current;
            string actualHash =
                LiveFeatureSnapshotJsonlStore
                    .ComputeStateSha256(state);

            if (expected.SequenceId != state.SequenceId
                || expected.MarketStateId != state.MarketStateId
                || !string.Equals(
                    expected.StateSha256,
                    actualHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Live/replay feature parity mismatch at replay sequence {state.SequenceId}.");
            }

            _matched++;
        }

        await _inner.WriteAsync(
            record,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CompleteAsync(
        CancellationToken cancellationToken)
    {
        if (await _expected.MoveNextAsync()
            .ConfigureAwait(false))
        {
            throw new InvalidDataException(
                $"Live parity evidence contains extra tick state sequence {_expected.Current.SequenceId}.");
        }

        await _inner.CompleteAsync(
            cancellationToken).ConfigureAwait(false);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        await _expected.DisposeAsync().ConfigureAwait(false);

        if (_inner is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync()
                .ConfigureAwait(false);
        }

        if (!_completed)
        {
            // Deliberately no implicit completion:
            // a failed parity run must remain failed.
        }
    }
}
