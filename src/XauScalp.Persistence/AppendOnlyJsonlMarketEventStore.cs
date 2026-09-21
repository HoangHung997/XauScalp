using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using XauScalp.Domain;
using XauScalp.MarketData;

namespace XauScalp.Persistence;

public sealed class AppendOnlyJsonlMarketEventStore : IRawMarketEventSink, IAsyncDisposable
{
    private readonly string _path;
    private readonly int _flushEveryRecords;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _pendingSinceFlush;
    private bool _disposed;

    public AppendOnlyJsonlMarketEventStore(string path, int flushEveryRecords = 1)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Dataset path is required.", nameof(path));
        }

        if (flushEveryRecords <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(flushEveryRecords),
                flushEveryRecords,
                "Flush interval must be positive.");
        }

        _path = Path.GetFullPath(path);
        _flushEveryRecords = flushEveryRecords;
        _jsonOptions = XauJson.CreateOptions();

        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _stream = new FileStream(
            _path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        _writer = new StreamWriter(
            _stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 64 * 1024,
            leaveOpen: false);
    }

    public string DatasetPath => _path;

    public async ValueTask AppendAsync(
        MarketEvent marketEvent,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(marketEvent);

        string json = JsonSerializer.Serialize<MarketEvent>(marketEvent, _jsonOptions);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            _pendingSinceFlush++;

            if (_pendingSinceFlush >= _flushEveryRecords)
            {
                await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                _pendingSinceFlush = 0;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            _pendingSinceFlush = 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long?> GetLastSequenceIdAsync(CancellationToken cancellationToken = default)
    {
        long? last = null;
        await foreach (MarketEvent marketEvent in ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            last = marketEvent.SequenceId;
        }

        return last;
    }

    public async Task<long?> GetHighestSourceSequenceIdAsync(
        CancellationToken cancellationToken = default)
    {
        long? highest = null;

        await foreach (MarketEvent marketEvent in ReadAllAsync(
            cancellationToken).ConfigureAwait(false))
        {
            if (marketEvent is FeedGapEvent)
            {
                continue;
            }

            highest = highest is null
                ? marketEvent.SequenceId
                : Math.Max(highest.Value, marketEvent.SequenceId);
        }

        return highest;
    }

    public async IAsyncEnumerable<MarketEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await FlushAsync(cancellationToken).ConfigureAwait(false);

        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024,
            leaveOpen: false);

        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidDataException("Raw market-event dataset contains an unexpected blank line.");
            }

            MarketEvent? marketEvent;
            try
            {
                marketEvent = JsonSerializer.Deserialize<MarketEvent>(line, _jsonOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "Raw market-event dataset contains invalid or truncated JSON.",
                    exception);
            }

            if (marketEvent is null)
            {
                throw new InvalidDataException("Raw market-event dataset contains a null event.");
            }

            yield return marketEvent;
        }
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
