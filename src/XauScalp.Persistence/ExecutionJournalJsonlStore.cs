using System.Text;
using System.Text.Json;
using XauScalp.Domain;
using XauScalp.Execution;

namespace XauScalp.Persistence;

public sealed class ExecutionJournalJsonlStore :
    IExecutionJournal,
    IAsyncDisposable
{
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions = XauJson.CreateOptions();
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public ExecutionJournalJsonlStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "Execution journal path is required.",
                nameof(path));
        }

        _path = Path.GetFullPath(path);
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
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        _writer = new StreamWriter(
            _stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            64 * 1024,
            leaveOpen: false);
    }

    public async ValueTask AppendAsync(
        ExecutionJournalEvent journalEvent,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(journalEvent);

        string json = JsonSerializer.Serialize(journalEvent, _jsonOptions);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer
                .WriteLineAsync(json.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExecutionLifecycleSnapshot?> GetAsync(
        Guid tradeIntentId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ExecutionJournalEvent> events = await ReadAllEventsAsync(
            cancellationToken).ConfigureAwait(false);

        ExecutionJournalEvent[] matching = events
            .Where(item => item.TradeIntentId == tradeIntentId)
            .ToArray();

        return matching.Length == 0
            ? null
            : MakeSnapshot(matching);
    }

    public async Task<IReadOnlyList<ExecutionLifecycleSnapshot>> GetAllAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ExecutionJournalEvent> events = await ReadAllEventsAsync(
            cancellationToken).ConfigureAwait(false);

        return events
            .GroupBy(static item => item.TradeIntentId)
            .Select(group => MakeSnapshot(group.ToArray()))
            .OrderBy(static snapshot => snapshot.Plan.CreatedAtUtc)
            .ThenBy(static snapshot => snapshot.Plan.TradeIntentId)
            .ToArray();
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

    private async Task<IReadOnlyList<ExecutionJournalEvent>> ReadAllEventsAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        var events = new List<ExecutionJournalEvent>();

        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true,
            64 * 1024,
            leaveOpen: false);

        while (true)
        {
            string? line = await reader
                .ReadLineAsync(cancellationToken)
                .ConfigureAwait(false);

            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidDataException(
                    "Execution journal contains an unexpected blank line.");
            }

            ExecutionJournalEvent? journalEvent;
            try
            {
                journalEvent = JsonSerializer.Deserialize<ExecutionJournalEvent>(
                    line,
                    _jsonOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "Execution journal contains invalid JSON.",
                    exception);
            }

            events.Add(
                journalEvent
                ?? throw new InvalidDataException(
                    "Execution journal contains a null event."));
        }

        Validate(events);
        return events;
    }

    private static ExecutionLifecycleSnapshot MakeSnapshot(
        ExecutionJournalEvent[] events)
    {
        ExecutionJournalEvent latest = events[^1];

        return new ExecutionLifecycleSnapshot(
            latest.Plan,
            latest.Ownership,
            latest.EffectiveState,
            latest.Result,
            events);
    }

    private static void Validate(IReadOnlyList<ExecutionJournalEvent> events)
    {
        var lastTimestamp = new Dictionary<Guid, DateTimeOffset>();
        var plans = new Dictionary<Guid, TradePlan>();

        foreach (ExecutionJournalEvent journalEvent in events)
        {
            if (plans.TryGetValue(
                    journalEvent.TradeIntentId,
                    out TradePlan? existingPlan)
                && existingPlan != journalEvent.Plan)
            {
                throw new InvalidDataException(
                    "Execution journal reuses a TradeIntentId for a different plan.");
            }

            plans[journalEvent.TradeIntentId] = journalEvent.Plan;

            if (lastTimestamp.TryGetValue(
                    journalEvent.TradeIntentId,
                    out DateTimeOffset previous)
                && journalEvent.RecordedAtUtc < previous)
            {
                throw new InvalidDataException(
                    "Execution journal timestamps regress within one trade intent.");
            }

            lastTimestamp[journalEvent.TradeIntentId] =
                journalEvent.RecordedAtUtc;
        }
    }
}
