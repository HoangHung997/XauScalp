using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using XauScalp.Domain;
using XauScalp.Risk;

namespace XauScalp.Persistence;

public sealed class RiskLedgerJsonlStore :
    IOwnedRiskLedger,
    IAsyncDisposable
{
    private readonly string _path;
    private readonly PositionOwnership _ownership;
    private readonly TimeOnly _tradingDayBoundaryUtc;
    private readonly JsonSerializerOptions _jsonOptions = XauJson.CreateOptions();
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public RiskLedgerJsonlStore(
        string path,
        PositionOwnership ownership,
        TimeOnly? tradingDayBoundaryUtc = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Risk ledger path is required.", nameof(path));
        }

        _path = Path.GetFullPath(path);
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        _tradingDayBoundaryUtc = tradingDayBoundaryUtc ?? TimeOnly.MinValue;

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

    public ValueTask StartTradingDayAsync(
        DateTimeOffset occurredAtUtc,
        decimal startEquity,
        CancellationToken cancellationToken = default)
    {
        EnsureUtc(occurredAtUtc);

        if (startEquity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startEquity),
                startEquity,
                "Trading-day start equity must be positive.");
        }

        return AppendAsync(
            new RiskLedgerEvent(
                RiskLedgerEventKind.TradingDayStarted,
                occurredAtUtc,
                startEquity,
                RealizedPnlMoney: null,
                CommissionCostMoney: null,
                Ownership: null),
            cancellationToken);
    }

    public ValueTask RecordClosedTradeAsync(
        DateTimeOffset occurredAtUtc,
        decimal realizedPnlMoney,
        decimal commissionCostMoney,
        PositionOwnership ownership,
        CancellationToken cancellationToken = default)
    {
        EnsureUtc(occurredAtUtc);
        ArgumentNullException.ThrowIfNull(ownership);

        if (commissionCostMoney < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commissionCostMoney),
                commissionCostMoney,
                "Commission cost must be non-negative.");
        }

        return AppendAsync(
            new RiskLedgerEvent(
                RiskLedgerEventKind.TradeClosed,
                occurredAtUtc,
                StartEquity: null,
                realizedPnlMoney,
                commissionCostMoney,
                ownership),
            cancellationToken);
    }

    public ValueTask RecordExecutionFailureAsync(
        DateTimeOffset occurredAtUtc,
        PositionOwnership ownership,
        CancellationToken cancellationToken = default)
    {
        EnsureUtc(occurredAtUtc);
        ArgumentNullException.ThrowIfNull(ownership);

        return AppendAsync(
            new RiskLedgerEvent(
                RiskLedgerEventKind.ExecutionFailure,
                occurredAtUtc,
                StartEquity: null,
                RealizedPnlMoney: null,
                CommissionCostMoney: null,
                ownership),
            cancellationToken);
    }

    public OwnedRiskLedgerSnapshot GetSnapshot(DateTimeOffset asOfUtc)
    {
        EnsureUtc(asOfUtc);
        ObjectDisposedException.ThrowIf(_disposed, this);

        FlushSynchronously();

        DateOnly tradingDay = ResolveTradingDay(asOfUtc);
        decimal startEquity = 0;
        decimal realizedNet = 0;
        int closedTrades = 0;
        DateTimeOffset? lastLoss = null;
        DateTimeOffset? lastExecutionFailure = null;
        bool started = false;

        foreach (RiskLedgerEvent ledgerEvent in ReadEventsSynchronously())
        {
            if (ResolveTradingDay(ledgerEvent.OccurredAtUtc) != tradingDay)
            {
                continue;
            }

            switch (ledgerEvent.Kind)
            {
                case RiskLedgerEventKind.TradingDayStarted:
                    if (ledgerEvent.StartEquity is not decimal equity || equity <= 0)
                    {
                        throw new InvalidDataException(
                            "Risk ledger contains an invalid trading-day start.");
                    }

                    startEquity = equity;
                    realizedNet = 0;
                    closedTrades = 0;
                    lastLoss = null;
                    lastExecutionFailure = null;
                    started = true;
                    break;

                case RiskLedgerEventKind.TradeClosed:
                    if (!IsOwned(ledgerEvent.Ownership))
                    {
                        break;
                    }

                    if (ledgerEvent.RealizedPnlMoney is not decimal pnl
                        || ledgerEvent.CommissionCostMoney is not decimal commission
                        || commission < 0)
                    {
                        throw new InvalidDataException(
                            "Risk ledger contains an invalid closed-trade event.");
                    }

                    decimal net = pnl - commission;
                    realizedNet += net;
                    closedTrades++;
                    if (net < 0)
                    {
                        lastLoss = ledgerEvent.OccurredAtUtc;
                    }

                    break;

                case RiskLedgerEventKind.ExecutionFailure:
                    if (IsOwned(ledgerEvent.Ownership))
                    {
                        lastExecutionFailure = ledgerEvent.OccurredAtUtc;
                    }

                    break;

                default:
                    throw new InvalidDataException(
                        $"Unsupported risk ledger event kind {ledgerEvent.Kind}.");
            }
        }

        return new OwnedRiskLedgerSnapshot(
            tradingDay,
            IsReady: started,
            startEquity,
            realizedNet,
            closedTrades,
            lastLoss,
            lastExecutionFailure);
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
        RiskLedgerEvent ledgerEvent,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string json = JsonSerializer.Serialize(ledgerEvent, _jsonOptions);

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

    private void FlushSynchronously()
    {
        _gate.Wait();
        try
        {
            _writer.Flush();
        }
        finally
        {
            _gate.Release();
        }
    }

    private IEnumerable<RiskLedgerEvent> ReadEventsSynchronously()
    {
        using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            64 * 1024,
            FileOptions.SequentialScan);

        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true,
            64 * 1024,
            leaveOpen: false);

        while (reader.ReadLine() is string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidDataException(
                    "Risk ledger contains an unexpected blank line.");
            }

            RiskLedgerEvent? ledgerEvent;
            try
            {
                ledgerEvent = JsonSerializer.Deserialize<RiskLedgerEvent>(
                    line,
                    _jsonOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "Risk ledger contains invalid JSON.",
                    exception);
            }

            yield return ledgerEvent
                ?? throw new InvalidDataException(
                    "Risk ledger contains a null event.");
        }
    }

    private DateOnly ResolveTradingDay(DateTimeOffset utc)
    {
        DateTime value = utc.UtcDateTime;
        DateOnly date = DateOnly.FromDateTime(value);
        TimeOnly time = TimeOnly.FromDateTime(value);

        return time < _tradingDayBoundaryUtc
            ? date.AddDays(-1)
            : date;
    }

    private bool IsOwned(PositionOwnership? ownership)
    {
        return ownership is not null
            && ownership.MagicNumber == _ownership.MagicNumber
            && string.Equals(
                ownership.StrategyId,
                _ownership.StrategyId,
                StringComparison.Ordinal);
    }

    private static void EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Risk ledger timestamps must use UTC offset +00:00.");
        }
    }

    private enum RiskLedgerEventKind
    {
        TradingDayStarted = 0,
        TradeClosed = 1,
        ExecutionFailure = 2,
    }

    private sealed record RiskLedgerEvent(
        RiskLedgerEventKind Kind,
        DateTimeOffset OccurredAtUtc,
        decimal? StartEquity,
        decimal? RealizedPnlMoney,
        decimal? CommissionCostMoney,
        PositionOwnership? Ownership);
}
