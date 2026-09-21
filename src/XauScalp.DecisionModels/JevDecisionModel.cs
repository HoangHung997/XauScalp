using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using XauScalp.Domain;

namespace XauScalp.DecisionModels;

public sealed class JevDecisionModel : IXauDecisionModel
{
    public const string RequestSchemaVersion = "jev-request-v1";
    public const string ResponseSchemaVersion = "jev-response-v1";

    private readonly IJevProviderClient _providerClient;
    private readonly IJevSecretProvider _secretProvider;
    private readonly JevDecisionModelOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IJevTelemetrySink _telemetry;
    private readonly JevCircuitBreaker _circuitBreaker;

    public JevDecisionModel(
        IJevProviderClient providerClient,
        IJevSecretProvider secretProvider,
        JevDecisionModelOptions options,
        TimeProvider? timeProvider = null,
        IJevTelemetrySink? telemetry = null)
    {
        _providerClient = providerClient ?? throw new ArgumentNullException(nameof(providerClient));
        _secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _telemetry = telemetry ?? NullJevTelemetrySink.Instance;
        _circuitBreaker = new JevCircuitBreaker(
            options.CircuitBreakerFailureThreshold,
            options.CircuitBreakerOpenDuration);
    }

    public string ModelId => "jev";

    public string ModelVersion => _options.ProviderModelVersion;

    public async Task<XauDecision> EvaluateAsync(
        XauMarketState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = _timeProvider.GetUtcNow();
        ValidateState(state, now);

        if (!_circuitBreaker.TryEnter(now, out TimeSpan remaining))
        {
            throw new JevCircuitOpenException(remaining);
        }

        JevSecret credential;
        try
        {
            credential = await _secretProvider
                .GetSecretAsync(_options.SecretReference, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JevAdapterException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new JevSecretUnavailableException(
                "JEV secret provider failed.",
                exception);
        }

        JevProviderRequest request = BuildRequest(state);
        long evaluationStarted = Stopwatch.GetTimestamp();
        JevAdapterException? finalFailure = null;

        for (int attempt = 1; attempt <= _options.MaxRetries + 1; attempt++)
        {
            long attemptStarted = Stopwatch.GetTimestamp();

            try
            {
                JevProviderResponse response = await InvokeAttemptAsync(
                    request,
                    credential,
                    cancellationToken).ConfigureAwait(false);

                ValidateResponse(state, response, _timeProvider.GetUtcNow());
                _circuitBreaker.RecordSuccess();

                _telemetry.Record(
                    new JevTelemetryEvent(
                        JevTelemetryKind.AttemptSucceeded,
                        state.MarketStateId,
                        request.RequestId,
                        attempt,
                        ModelVersion,
                        FailureCode: null,
                        Stopwatch.GetElapsedTime(attemptStarted),
                        FallbackModelVersion: null));

                return MapDecision(
                    state,
                    request,
                    response,
                    Stopwatch.GetElapsedTime(evaluationStarted));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (JevTransientException exception)
            {
                finalFailure = exception;
                RecordAttemptFailure(state, request, attempt, attemptStarted, exception);

                if (attempt <= _options.MaxRetries)
                {
                    continue;
                }

                break;
            }
            catch (JevAdapterException exception)
            {
                finalFailure = exception;
                RecordAttemptFailure(state, request, attempt, attemptStarted, exception);
                break;
            }
            catch (Exception exception)
            {
                finalFailure = new JevProviderUnavailableException(
                    "JEV provider failed with an unexpected error.",
                    exception);

                RecordAttemptFailure(
                    state,
                    request,
                    attempt,
                    attemptStarted,
                    finalFailure);

                if (attempt <= _options.MaxRetries)
                {
                    continue;
                }

                break;
            }
        }

        JevAdapterException failure = finalFailure
            ?? new JevProviderUnavailableException("JEV evaluation failed without a provider result.");

        bool opened = _circuitBreaker.RecordFailure(_timeProvider.GetUtcNow());
        if (opened)
        {
            _telemetry.Record(
                new JevTelemetryEvent(
                    JevTelemetryKind.CircuitOpened,
                    state.MarketStateId,
                    request.RequestId,
                    Attempt: null,
                    ModelVersion,
                    failure.Code,
                    Stopwatch.GetElapsedTime(evaluationStarted),
                    FallbackModelVersion: null));
        }

        throw failure;
    }

    private async Task<JevProviderResponse> InvokeAttemptAsync(
        JevProviderRequest request,
        JevSecret credential,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_options.AttemptTimeout);

        Task<JevProviderResponse> providerTask = _providerClient.EvaluateAsync(
            request,
            credential,
            linked.Token);

        try
        {
            return await providerTask
                .WaitAsync(_options.AttemptTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new JevTimeoutException(
                "JEV provider attempt exceeded the configured timeout.",
                exception);
        }
        catch (TimeoutException exception)
        {
            linked.Cancel();
            throw new JevTimeoutException(
                "JEV provider attempt exceeded the configured timeout.",
                exception);
        }
    }

    private JevProviderRequest BuildRequest(XauMarketState state)
    {
        JevFeatureInput[] features = state.Features
            .Select(
                static feature => new JevFeatureInput(
                    feature.Name,
                    feature.Value,
                    feature.Unit,
                    feature.IsAvailable,
                    feature.ObservedAtUtc,
                    feature.UnavailableReason))
            .ToArray();

        return new JevProviderRequest(
            RequestSchemaVersion,
            DeterministicRequestId(state.MarketStateId),
            state.MarketStateId,
            state.TimestampUtc,
            state.SequenceId,
            state.Symbol,
            state.BrokerSymbol,
            state.Bid,
            state.Ask,
            state.Mid,
            state.FeatureSchemaVersion,
            _options.ProviderModelId,
            _options.ProviderModelVersion,
            features);
    }

    private XauDecision MapDecision(
        XauMarketState state,
        JevProviderRequest request,
        JevProviderResponse response,
        TimeSpan latency)
    {
        return new XauDecision(
            ContractVersions.DecisionV1,
            request.RequestId,
            state.MarketStateId,
            DecisionModelType.Jev,
            response.Action,
            response.ActionProbability,
            response.PUp5First,
            response.PDown5First,
            response.PUp10First,
            response.PDown10First,
            response.PAdverseBarrierFirst,
            response.PContinuation,
            response.PReversal,
            response.PFalseBreak,
            response.Confidence,
            response.PHold,
            response.PExitNow,
            response.PTp5FromHere,
            response.PTp10FromHere,
            ModelId,
            response.ProviderModelVersion,
            state.FeatureSchemaVersion,
            response.ProviderEvaluatedAtUtc,
            latency);
    }

    private void ValidateState(XauMarketState state, DateTimeOffset now)
    {
        if (!state.Readiness.RequiredP0Ready)
        {
            throw new JevInputException(
                "state-not-ready",
                "JEV inference fails closed while required P0 data is not ready.");
        }

        if (now < state.TimestampUtc)
        {
            throw new JevInputException(
                "state-from-future",
                "JEV market state timestamp is later than the application clock.");
        }

        if (now - state.TimestampUtc > _options.MaxResponseAge)
        {
            throw new JevInputException(
                "state-stale",
                "JEV market state is already stale before provider evaluation.");
        }
    }

    private void ValidateResponse(
        XauMarketState state,
        JevProviderResponse response,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!string.Equals(
            response.ResponseSchemaVersion,
            ResponseSchemaVersion,
            StringComparison.Ordinal))
        {
            throw new JevResponseValidationException(
                "response-schema-mismatch",
                "JEV provider response schema is not supported.");
        }

        if (response.MarketStateId != state.MarketStateId)
        {
            throw new JevResponseValidationException(
                "market-state-mismatch",
                "JEV provider response references a different market state.");
        }

        if (!string.Equals(
                response.ProviderModelId,
                _options.ProviderModelId,
                StringComparison.Ordinal)
            || !string.Equals(
                response.ProviderModelVersion,
                _options.ProviderModelVersion,
                StringComparison.Ordinal))
        {
            throw new JevResponseValidationException(
                "provider-version-mismatch",
                "JEV provider response does not match the pinned model identity.");
        }

        if (!string.Equals(
            response.FeatureSchemaVersion,
            state.FeatureSchemaVersion,
            StringComparison.Ordinal))
        {
            throw new JevResponseValidationException(
                "feature-schema-mismatch",
                "JEV provider response feature schema does not match the market state.");
        }

        if (response.ProviderEvaluatedAtUtc.Offset != TimeSpan.Zero
            || response.ProviderEvaluatedAtUtc < state.TimestampUtc
            || response.ProviderEvaluatedAtUtc > now
            || now - response.ProviderEvaluatedAtUtc > _options.MaxResponseAge)
        {
            throw new JevResponseValidationException(
                "response-stale",
                "JEV provider response timestamp is invalid or stale.");
        }

        if (!Enum.IsDefined(response.Action))
        {
            throw new JevResponseValidationException(
                "invalid-action",
                "JEV provider returned an unknown trade action.");
        }

        ValidateProbability(response.ActionProbability, nameof(response.ActionProbability));
        ValidateProbability(response.PUp5First, nameof(response.PUp5First));
        ValidateProbability(response.PDown5First, nameof(response.PDown5First));
        ValidateProbability(response.PUp10First, nameof(response.PUp10First));
        ValidateProbability(response.PDown10First, nameof(response.PDown10First));
        ValidateProbability(response.PAdverseBarrierFirst, nameof(response.PAdverseBarrierFirst));
        ValidateProbability(response.PContinuation, nameof(response.PContinuation));
        ValidateProbability(response.PReversal, nameof(response.PReversal));
        ValidateProbability(response.PFalseBreak, nameof(response.PFalseBreak));
        ValidateProbability(response.Confidence, nameof(response.Confidence));
        ValidateOptionalProbability(response.PHold, nameof(response.PHold));
        ValidateOptionalProbability(response.PExitNow, nameof(response.PExitNow));
        ValidateOptionalProbability(response.PTp5FromHere, nameof(response.PTp5FromHere));
        ValidateOptionalProbability(response.PTp10FromHere, nameof(response.PTp10FromHere));
    }

    private void RecordAttemptFailure(
        XauMarketState state,
        JevProviderRequest request,
        int attempt,
        long attemptStarted,
        JevAdapterException exception)
    {
        _telemetry.Record(
            new JevTelemetryEvent(
                JevTelemetryKind.AttemptFailed,
                state.MarketStateId,
                request.RequestId,
                attempt,
                ModelVersion,
                exception.Code,
                Stopwatch.GetElapsedTime(attemptStarted),
                FallbackModelVersion: null));
    }

    private Guid DeterministicRequestId(Guid marketStateId)
    {
        string identity = string.Join(
            "|",
            "jev",
            _options.ProviderModelId,
            _options.ProviderModelVersion,
            marketStateId.ToString("D"));

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static void ValidateProbability(double value, string field)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
        {
            throw new JevResponseValidationException(
                "invalid-probability",
                $"JEV provider response field {field} must be finite and in [0,1].");
        }
    }

    private static void ValidateOptionalProbability(double? value, string field)
    {
        if (value is not null)
        {
            ValidateProbability(value.Value, field);
        }
    }

    private sealed class JevCircuitBreaker
    {
        private readonly object _sync = new();
        private readonly int _failureThreshold;
        private readonly TimeSpan _openDuration;
        private int _consecutiveFailures;
        private DateTimeOffset? _openUntilUtc;

        public JevCircuitBreaker(int failureThreshold, TimeSpan openDuration)
        {
            _failureThreshold = failureThreshold;
            _openDuration = openDuration;
        }

        public bool TryEnter(DateTimeOffset now, out TimeSpan remaining)
        {
            lock (_sync)
            {
                if (_openUntilUtc is DateTimeOffset openUntil)
                {
                    if (now < openUntil)
                    {
                        remaining = openUntil - now;
                        return false;
                    }

                    _openUntilUtc = null;
                    _consecutiveFailures = 0;
                }

                remaining = TimeSpan.Zero;
                return true;
            }
        }

        public void RecordSuccess()
        {
            lock (_sync)
            {
                _consecutiveFailures = 0;
                _openUntilUtc = null;
            }
        }

        public bool RecordFailure(DateTimeOffset now)
        {
            lock (_sync)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures < _failureThreshold)
                {
                    return false;
                }

                _openUntilUtc = now + _openDuration;
                return true;
            }
        }
    }
}
