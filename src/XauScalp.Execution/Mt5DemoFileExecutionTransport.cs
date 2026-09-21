using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XauScalp.Execution;

public sealed class Mt5DemoFileExecutionTransport : IMt5DemoExecutionTransport
{
    private readonly Mt5DemoExecutionGatewayOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public Mt5DemoFileExecutionTransport(
        Mt5DemoExecutionGatewayOptions options,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }

    public async Task<Mt5DemoExecutionReply> ExchangeAsync(
        Mt5DemoExecutionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(
                command.ProtocolVersion,
                Mt5DemoExecutionProtocol.Version,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported MT5 demo execution protocol '{command.ProtocolVersion}'.");
        }

        if (!command.DemoOnly)
        {
            throw new InvalidOperationException(
                "MT5 execution transport is demo-only and refuses commands without DemoOnly=true.");
        }

        string sessionId = RequireActiveDemoBridge();
        Mt5DemoExecutionCommand sessionCommand = command with
        {
            BridgeSessionId = sessionId,
        };

        await AppendCommandAsync(sessionCommand, cancellationToken).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed <= _options.CommandTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Mt5DemoExecutionReply? reply = TryReadFinalReply(
                sessionCommand.CommandId,
                sessionId);

            if (reply is not null)
            {
                return reply;
            }

            await Task
                .Delay(_options.PollInterval, cancellationToken)
                .ConfigureAwait(false);
        }

        throw new BrokerOutcomeUnknownException(
            $"Timed out waiting for MT5 demo bridge response for command {sessionCommand.CommandId}. "
            + "The command was durably appended, so the broker outcome must be reconciled before any retry.");
    }

    private string RequireActiveDemoBridge()
    {
        IReadOnlyList<string> lines = ReadCompleteLines(_options.EventFilePath);
        if (lines.Count == 0)
        {
            throw new BrokerSafeToRetryException(
                "MT5 demo execution bridge has not emitted readiness/heartbeat evidence. No command was sent.");
        }

        BridgeHeartbeat? latestHeartbeat = null;
        var readyBySession = new Dictionary<string, BridgeReady>(StringComparer.Ordinal);

        foreach (string line in lines)
        {
            using JsonDocument document = ParseLine(line);
            JsonElement root = document.RootElement;

            string? type = OptionalString(root, "type");
            if (string.Equals(
                    type,
                    Mt5DemoExecutionProtocol.BridgeReadyType,
                    StringComparison.Ordinal))
            {
                BridgeReady ready = ParseReady(root);
                readyBySession[ready.SessionId] = ready;
                continue;
            }

            if (!string.Equals(
                    type,
                    Mt5DemoExecutionProtocol.BridgeHeartbeatType,
                    StringComparison.Ordinal))
            {
                continue;
            }

            BridgeHeartbeat heartbeat = ParseHeartbeat(root);
            if (latestHeartbeat is null
                || heartbeat.UtcUnixMilliseconds > latestHeartbeat.UtcUnixMilliseconds)
            {
                latestHeartbeat = heartbeat;
            }
        }

        if (latestHeartbeat is null)
        {
            throw new BrokerSafeToRetryException(
                "MT5 demo execution bridge heartbeat is unavailable. No command was sent.");
        }

        if (!readyBySession.TryGetValue(
                latestHeartbeat.SessionId,
                out BridgeReady? ready))
        {
            throw new InvalidDataException(
                "MT5 demo execution bridge heartbeat has no matching bridgeReady record.");
        }

        ValidateBridgeIdentity(ready, latestHeartbeat);
        ValidateHeartbeatFreshness(latestHeartbeat);

        return latestHeartbeat.SessionId;
    }

    private void ValidateBridgeIdentity(
        BridgeReady ready,
        BridgeHeartbeat heartbeat)
    {
        if (!ready.DemoAccountVerified || !heartbeat.DemoAccountVerified)
        {
            throw new InvalidOperationException(
                "MT5 bridge did not verify a demo account. Real-money execution is forbidden.");
        }

        if (!string.Equals(
                ready.ProtocolVersion,
                Mt5DemoExecutionProtocol.Version,
                StringComparison.Ordinal)
            || !string.Equals(
                heartbeat.ProtocolVersion,
                Mt5DemoExecutionProtocol.Version,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "MT5 demo bridge readiness/heartbeat protocol version mismatch.");
        }

        if (!string.Equals(
                ready.BrokerSymbol,
                _options.BrokerSymbol,
                StringComparison.Ordinal)
            || !string.Equals(
                heartbeat.BrokerSymbol,
                _options.BrokerSymbol,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "MT5 demo bridge broker symbol does not match configured broker symbol.");
        }

        if (ready.MagicNumber != _options.Ownership.MagicNumber
            || heartbeat.MagicNumber != _options.Ownership.MagicNumber)
        {
            throw new InvalidOperationException(
                "MT5 demo bridge magic number does not match configured ownership.");
        }
    }

    private void ValidateHeartbeatFreshness(BridgeHeartbeat heartbeat)
    {
        long now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        long tolerance = checked((long)_options.MaxHeartbeatAge.TotalMilliseconds);

        if (heartbeat.UtcUnixMilliseconds < now - tolerance
            || heartbeat.UtcUnixMilliseconds > now + tolerance)
        {
            throw new BrokerSafeToRetryException(
                "MT5 demo bridge heartbeat is stale or clock-skewed beyond the configured fail-closed window. "
                + "No command was sent.");
        }
    }

    private async Task AppendCommandAsync(
        Mt5DemoExecutionCommand command,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_options.CommandFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(command, _jsonOptions);
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(
                _options.CommandFilePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough);

            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private Mt5DemoExecutionReply? TryReadFinalReply(
        Guid commandId,
        string sessionId)
    {
        foreach (string line in ReadCompleteLines(_options.EventFilePath))
        {
            using JsonDocument document = ParseLine(line);
            JsonElement root = document.RootElement;

            string? type = OptionalString(root, "type");
            if (!string.Equals(
                    type,
                    Mt5DemoExecutionProtocol.ExecutionResultType,
                    StringComparison.Ordinal)
                && !string.Equals(
                    type,
                    Mt5DemoExecutionProtocol.StateType,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryGuid(root, "commandId", out Guid candidate)
                || candidate != commandId)
            {
                continue;
            }

            string? candidateSession = OptionalString(root, "bridgeSessionId");
            if (!string.Equals(candidateSession, sessionId, StringComparison.Ordinal))
            {
                continue;
            }

            Mt5DemoExecutionReply? reply = JsonSerializer.Deserialize<Mt5DemoExecutionReply>(
                line,
                _jsonOptions);

            return reply
                ?? throw new InvalidDataException(
                    $"MT5 demo bridge returned a null response for command {commandId}.");
        }

        return null;
    }

    private static JsonDocument ParseLine(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "MT5 demo execution event file contains an invalid complete JSON line.",
                exception);
        }
    }

    private static IReadOnlyList<string> ReadCompleteLines(string path)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        byte[] bytes;
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length > int.MaxValue)
            {
                throw new InvalidDataException(
                    "MT5 demo execution event file is too large for fail-closed scan mode.");
            }

            bytes = new byte[stream.Length];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            if (offset != bytes.Length)
            {
                Array.Resize(ref bytes, offset);
            }
        }

        int lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
        if (lastNewline < 0)
        {
            return Array.Empty<string>();
        }

        string text = Encoding.UTF8.GetString(bytes, 0, lastNewline + 1);
        return text
            .Split('\n')
            .Select(static line => line.TrimEnd('\r'))
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }

    private static BridgeReady ParseReady(JsonElement root)
    {
        return new BridgeReady(
            RequiredString(root, "protocolVersion"),
            RequiredString(root, "bridgeSessionId"),
            RequiredBoolean(root, "demoAccountVerified"),
            RequiredString(root, "brokerSymbol"),
            RequiredInt64(root, "magicNumber"));
    }

    private static BridgeHeartbeat ParseHeartbeat(JsonElement root)
    {
        return new BridgeHeartbeat(
            RequiredString(root, "protocolVersion"),
            RequiredString(root, "bridgeSessionId"),
            RequiredBoolean(root, "demoAccountVerified"),
            RequiredString(root, "brokerSymbol"),
            RequiredInt64(root, "magicNumber"),
            RequiredInt64(root, "utcUnixMs"));
    }

    private static string RequiredString(JsonElement root, string propertyName)
    {
        string? value = OptionalString(root, propertyName);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException(
                $"MT5 demo bridge event requires non-empty '{propertyName}'.");
    }

    private static string? OptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw new InvalidDataException(
                $"MT5 demo bridge event '{propertyName}' must be a JSON string.");
    }

    private static bool RequiredBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException(
                $"MT5 demo bridge event '{propertyName}' must be a JSON boolean.");
        }

        return value.GetBoolean();
    }

    private static long RequiredInt64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out long result))
        {
            throw new InvalidDataException(
                $"MT5 demo bridge event '{propertyName}' must be an integer.");
        }

        return result;
    }

    private static bool TryGuid(
        JsonElement root,
        string propertyName,
        out Guid result)
    {
        result = Guid.Empty;

        return root.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && Guid.TryParse(value.GetString(), out result);
    }

    private sealed record BridgeReady(
        string ProtocolVersion,
        string SessionId,
        bool DemoAccountVerified,
        string BrokerSymbol,
        long MagicNumber);

    private sealed record BridgeHeartbeat(
        string ProtocolVersion,
        string SessionId,
        bool DemoAccountVerified,
        string BrokerSymbol,
        long MagicNumber,
        long UtcUnixMilliseconds);
}
