namespace XauScalp.Domain;

internal static class ContractGuard
{
    public static string Required(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", parameterName);
        }

        return value;
    }

    public static Guid NonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identifier must not be empty.", parameterName);
        }

        return value;
    }

    public static DateTimeOffset Utc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use UTC offset +00:00.", parameterName);
        }

        return value;
    }

    public static long NonNegative(long value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be non-negative.");
        }

        return value;
    }

    public static int NonNegative(int value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be non-negative.");
        }

        return value;
    }

    public static int Positive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be greater than zero.");
        }

        return value;
    }

    public static decimal Positive(decimal value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be greater than zero.");
        }

        return value;
    }

    public static decimal NonNegative(decimal value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be non-negative.");
        }

        return value;
    }

    public static double NonNegative(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be finite and non-negative.");
        }

        return value;
    }

    public static double Percentage(double value, string parameterName, bool allowZero = false)
    {
        bool lowerBoundInvalid = allowZero ? value < 0 : value <= 0;
        if (!double.IsFinite(value) || lowerBoundInvalid || value > 100)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Percentage must be finite and within the allowed 0..100 range.");
        }

        return value;
    }

    public static double Probability(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Probability must be finite and in [0, 1].");
        }

        return value;
    }

    public static double? OptionalProbability(double? value, string parameterName)
    {
        return value is null ? null : Probability(value.Value, parameterName);
    }

    public static string ExactVersion(string? value, string expected, string parameterName)
    {
        string version = Required(value, parameterName);
        if (!string.Equals(version, expected, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unsupported contract version '{version}'. Expected '{expected}'.", parameterName);
        }

        return version;
    }
}
