using System.Diagnostics;
using XauScalp.Domain;

namespace XauScalp.DecisionModels;

public enum DecisionEvaluationSource
{
    JevPrimary = 0,
    XauNativeFallback = 1,
}

public sealed record DecisionEvaluationResult(
    XauDecision? Decision,
    DecisionEvaluationSource? Source,
    bool StopNewTrades,
    string? FailureCode,
    TimeSpan Elapsed);

public sealed class JevDecisionCoordinator
{
    private readonly IXauDecisionModel _jev;
    private readonly IXauDecisionModel _xauNative;
    private readonly IJevTelemetrySink _telemetry;

    public JevDecisionCoordinator(
        IXauDecisionModel jev,
        IXauDecisionModel xauNative,
        IJevTelemetrySink? telemetry = null)
    {
        _jev = jev ?? throw new ArgumentNullException(nameof(jev));
        _xauNative = xauNative ?? throw new ArgumentNullException(nameof(xauNative));
        _telemetry = telemetry ?? NullJevTelemetrySink.Instance;
    }

    public async Task<DecisionEvaluationResult> EvaluateAsync(
        XauMarketState state,
        JevFailurePolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        long started = Stopwatch.GetTimestamp();

        try
        {
            XauDecision decision = await _jev
                .EvaluateAsync(state, cancellationToken)
                .ConfigureAwait(false);

            EnsureDecisionMatchesState(decision, state, DecisionModelType.Jev);

            return new DecisionEvaluationResult(
                decision,
                DecisionEvaluationSource.JevPrimary,
                StopNewTrades: false,
                FailureCode: null,
                Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JevAdapterException exception)
        {
            if (policy == JevFailurePolicy.StopNewTrades)
            {
                _telemetry.Record(
                    new JevTelemetryEvent(
                        JevTelemetryKind.StopNewTrades,
                        state.MarketStateId,
                        RequestId: null,
                        Attempt: null,
                        _jev.ModelVersion,
                        exception.Code,
                        Stopwatch.GetElapsedTime(started),
                        FallbackModelVersion: null));

                return new DecisionEvaluationResult(
                    Decision: null,
                    Source: null,
                    StopNewTrades: true,
                    exception.Code,
                    Stopwatch.GetElapsedTime(started));
            }

            try
            {
                XauDecision fallback = await _xauNative
                    .EvaluateAsync(state, cancellationToken)
                    .ConfigureAwait(false);

                EnsureDecisionMatchesState(
                    fallback,
                    state,
                    DecisionModelType.XauNative);

                _telemetry.Record(
                    new JevTelemetryEvent(
                        JevTelemetryKind.FallbackSucceeded,
                        state.MarketStateId,
                        RequestId: null,
                        Attempt: null,
                        _jev.ModelVersion,
                        exception.Code,
                        Stopwatch.GetElapsedTime(started),
                        _xauNative.ModelVersion));

                return new DecisionEvaluationResult(
                    fallback,
                    DecisionEvaluationSource.XauNativeFallback,
                    StopNewTrades: false,
                    exception.Code,
                    Stopwatch.GetElapsedTime(started));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception fallbackException)
            {
                string failureCode = fallbackException is JevAdapterException adapter
                    ? adapter.Code
                    : "xau-native-fallback-failed";

                _telemetry.Record(
                    new JevTelemetryEvent(
                        JevTelemetryKind.FallbackFailed,
                        state.MarketStateId,
                        RequestId: null,
                        Attempt: null,
                        _jev.ModelVersion,
                        failureCode,
                        Stopwatch.GetElapsedTime(started),
                        _xauNative.ModelVersion));

                return new DecisionEvaluationResult(
                    Decision: null,
                    Source: null,
                    StopNewTrades: true,
                    failureCode,
                    Stopwatch.GetElapsedTime(started));
            }
        }
    }

    private static void EnsureDecisionMatchesState(
        XauDecision decision,
        XauMarketState state,
        DecisionModelType expectedModelType)
    {
        if (decision.MarketStateId != state.MarketStateId
            || decision.ModelType != expectedModelType
            || !string.Equals(
                decision.FeatureSchemaVersion,
                state.FeatureSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new JevResponseValidationException(
                "decision-contract-mismatch",
                "Decision model returned a decision incompatible with the supplied market state.");
        }
    }
}
