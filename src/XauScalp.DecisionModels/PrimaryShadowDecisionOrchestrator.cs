using System.Diagnostics;
using XauScalp.Domain;

namespace XauScalp.DecisionModels;

public sealed class PrimaryShadowDecisionOrchestrator
{
    private readonly IXauDecisionModel _jev;
    private readonly IXauDecisionModel _xauNative;
    private readonly JevDecisionCoordinator _jevCoordinator;
    private readonly IDecisionComparisonStore _store;
    private readonly IAuthoritativeTradeDecisionSink _tradeSink;
    private readonly TimeProvider _timeProvider;

    public PrimaryShadowDecisionOrchestrator(
        IXauDecisionModel jev,
        IXauDecisionModel xauNative,
        IDecisionComparisonStore store,
        IAuthoritativeTradeDecisionSink? tradeSink = null,
        IJevTelemetrySink? jevTelemetry = null,
        TimeProvider? timeProvider = null)
    {
        _jev = jev ?? throw new ArgumentNullException(nameof(jev));
        _xauNative = xauNative ?? throw new ArgumentNullException(nameof(xauNative));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tradeSink = tradeSink ?? NullAuthoritativeTradeDecisionSink.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jevCoordinator = new JevDecisionCoordinator(
            _jev,
            _xauNative,
            jevTelemetry);
    }

    public async Task<PrimaryShadowEvaluationResult> EvaluateAsync(
        XauMarketState state,
        DecisionModelSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        Guid comparisonId = Guid.NewGuid();

        PrimaryEvaluation primary = settings.PrimaryDecisionModel switch
        {
            DecisionModelType.Jev => await EvaluateJevPrimaryAsync(
                state,
                settings.JevFailurePolicy,
                cancellationToken).ConfigureAwait(false),

            DecisionModelType.XauNative => await EvaluateNativePrimaryAsync(
                state,
                cancellationToken).ConfigureAwait(false),

            _ => throw new InvalidOperationException(
                "Only JEV and XAU Native are valid primary models."),
        };

        ModelDecisionRecord? shadow = null;
        if (settings.ShadowComparison.Enabled)
        {
            DecisionModelType shadowModel = settings.ShadowComparison.ShadowModel
                ?? throw new InvalidOperationException(
                    "Enabled shadow comparison requires a shadow model.");

            shadow = shadowModel switch
            {
                DecisionModelType.Jev => await EvaluateDirectAsync(
                    _jev,
                    DecisionModelType.Jev,
                    DecisionAuthorityRole.Shadow,
                    isAuthoritative: false,
                    state,
                    cancellationToken).ConfigureAwait(false),

                DecisionModelType.XauNative => await EvaluateDirectAsync(
                    _xauNative,
                    DecisionModelType.XauNative,
                    DecisionAuthorityRole.Shadow,
                    isAuthoritative: false,
                    state,
                    cancellationToken).ConfigureAwait(false),

                _ => throw new InvalidOperationException(
                    "Only JEV and XAU Native are valid shadow models."),
            };
        }

        var comparison = new DecisionComparisonRecord(
            comparisonId,
            state.MarketStateId,
            state.TimestampUtc,
            settings.PrimaryDecisionModel,
            settings.ShadowComparison.Enabled,
            primary.Record,
            shadow,
            _timeProvider.GetUtcNow());

        await _store
            .AppendComparisonAsync(comparison, cancellationToken)
            .ConfigureAwait(false);

        XauDecision? authoritative = primary.Record.Decision;
        if (!primary.StopNewTrades
            && authoritative is not null
            && authoritative.Action is TradeAction.Long or TradeAction.Short)
        {
            await _tradeSink
                .AcceptAsync(
                    new AuthoritativeTradeDecisionEnvelope(
                        comparisonId,
                        authoritative,
                        primary.Record.IsFallback),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new PrimaryShadowEvaluationResult(
            comparisonId,
            authoritative,
            primary.Record.ActualModel,
            primary.StopNewTrades,
            primary.Record.IsFallback);
    }

    private async Task<PrimaryEvaluation> EvaluateNativePrimaryAsync(
        XauMarketState state,
        CancellationToken cancellationToken)
    {
        ModelDecisionRecord record = await EvaluateDirectAsync(
            _xauNative,
            DecisionModelType.XauNative,
            DecisionAuthorityRole.Primary,
            isAuthoritative: true,
            state,
            cancellationToken).ConfigureAwait(false);

        return new PrimaryEvaluation(
            record,
            StopNewTrades: !record.Succeeded);
    }

    private async Task<PrimaryEvaluation> EvaluateJevPrimaryAsync(
        XauMarketState state,
        JevFailurePolicy failurePolicy,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();

        DecisionEvaluationResult result = await _jevCoordinator
            .EvaluateAsync(
                state,
                failurePolicy,
                cancellationToken)
            .ConfigureAwait(false);

        XauDecision? decision = result.Decision;
        bool fallback = result.Source == DecisionEvaluationSource.XauNativeFallback;

        var record = new ModelDecisionRecord(
            DecisionAuthorityRole.Primary,
            DecisionModelType.Jev,
            decision?.ModelType,
            decision,
            Succeeded: decision is not null,
            IsAuthoritative: decision is not null,
            IsFallback: fallback,
            result.FailureCode,
            Stopwatch.GetElapsedTime(started));

        return new PrimaryEvaluation(record, result.StopNewTrades);
    }

    private static async Task<ModelDecisionRecord> EvaluateDirectAsync(
        IXauDecisionModel model,
        DecisionModelType configuredModel,
        DecisionAuthorityRole role,
        bool isAuthoritative,
        XauMarketState state,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            XauDecision decision = await model
                .EvaluateAsync(state, cancellationToken)
                .ConfigureAwait(false);

            ValidateDecision(decision, state, configuredModel);

            return new ModelDecisionRecord(
                role,
                configuredModel,
                decision.ModelType,
                decision,
                Succeeded: true,
                IsAuthoritative: isAuthoritative,
                IsFallback: false,
                FailureCode: null,
                Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            string failureCode = exception switch
            {
                JevAdapterException jev => jev.Code,
                NativeModelInputException => "xau-native-input-failed",
                _ => "model-evaluation-failed",
            };

            return new ModelDecisionRecord(
                role,
                configuredModel,
                ActualModel: null,
                Decision: null,
                Succeeded: false,
                IsAuthoritative: false,
                IsFallback: false,
                failureCode,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private static void ValidateDecision(
        XauDecision decision,
        XauMarketState state,
        DecisionModelType expectedModel)
    {
        if (decision.MarketStateId != state.MarketStateId
            || decision.ModelType != expectedModel
            || !string.Equals(
                decision.FeatureSchemaVersion,
                state.FeatureSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Decision model returned a decision incompatible with the shared market state.");
        }
    }

    private sealed record PrimaryEvaluation(
        ModelDecisionRecord Record,
        bool StopNewTrades);
}
