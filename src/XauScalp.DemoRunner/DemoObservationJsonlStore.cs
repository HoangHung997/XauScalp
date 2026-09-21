using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XauScalp.Domain;
using XauScalp.Runtime;

namespace XauScalp.DemoRunner;

public enum DemoObservationKind
{
    StartupReconciliation = 0,
    RiskLedgerSync = 1,
    Evaluation = 2,
    ExecutionFailureRecorded = 3,
    ReadyStateSnapshot = 4,
}

public sealed record DemoObservation(
    DemoObservationKind Kind,
    DateTimeOffset OccurredAtUtc,
    Guid? MarketStateId,
    Guid? ComparisonId,
    Guid? DecisionId,
    Guid? RiskDecisionId,
    Guid? TradeIntentId,
    string? Outcome,
    string? ReasonCode,
    string? BrokerOrderId,
    string? BrokerDealId,
    string? BrokerPositionId,
    double? ModelLatencyMs,
    double? SlippagePoints,
    string? Message);

public sealed class DemoObservationJsonlStore :
    IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    public DemoObservationJsonlStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _stream = new FileStream(
            fullPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        _writer = new StreamWriter(
            _stream,
            new UTF8Encoding(false),
            32 * 1024,
            leaveOpen: false);

        _jsonOptions = new JsonSerializerOptions(
            JsonSerializerDefaults.Web);
        _jsonOptions.Converters.Add(
            new JsonStringEnumConverter());
    }

    public async ValueTask AppendAsync(
        DemoObservation observation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(observation);

        string json = JsonSerializer.Serialize(
            observation,
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

    public ValueTask AppendEvaluationAsync(
        XauMarketState state,
        DemoRuntimeEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(evaluation);

        XauDecision? decision = evaluation.AuthoritativeDecision;
        return AppendAsync(
            new DemoObservation(
                DemoObservationKind.Evaluation,
                DateTimeOffset.UtcNow,
                state.MarketStateId,
                evaluation.ComparisonId,
                decision?.DecisionId,
                evaluation.RiskDecision?.RiskDecisionId,
                evaluation.TradePlan?.TradeIntentId,
                evaluation.Outcome.ToString(),
                evaluation.RiskDecision?.ReasonCode,
                evaluation.ExecutionResult?.BrokerOrderId,
                evaluation.ExecutionResult?.BrokerDealId,
                evaluation.Reconciliation?.Issues
                    .FirstOrDefault()?.Message,
                decision?.Latency.TotalMilliseconds,
                evaluation.ExecutionResult?.SlippagePoints,
                evaluation.ExecutionResult?.Message),
            cancellationToken);
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
