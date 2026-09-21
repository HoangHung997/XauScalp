using XauScalp.Domain;

namespace XauScalp.DecisionModels;

public sealed record JevFeatureInput(
    string Name,
    double? Value,
    string Unit,
    bool IsAvailable,
    DateTimeOffset? ObservedAtUtc,
    string? UnavailableReason);

public sealed record JevProviderRequest(
    string RequestSchemaVersion,
    Guid RequestId,
    Guid MarketStateId,
    DateTimeOffset StateTimestampUtc,
    long SequenceId,
    string Symbol,
    string BrokerSymbol,
    decimal Bid,
    decimal Ask,
    decimal Mid,
    string FeatureSchemaVersion,
    string ProviderModelId,
    string ProviderModelVersion,
    IReadOnlyList<JevFeatureInput> Features);

public sealed record JevProviderResponse(
    string ResponseSchemaVersion,
    Guid MarketStateId,
    TradeAction Action,
    double ActionProbability,
    double PUp5First,
    double PDown5First,
    double PUp10First,
    double PDown10First,
    double PAdverseBarrierFirst,
    double PContinuation,
    double PReversal,
    double PFalseBreak,
    double Confidence,
    double? PHold,
    double? PExitNow,
    double? PTp5FromHere,
    double? PTp10FromHere,
    string ProviderModelId,
    string ProviderModelVersion,
    string FeatureSchemaVersion,
    DateTimeOffset ProviderEvaluatedAtUtc);

public interface IJevProviderClient
{
    Task<JevProviderResponse> EvaluateAsync(
        JevProviderRequest request,
        JevSecret credential,
        CancellationToken cancellationToken);
}

public interface IJevSecretProvider
{
    ValueTask<JevSecret> GetSecretAsync(
        string secretReference,
        CancellationToken cancellationToken);
}

public sealed class JevSecret
{
    private readonly string _value;

    public JevSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("JEV secret cannot be empty.", nameof(value));
        }

        _value = value;
    }

    public string DangerousReveal() => _value;

    public override string ToString() => "***";
}

public enum JevTelemetryKind
{
    AttemptSucceeded = 0,
    AttemptFailed = 1,
    CircuitOpened = 2,
    StopNewTrades = 3,
    FallbackSucceeded = 4,
    FallbackFailed = 5,
}

public sealed record JevTelemetryEvent(
    JevTelemetryKind Kind,
    Guid MarketStateId,
    Guid? RequestId,
    int? Attempt,
    string PrimaryModelVersion,
    string? FailureCode,
    TimeSpan Elapsed,
    string? FallbackModelVersion);

public interface IJevTelemetrySink
{
    void Record(JevTelemetryEvent telemetryEvent);
}

public sealed class NullJevTelemetrySink : IJevTelemetrySink
{
    public static NullJevTelemetrySink Instance { get; } = new();

    private NullJevTelemetrySink()
    {
    }

    public void Record(JevTelemetryEvent telemetryEvent)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);
    }
}

public sealed class JevDecisionModelOptions
{
    public JevDecisionModelOptions(
        string providerModelId,
        string providerModelVersion,
        string secretReference,
        TimeSpan attemptTimeout,
        TimeSpan maxResponseAge,
        int maxRetries,
        int circuitBreakerFailureThreshold,
        TimeSpan circuitBreakerOpenDuration)
    {
        ProviderModelId = Required(providerModelId, nameof(providerModelId));
        ProviderModelVersion = Required(providerModelVersion, nameof(providerModelVersion));
        SecretReference = Required(secretReference, nameof(secretReference));

        if (attemptTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attemptTimeout),
                attemptTimeout,
                "Attempt timeout must be positive.");
        }

        if (maxResponseAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResponseAge),
                maxResponseAge,
                "Maximum response age must be positive.");
        }

        if (maxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRetries),
                maxRetries,
                "Maximum retries must be non-negative.");
        }

        if (circuitBreakerFailureThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(circuitBreakerFailureThreshold),
                circuitBreakerFailureThreshold,
                "Circuit-breaker threshold must be positive.");
        }

        if (circuitBreakerOpenDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(circuitBreakerOpenDuration),
                circuitBreakerOpenDuration,
                "Circuit-breaker open duration must be positive.");
        }

        AttemptTimeout = attemptTimeout;
        MaxResponseAge = maxResponseAge;
        MaxRetries = maxRetries;
        CircuitBreakerFailureThreshold = circuitBreakerFailureThreshold;
        CircuitBreakerOpenDuration = circuitBreakerOpenDuration;
    }

    public string ProviderModelId { get; }

    public string ProviderModelVersion { get; }

    public string SecretReference { get; }

    public TimeSpan AttemptTimeout { get; }

    public TimeSpan MaxResponseAge { get; }

    public int MaxRetries { get; }

    public int CircuitBreakerFailureThreshold { get; }

    public TimeSpan CircuitBreakerOpenDuration { get; }

    private static string Required(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", parameterName);
        }

        return value;
    }
}

public class JevAdapterException : InvalidOperationException
{
    public JevAdapterException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public class JevTransientException : JevAdapterException
{
    public JevTransientException(string code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }
}

public sealed class JevProviderUnavailableException : JevTransientException
{
    public JevProviderUnavailableException(string message, Exception? innerException = null)
        : base("provider-unavailable", message, innerException)
    {
    }
}

public sealed class JevTimeoutException : JevTransientException
{
    public JevTimeoutException(string message, Exception? innerException = null)
        : base("timeout", message, innerException)
    {
    }
}

public sealed class JevResponseValidationException : JevAdapterException
{
    public JevResponseValidationException(string code, string message)
        : base(code, message)
    {
    }
}

public sealed class JevInputException : JevAdapterException
{
    public JevInputException(string code, string message)
        : base(code, message)
    {
    }
}

public sealed class JevSecretUnavailableException : JevAdapterException
{
    public JevSecretUnavailableException(string message, Exception? innerException = null)
        : base("secret-unavailable", message, innerException)
    {
    }
}

public sealed class JevCircuitOpenException : JevAdapterException
{
    public JevCircuitOpenException(TimeSpan remaining)
        : base(
            "circuit-open",
            $"JEV provider circuit is open for another {Math.Max(0, remaining.TotalMilliseconds):0} ms.")
    {
        Remaining = remaining;
    }

    public TimeSpan Remaining { get; }
}
