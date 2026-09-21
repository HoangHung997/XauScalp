using System.Text.Json;

namespace XauScalp.MarketData;

public static class Mt5NdjsonParser
{
    public static Mt5WireMessage Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("MT5 frame cannot be empty.", nameof(json));
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        string type = RequiredString(root, "type");
        long sequence = RequiredInt64(root, "sequence");
        if (sequence < 0)
        {
            throw new InvalidDataException("MT5 source sequence must be non-negative.");
        }

        string brokerSymbol = RequiredString(root, "brokerSymbol");

        return type switch
        {
            "tick" => ParseTick(root, sequence, brokerSymbol),
            "symbol" => ParseSymbol(root, sequence, brokerSymbol),
            "connection" => ParseConnection(root, sequence, brokerSymbol),
            "news" => ParseNewsContext(root, sequence, brokerSymbol),
            _ => throw new InvalidDataException($"Unsupported MT5 frame type '{type}'."),
        };
    }

    private static Mt5WireTick ParseTick(JsonElement root, long sequence, string brokerSymbol)
    {
        long brokerTimeMilliseconds = RequiredInt64(root, "brokerTimeMsc");
        decimal bid = RequiredDecimal(root, "bid");
        decimal ask = RequiredDecimal(root, "ask");
        decimal rawLast = RequiredDecimal(root, "last");
        double rawVolume = RequiredDouble(root, "volume");
        int flags = RequiredInt32(root, "flags");

        if (brokerTimeMilliseconds < 0)
        {
            throw new InvalidDataException("brokerTimeMsc must be non-negative.");
        }

        if (bid <= 0 || ask <= 0 || ask < bid)
        {
            throw new InvalidDataException("MT5 tick bid/ask are invalid.");
        }

        if (!double.IsFinite(rawVolume) || rawVolume < 0)
        {
            throw new InvalidDataException("MT5 tick volume must be finite and non-negative.");
        }

        return new Mt5WireTick(
            sequence,
            brokerSymbol,
            brokerTimeMilliseconds,
            bid,
            ask,
            rawLast > 0 ? rawLast : null,
            rawVolume,
            flags);
    }

    private static Mt5WireSymbolSpecification ParseSymbol(JsonElement root, long sequence, string brokerSymbol)
    {
        return new Mt5WireSymbolSpecification(
            sequence,
            brokerSymbol,
            RequiredInt32(root, "digits"),
            RequiredDecimal(root, "point"),
            RequiredDecimal(root, "tickSize"),
            RequiredDecimal(root, "tickValue"),
            RequiredDecimal(root, "contractSize"),
            RequiredDecimal(root, "minVolume"),
            RequiredDecimal(root, "maxVolume"),
            RequiredDecimal(root, "volumeStep"),
            RequiredDecimal(root, "minStopDistance"));
    }

    private static Mt5WireConnection ParseConnection(JsonElement root, long sequence, string brokerSymbol)
    {
        string stateText = RequiredString(root, "state");
        Mt5WireConnectionState state = stateText switch
        {
            "connected" => Mt5WireConnectionState.Connected,
            "reconnecting" => Mt5WireConnectionState.Reconnecting,
            "disconnected" => Mt5WireConnectionState.Disconnected,
            _ => throw new InvalidDataException($"Unsupported MT5 connection state '{stateText}'."),
        };

        string? reason = root.TryGetProperty("reason", out JsonElement reasonElement)
            && reasonElement.ValueKind != JsonValueKind.Null
                ? reasonElement.GetString()
                : null;

        return new Mt5WireConnection(sequence, brokerSymbol, state, reason);
    }

    private static Mt5WireNewsContext ParseNewsContext(
        JsonElement root,
        long sequence,
        string brokerSymbol)
    {
        bool available = RequiredBoolean(root, "available");
        string source = RequiredString(root, "source");
        int? errorCode = OptionalInt32(root, "sourceErrorCode");
        double? before = OptionalDouble(root, "newsDistanceBeforeSec");
        double? after = OptionalDouble(root, "newsDistanceAfterSec");

        if (available)
        {
            if (before is not double beforeValue
                || !double.IsFinite(beforeValue)
                || beforeValue < 0
                || after is not double afterValue
                || !double.IsFinite(afterValue)
                || afterValue < 0)
            {
                throw new InvalidDataException(
                    "Available MT5 news context requires non-negative finite distances.");
            }
        }
        else if (before is not null || after is not null)
        {
            throw new InvalidDataException(
                "Unavailable MT5 news context cannot carry synthetic distances.");
        }

        return new Mt5WireNewsContext(
            sequence,
            brokerSymbol,
            available,
            before,
            after,
            source,
            errorCode);
    }

    private static bool RequiredBoolean(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException(
                $"MT5 frame is missing boolean '{propertyName}'.");
        }

        return value.GetBoolean();
    }

    private static int? OptionalInt32(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (!value.TryGetInt32(out int result))
        {
            throw new InvalidDataException(
                $"MT5 frame '{propertyName}' must be an integer or null.");
        }

        return result;
    }

    private static double? OptionalDouble(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (!value.TryGetDouble(out double result)
            || !double.IsFinite(result))
        {
            throw new InvalidDataException(
                $"MT5 frame '{propertyName}' must be a finite number or null.");
        }

        return result;
    }

    private static string RequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"MT5 frame is missing string '{propertyName}'.");
        }

        return value.GetString()!;
    }

    private static long RequiredInt64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || !value.TryGetInt64(out long result))
        {
            throw new InvalidDataException($"MT5 frame is missing integer '{propertyName}'.");
        }

        return result;
    }

    private static int RequiredInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || !value.TryGetInt32(out int result))
        {
            throw new InvalidDataException($"MT5 frame is missing integer '{propertyName}'.");
        }

        return result;
    }

    private static decimal RequiredDecimal(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || !value.TryGetDecimal(out decimal result))
        {
            throw new InvalidDataException($"MT5 frame is missing decimal '{propertyName}'.");
        }

        return result;
    }

    private static double RequiredDouble(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value)
            || !value.TryGetDouble(out double result))
        {
            throw new InvalidDataException($"MT5 frame is missing numeric '{propertyName}'.");
        }

        return result;
    }
}
