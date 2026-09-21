using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using XauScalp.DecisionModels;
using XauScalp.Domain;

namespace XauScalp.Persistence;

public sealed class DecisionComparisonJsonlStore :
    IDecisionComparisonStore,
    IAsyncDisposable
{
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions = XauJson.CreateOptions();
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public DecisionComparisonJsonlStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Decision telemetry path is required.", nameof(path));
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

    public ValueTask AppendComparisonAsync(
        DecisionComparisonRecord comparison,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        return AppendAsync(
            new StoredDecisionEvent(
                StoredDecisionEventKind.Comparison,
                comparison.ComparisonId,
                comparison,
                FutureLabels: null,
                ExecutionOutcome: null),
            cancellationToken);
    }

    public ValueTask AttachFutureLabelsAsync(
        Guid comparisonId,
        DecisionFutureLabels labels,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(labels);
        return AppendAsync(
            new StoredDecisionEvent(
                StoredDecisionEventKind.FutureLabels,
                comparisonId,
                Comparison: null,
                labels,
                ExecutionOutcome: null),
            cancellationToken);
    }

    public ValueTask AttachExecutionOutcomeAsync(
        Guid comparisonId,
        ExecutedTradeOutcome outcome,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return AppendAsync(
            new StoredDecisionEvent(
                StoredDecisionEventKind.ExecutionOutcome,
                comparisonId,
                Comparison: null,
                FutureLabels: null,
                outcome),
            cancellationToken);
    }

    public async Task<IReadOnlyList<DecisionComparisonBundle>> ReadAllAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await FlushAsync(cancellationToken).ConfigureAwait(false);

        var order = new List<Guid>();
        var bundles = new Dictionary<Guid, DecisionComparisonBundle>();

        await foreach (StoredDecisionEvent stored in ReadEventsAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            switch (stored.Kind)
            {
                case StoredDecisionEventKind.Comparison:
                    if (stored.Comparison is null
                        || stored.Comparison.ComparisonId != stored.ComparisonId
                        || bundles.ContainsKey(stored.ComparisonId))
                    {
                        throw new InvalidDataException(
                            "Decision comparison telemetry contains an invalid or duplicate comparison.");
                    }

                    order.Add(stored.ComparisonId);
                    bundles.Add(
                        stored.ComparisonId,
                        new DecisionComparisonBundle(
                            stored.Comparison,
                            FutureLabels: null,
                            ExecutionOutcome: null));
                    break;

                case StoredDecisionEventKind.FutureLabels:
                    if (stored.FutureLabels is null
                        || !bundles.TryGetValue(stored.ComparisonId, out DecisionComparisonBundle? labelBundle)
                        || labelBundle.Comparison.MarketStateId != stored.FutureLabels.MarketStateId)
                    {
                        throw new InvalidDataException(
                            "Decision future-label event cannot be linked to its comparison.");
                    }

                    bundles[stored.ComparisonId] = labelBundle with
                    {
                        FutureLabels = stored.FutureLabels,
                    };
                    break;

                case StoredDecisionEventKind.ExecutionOutcome:
                    if (stored.ExecutionOutcome is null
                        || !bundles.TryGetValue(stored.ComparisonId, out DecisionComparisonBundle? executionBundle)
                        || executionBundle.Comparison.Primary.Decision?.DecisionId
                            != stored.ExecutionOutcome.DecisionId
                        || !executionBundle.Comparison.Primary.IsAuthoritative)
                    {
                        throw new InvalidDataException(
                            "Execution outcome cannot be linked to the authoritative primary decision.");
                    }

                    bundles[stored.ComparisonId] = executionBundle with
                    {
                        ExecutionOutcome = stored.ExecutionOutcome,
                    };
                    break;

                default:
                    throw new InvalidDataException(
                        $"Unsupported decision telemetry event kind {stored.Kind}.");
            }
        }

        return order.Select(id => bundles[id]).ToArray();
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

    private async ValueTask AppendAsync(
        StoredDecisionEvent stored,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string json = JsonSerializer.Serialize(stored, _jsonOptions);

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

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async IAsyncEnumerable<StoredDecisionEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
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
                yield break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidDataException(
                    "Decision telemetry contains an unexpected blank line.");
            }

            StoredDecisionEvent? stored;
            try
            {
                stored = JsonSerializer.Deserialize<StoredDecisionEvent>(
                    line,
                    _jsonOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "Decision telemetry contains invalid JSON.",
                    exception);
            }

            yield return stored
                ?? throw new InvalidDataException(
                    "Decision telemetry contains a null event.");
        }
    }

    private enum StoredDecisionEventKind
    {
        Comparison = 0,
        FutureLabels = 1,
        ExecutionOutcome = 2,
    }

    private sealed record StoredDecisionEvent(
        StoredDecisionEventKind Kind,
        Guid ComparisonId,
        DecisionComparisonRecord? Comparison,
        DecisionFutureLabels? FutureLabels,
        ExecutedTradeOutcome? ExecutionOutcome);
}
