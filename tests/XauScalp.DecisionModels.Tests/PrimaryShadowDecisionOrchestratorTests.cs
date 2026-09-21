using XauScalp.Domain;

namespace XauScalp.DecisionModels.Tests;

public sealed class PrimaryShadowDecisionOrchestratorTests
{
    [Fact]
    public async Task ShadowLong_PrimaryWait_CannotReachAuthoritativeTradeSink()
    {
        XauMarketState state = MakeState();
        var primary = new RecordingModel(
            DecisionModelType.XauNative,
            TradeAction.Wait);
        var shadow = new RecordingModel(
            DecisionModelType.Jev,
            TradeAction.Long);
        var store = new InMemoryDecisionComparisonStore();
        var sink = new RecordingTradeSink();

        var orchestrator = new PrimaryShadowDecisionOrchestrator(
            shadow,
            primary,
            store,
            sink);

        DecisionModelSettings settings = new(
            DecisionModelType.XauNative,
            JevFailurePolicy.StopNewTrades,
            new ShadowComparisonSettings(
                enabled: true,
                shadowModel: DecisionModelType.Jev));

        PrimaryShadowEvaluationResult result = await orchestrator.EvaluateAsync(
            state,
            settings,
            CancellationToken.None);

        Assert.Equal(TradeAction.Wait, result.AuthoritativeDecision!.Action);
        Assert.Empty(sink.Envelopes);

        DecisionComparisonBundle bundle = Assert.Single(
            await store.ReadAllAsync(CancellationToken.None));

        Assert.Equal(TradeAction.Wait, bundle.Comparison.Primary.Decision!.Action);
        Assert.Equal(TradeAction.Long, bundle.Comparison.Shadow!.Decision!.Action);
        Assert.True(bundle.Comparison.Primary.IsAuthoritative);
        Assert.False(bundle.Comparison.Shadow.IsAuthoritative);
    }

    [Fact]
    public async Task PrimaryAndShadowReceiveSameMarketStateInstance()
    {
        XauMarketState state = MakeState();
        var jev = new RecordingModel(
            DecisionModelType.Jev,
            TradeAction.Wait);
        var native = new RecordingModel(
            DecisionModelType.XauNative,
            TradeAction.Wait);

        var orchestrator = new PrimaryShadowDecisionOrchestrator(
            jev,
            native,
            new InMemoryDecisionComparisonStore());

        await orchestrator.EvaluateAsync(
            state,
            new DecisionModelSettings(
                DecisionModelType.Jev,
                JevFailurePolicy.StopNewTrades,
                new ShadowComparisonSettings(
                    true,
                    DecisionModelType.XauNative)),
            CancellationToken.None);

        Assert.Same(state, jev.LastState);
        Assert.Same(state, native.LastState);
        Assert.Equal(state.MarketStateId, jev.LastState!.MarketStateId);
        Assert.Equal(state.MarketStateId, native.LastState!.MarketStateId);
    }

    [Fact]
    public async Task OnlyActionablePrimaryDecisionReachesTradeSink()
    {
        XauMarketState state = MakeState();
        var jev = new RecordingModel(
            DecisionModelType.Jev,
            TradeAction.Short);
        var native = new RecordingModel(
            DecisionModelType.XauNative,
            TradeAction.Long);
        var sink = new RecordingTradeSink();

        var orchestrator = new PrimaryShadowDecisionOrchestrator(
            jev,
            native,
            new InMemoryDecisionComparisonStore(),
            sink);

        PrimaryShadowEvaluationResult result = await orchestrator.EvaluateAsync(
            state,
            new DecisionModelSettings(
                DecisionModelType.Jev,
                JevFailurePolicy.StopNewTrades,
                new ShadowComparisonSettings(
                    true,
                    DecisionModelType.XauNative)),
            CancellationToken.None);

        AuthoritativeTradeDecisionEnvelope envelope = Assert.Single(sink.Envelopes);
        Assert.Equal(TradeAction.Short, envelope.Decision.Action);
        Assert.Equal(result.ComparisonId, envelope.ComparisonId);
        Assert.False(envelope.IsFallback);
    }

    [Fact]
    public async Task JevFallbackToNative_IsMarkedAuthoritativeFallback()
    {
        XauMarketState state = MakeState();
        var jev = new RecordingModel(
            DecisionModelType.Jev,
            TradeAction.Long,
            failure: new JevProviderUnavailableException("offline"));
        var native = new RecordingModel(
            DecisionModelType.XauNative,
            TradeAction.Long);
        var store = new InMemoryDecisionComparisonStore();
        var sink = new RecordingTradeSink();

        var orchestrator = new PrimaryShadowDecisionOrchestrator(
            jev,
            native,
            store,
            sink);

        PrimaryShadowEvaluationResult result = await orchestrator.EvaluateAsync(
            state,
            new DecisionModelSettings(
                DecisionModelType.Jev,
                JevFailurePolicy.FallbackToXauNative,
                new ShadowComparisonSettings(
                    enabled: false,
                    shadowModel: null)),
            CancellationToken.None);

        Assert.False(result.StopNewTrades);
        Assert.True(result.UsedFallback);
        Assert.Equal(DecisionModelType.XauNative, result.AuthoritativeModel);

        AuthoritativeTradeDecisionEnvelope envelope = Assert.Single(sink.Envelopes);
        Assert.True(envelope.IsFallback);

        DecisionComparisonBundle bundle = Assert.Single(
            await store.ReadAllAsync(CancellationToken.None));
        Assert.True(bundle.Comparison.Primary.IsFallback);
        Assert.Equal(
            DecisionModelType.Jev,
            bundle.Comparison.Primary.ConfiguredModel);
        Assert.Equal(
            DecisionModelType.XauNative,
            bundle.Comparison.Primary.ActualModel);
    }

    [Fact]
    public async Task ReportSeparatesAllStateFromExecutedTradeScoring()
    {
        XauMarketState state = MakeState();
        var jev = new RecordingModel(
            DecisionModelType.Jev,
            TradeAction.Long,
            pUp5: 0.8);
        var native = new RecordingModel(
            DecisionModelType.XauNative,
            TradeAction.Long,
            pUp5: 0.6);
        var store = new InMemoryDecisionComparisonStore();

        var orchestrator = new PrimaryShadowDecisionOrchestrator(
            jev,
            native,
            store);

        PrimaryShadowEvaluationResult evaluation =
            await orchestrator.EvaluateAsync(
                state,
                new DecisionModelSettings(
                    DecisionModelType.Jev,
                    JevFailurePolicy.StopNewTrades,
                    new ShadowComparisonSettings(
                        true,
                        DecisionModelType.XauNative)),
                CancellationToken.None);

        await store.AttachFutureLabelsAsync(
            evaluation.ComparisonId,
            new DecisionFutureLabels(
                state.MarketStateId,
                "labels-1",
                TimeSpan.FromMinutes(5),
                Censored: false,
                Up5First: true,
                Down5First: false,
                Up10First: null,
                Down10First: null),
            CancellationToken.None);

        await store.AttachExecutionOutcomeAsync(
            evaluation.ComparisonId,
            new ExecutedTradeOutcome(
                evaluation.AuthoritativeDecision!.DecisionId,
                NetPnlPrice: 1.25m,
                ClosedAtUtc: state.TimestampUtc.AddMinutes(1)),
            CancellationToken.None);

        DecisionComparisonReport report = DecisionComparisonReportBuilder.Build(
            await store.ReadAllAsync(CancellationToken.None));

        Assert.Equal(2, report.AllState.Count);
        Assert.Single(report.ExecutedTrades);
        Assert.Equal(
            DecisionModelType.Jev,
            report.ExecutedTrades[0].Model);
        Assert.Equal(1, report.ExecutedTrades[0].TradeCount);
    }

    private static XauMarketState MakeState()
    {
        DateTimeOffset timestamp = new(
            2026,
            9,
            21,
            0,
            0,
            0,
            TimeSpan.Zero);

        return new XauMarketState(
            ContractVersions.MarketStateV1,
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            timestamp,
            timestamp,
            42,
            "XAUUSD",
            "XAUUSD.G",
            100m,
            100.2m,
            100.1m,
            ContractVersions.FeatureSchemaV1,
            "shadow-test",
            LiquiditySource.None,
            new DataReadiness(
                requiredP0Ready: true,
                tickHistoryReady: true,
                barHistoryReady: true,
                newsDataAvailable: true,
                missingRequirements: []),
            []);
    }

    private sealed class RecordingModel : IXauDecisionModel
    {
        private readonly DecisionModelType _modelType;
        private readonly TradeAction _action;
        private readonly Exception? _failure;
        private readonly double _pUp5;

        public RecordingModel(
            DecisionModelType modelType,
            TradeAction action,
            Exception? failure = null,
            double pUp5 = 0.7)
        {
            _modelType = modelType;
            _action = action;
            _failure = failure;
            _pUp5 = pUp5;
        }

        public string ModelId => _modelType == DecisionModelType.Jev
            ? "jev"
            : "xau-native";

        public string ModelVersion => "test-v1";

        public XauMarketState? LastState { get; private set; }

        public Task<XauDecision> EvaluateAsync(
            XauMarketState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastState = state;

            if (_failure is not null)
            {
                throw _failure;
            }

            return Task.FromResult(
                new XauDecision(
                    ContractVersions.DecisionV1,
                    Guid.NewGuid(),
                    state.MarketStateId,
                    _modelType,
                    _action,
                    0.7,
                    _pUp5,
                    0.3,
                    0.6,
                    0.4,
                    0.2,
                    0.55,
                    0.45,
                    0.1,
                    0.7,
                    null,
                    null,
                    null,
                    null,
                    ModelId,
                    ModelVersion,
                    state.FeatureSchemaVersion,
                    state.TimestampUtc,
                    TimeSpan.Zero));
        }
    }

    private sealed class RecordingTradeSink : IAuthoritativeTradeDecisionSink
    {
        public List<AuthoritativeTradeDecisionEnvelope> Envelopes { get; } = [];

        public ValueTask AcceptAsync(
            AuthoritativeTradeDecisionEnvelope envelope,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Envelopes.Add(envelope);
            return ValueTask.CompletedTask;
        }
    }
}
