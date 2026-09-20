using System.Text;
using System.Text.Json;
using XauScalp.Domain;

namespace XauScalp.Replay;

public sealed class InMemoryReplayRecordSink : IReplayRecordSink
{
    private readonly List<ReplayOutputRecord> _records = [];

    public IReadOnlyList<ReplayOutputRecord> Records => _records;

    public ValueTask WriteAsync(
        ReplayOutputRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);
        _records.Add(record);
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

public sealed class JsonlReplayRecordSink : IReplayRecordSink, IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private readonly JsonSerializerOptions _jsonOptions = XauJson.CreateOptions();
    private bool _disposed;

    public JsonlReplayRecordSink(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanWrite)
        {
            throw new ArgumentException("Replay export stream must be writable.", nameof(stream));
        }

        _writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 64 * 1024,
            leaveOpen);
    }

    public async ValueTask WriteAsync(
        ReplayOutputRecord record,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(record);

        string json = JsonSerializer.Serialize(record, _jsonOptions);
        await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _writer.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
    }
}
