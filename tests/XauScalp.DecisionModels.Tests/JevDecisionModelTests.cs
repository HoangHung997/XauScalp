using XauScalp.Domain;

namespace XauScalp.DecisionModels.Tests;

public sealed class JevDecisionModelTests
{
    [Fact]
    public async Task ValidProviderResponse_MapsToSharedDecisionAndPinsVersion()
    {
        DateTimeOffset now = Utc(12, 0, 1);
        XauMarketState state = MakeState(Utc(12, 0, 0));
        var provider = new FakeProvider(
            request => ValidResponse(request, now));
        var telemetry = new RecordingTelemetry();
        JevDecisionModel model = CreateModel(provider, now, telemetry: telemetry);

        XauDecision decision = await model.EvaluateAsync(
            state,
            CancellationToken.None);

        Assert.Equal(DecisionModelType.Jev, decision.ModelType);
        Assert.Equal(state.MarketStateId, decision.MarketStateId);
        Assert.Equal("jev", decision.ModelId);
        Assert.Equal("provider-v1", decision.ModelVersion);
        Assert.Equal(TradeAction.Long, decision.Action);
        Assert.Equal(0.71, decision.PUp5First, precision: 12);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, provider.RequestIds.Distinct().Count());
        Assert.Contains(
            telemetry.Events,
            item => item.Kind == JevTelemetryKind.AttemptSucceeded);
    }

    [Fact]
    public async Task TransientFailures_RetryBoundedlyWithSameRequestIdentity()
    {
        DateTimeOffset now = Utc(12, 0, 1);
        var provider = new FakeProvider(
            _ => throw new JevProviderUnavailableException("offline"));
        var telemetry = new RecordingTelemetry();
        JevDecisionModel model = CreateModel(
            provider,
            now,
            maxRetries: 2,
            telemetry: telemetry);

        await Assert.ThrowsAsync<JevProviderUnavailableException>(
            async () => await model.EvaluateAsync(
                MakeState(Utc(12, 0, 0)),
                CancellationToken.None));

        Assert.Equal(3, provider.CallCount);
        Assert.Single(provider.RequestIds.Distinct());
        Assert.Equal(
            3,
            telemetry.Events.Count(
                item => item.Kind == JevTelemetryKind.AttemptFailed));
    }

    [Fact]
    public async Task Timeout_IsTransientAndBounded()
    {
        DateTimeOffset now = Utc(12, 0, 1);
        var provider = new FakeProvider(
            async (_, cancellationToken) =>
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
                throw new InvalidOperationException("unreachable");
            });

        JevDecisionModel model = CreateModel(
            provider,
            now,
            maxRetries: 1,
            timeout: TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<JevTimeoutException>(
            async () => await model.EvaluateAsync(
                MakeState(Utc(12, 0, 0)),
                CancellationToken.None));

        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task MalformedProbability_IsRejectedWithoutRetry()
    {
        DateTimeOffset now = Utc(12, 0, 1);
        var provider = new FakeProvider(
            request => ValidResponse(request, now) with
            {
                PUp5First = double.NaN,
            });

        JevDecisionModel model = CreateModel(
            provider,
            now,
            maxRetries: 3);

        JevResponseValidationException exception =
            await Assert.ThrowsAsync<JevResponseValidationException>(
                async () => await model.EvaluateAsync(
                    MakeState(Utc(12, 0, 0)),
                    CancellationToken.None));

        Assert.Equal("invalid-probability", exception.Code);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task ProviderVersionMismatch_IsRejected()
    {
        DateTimeOffset now = Utc(12, 0, 1);
        var provider = new FakeProvider(
            request => ValidResponse(request, now) with
            {
                ProviderModelVersion = "unexpected",
            });

        JevDecisionModel model = CreateModel(provider, now);

        JevResponseValidationException exception =
            await Assert.ThrowsAsync<JevResponseValidationException>(
                async () => await model.EvaluateAsync(
                    MakeState(Utc(12, 0, 0)),
                    CancellationToken.None));

        Assert.Equal("provider-version-mismatch", exception.Code);
    }

    [Fact]
    public async Task StaleStateAndStaleResponse_FailClosed()
    {
        DateTimeOffset now = Utc(12, 0, 10);
        JevDecisionModel stateModel = CreateModel(
            new FakeProvider(request => ValidResponse(request, now)),
            now,
            maxAge: TimeSpan.FromSeconds(2));

        JevInputException stateFailure =
            await Assert.ThrowsAsync<JevInputException>(
                async () => await stateModel.EvaluateAsync(
                    MakeState(Utc(12, 0, 0)),
                    CancellationToken.None));

        Assert.Equal("state-stale", stateFailure.Code);

        XauMarketState freshState = MakeState(
            now.AddMilliseconds(-250));
        var staleProvider = new FakeProvider(
            request => ValidResponse(
                request,
                now.AddSeconds(-1)));
        JevDecisionModel responseModel = CreateModel(
            staleProvider,
            now,
            maxAge: TimeSpan.FromMilliseconds(500));

        JevResponseValidationException responseFailure =
            await Assert.ThrowsAsync<JevResponseValidationException>(
                async () => await responseModel.EvaluateAsync(
                    freshState,
                    CancellationToken.None));

        Assert.Equal("response-stale", responseFailure.Code);
    }

    [Fact]
    public async Task CircuitBreaker_StopsProviderCallsAfterThreshold()
    {
        DateTimeOffset now = Utc(12, 0, 1);
        var provider = new FakeProvider(
            _ => throw new JevProviderUnavailableException("offline"));
        JevDecisionModel model = CreateModel(
            provider,
            now,
            maxRetries: 0,
            circuitThreshold: 1);

        await Assert.ThrowsAsync<JevProviderUnavailableException>(
            async () => await model.EvaluateAsync(
                MakeState(Utc(12, 0, 0)),
                CancellationToken.None));

        await Assert.ThrowsAsync<JevCircuitOpenException>(
            async () => await model.EvaluateAsync(
                MakeState(Utc(12, 0, 0)),
                CancellationToken.None));

        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task StopNewTradesPolicy_ReturnsNoDecisionOnProviderOutage()
    {
        DateTimeOffset now = Utc(12, 0, 1);
        var provider = new FakeProvider(
            _ => throw new JevProviderUnavailableException("offline"));
        var telemetry = new RecordingTelemetry();
        JevDecisionModel jev = CreateModel(
            provider,
            now,
            maxRetries: 0,
            telemetry: telemetry);
        var coordinator = new JevDecisionCoordinator(
            jev,
            new StubDecisionModel(
                DecisionModelType.XauNative,
                "native-v1"),
            telemetry);

        DecisionEvaluationResult result = await coordinator.EvaluateAsync(
            MakeState(Utc(12, 0, 0)),
            JevFailurePolicy.StopNewTrades,
            CancellationToken.None);

        Assert.True(result.StopNewTrades);
        Assert.Null(result.Decision);
        Assert.Null(result.Source);
        Assert.Contains(
            telemetry.Events,
            item => item.Kind == JevTelemetryKind.StopNewTrades);
    }

    [Fact]
    public async Task ExplicitFallbackPolicy_UsesOnlyXauNativeAndMarksSource()
    {
        DateTimeOffset now = Utc(12, 0, 1);
        var provider = new FakeProvider(
            _ => throw new JevProviderUnavailableException("offline"));
        var telemetry = new RecordingTelemetry();
        JevDecisionModel jev = CreateModel(
            provider,
            now,
            maxRetries: 0,
            telemetry: telemetry);
        var native = new StubDecisionModel(
            DecisionModelType.XauNative,
            "native-v1");
        var coordinator = new JevDecisionCoordinator(jev, native, telemetry);

        DecisionEvaluationResult result = await coordinator.EvaluateAsync(
            MakeState(Utc(12, 0, 0)),
            JevFailurePolicy.FallbackToXauNative,
            CancellationToken.None);

        Assert.False(result.StopNewTrades);
        Assert.Equal(
            DecisionEvaluationSource.XauNativeFallback,
            result.Source);
        Assert.Equal(
            DecisionModelType.XauNative,
            result.Decision!.ModelType);
        Assert.Equal(1, native.CallCount);
        Assert.Contains(
            telemetry.Events,
            item => item.Kind == JevTelemetryKind.FallbackSucceeded);
    }

    [Fact]
    public async Task ExternalCancellation_DoesNotFallback()
    {
        DateTimeOffset now = Utc(12, 0, 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var provider = new FakeProvider(
            request => ValidResponse(request, now));
        var native = new StubDecisionModel(
            DecisionModelType.XauNative,
            "native-v1");
        var coordinator = new JevDecisionCoordinator(
            CreateModel(provider, now),
            native);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await coordinator.EvaluateAsync(
                MakeState(Utc(12, 0, 0)),
                JevFailurePolicy.FallbackToXauNative,
                cts.Token));

        Assert.Equal(0, native.CallCount);
    }

    [Fact]
    public void SecretWrapper_IsRedacted()
    {
        var secret = new JevSecret("super-secret-key");

        Assert.Equal("***", secret.ToString());
        Assert.Equal("super-secret-key", secret.DangerousReveal());
    }

    private static JevDecisionModel CreateModel(
        FakeProvider provider,
        DateTimeOffset now,
        int maxRetries = 1,
        TimeSpan? timeout = null,
        TimeSpan? maxAge = null,
        int circuitThreshold = 3,
        RecordingTelemetry? telemetry = null)
    {
        return new JevDecisionModel(
            provider,
            new FakeSecretProvider(),
            new JevDecisionModelOptions(
                providerModelId: "jev-provider",
                providerModelVersion: "provider-v1",
                secretReference: "xauscalp/jev",
                attemptTimeout: timeout ?? TimeSpan.FromSeconds(1),
                maxResponseAge: maxAge ?? TimeSpan.FromSeconds(5),
                maxRetries,
                circuitThreshold,
                circuitBreakerOpenDuration: TimeSpan.FromMinutes(1)),
            new FixedTimeProvider(now),
            telemetry);
    }

    private static JevProviderResponse ValidResponse(
        JevProviderRequest request,
        DateTimeOffset evaluatedAt)
    {
        return new JevProviderResponse(
            JevDecisionModel.ResponseSchemaVersion,
            request.MarketStateId,
            TradeAction.Long,
            0.72,
            0.71,
            0.29,
            0.61,
            0.39,
            0.20,
            0.62,
            0.38,
            0.15,
            0.75,
            null,
            null,
            null,
            null,
            request.ProviderModelId,
            request.ProviderModelVersion,
            request.FeatureSchemaVersion,
            evaluatedAt);
    }

    private static XauMarketState MakeState(DateTimeOffset timestamp)
    {
        NumericFeatureValue[] features =
        [
            new(
                "Return1s",
                0.2,
                "price",
                isAvailable: true,
                timestamp,
                unavailableReason: null),
            new(
                "SpreadAtrRatio",
                0.1,
                "ratio",
                isAvailable: true,
                timestamp,
                unavailableReason: null),
        ];

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
            "jev-test",
            LiquiditySource.None,
            new DataReadiness(
                requiredP0Ready: true,
                tickHistoryReady: true,
                barHistoryReady: true,
                newsDataAvailable: true,
                missingRequirements: []),
            features);
    }

    private static DateTimeOffset Utc(int hour, int minute, int second)
    {
        return new DateTimeOffset(
            2026,
            9,
            19,
            hour,
            minute,
            second,
            TimeSpan.Zero);
    }

    private sealed class FakeProvider : IJevProviderClient
    {
        private readonly Func<
            JevProviderRequest,
            CancellationToken,
            Task<JevProviderResponse>> _handler;

        public FakeProvider(Func<JevProviderRequest, JevProviderResponse> handler)
            : this(
                (request, _) => Task.FromResult(handler(request)))
        {
        }

        public FakeProvider(
            Func<
                JevProviderRequest,
                CancellationToken,
                Task<JevProviderResponse>> handler)
        {
            _handler = handler;
        }

        public int CallCount { get; private set; }

        public List<Guid> RequestIds { get; } = [];

        public async Task<JevProviderResponse> EvaluateAsync(
            JevProviderRequest request,
            JevSecret credential,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestIds.Add(request.RequestId);
            Assert.Equal("super-secret-key", credential.DangerousReveal());

            return await _handler(request, cancellationToken);
        }
    }

    private sealed class FakeSecretProvider : IJevSecretProvider
    {
        public ValueTask<JevSecret> GetSecretAsync(
            string secretReference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("xauscalp/jev", secretReference);
            return ValueTask.FromResult(
                new JevSecret("super-secret-key"));
        }
    }

    private sealed class RecordingTelemetry : IJevTelemetrySink
    {
        public List<JevTelemetryEvent> Events { get; } = [];

        public void Record(JevTelemetryEvent telemetryEvent)
        {
            Events.Add(telemetryEvent);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class StubDecisionModel : IXauDecisionModel
    {
        private readonly DecisionModelType _modelType;

        public StubDecisionModel(
            DecisionModelType modelType,
            string modelVersion)
        {
            _modelType = modelType;
            ModelVersion = modelVersion;
        }

        public string ModelId => _modelType == DecisionModelType.Jev
            ? "jev"
            : "xau-native";

        public string ModelVersion { get; }

        public int CallCount { get; private set; }

        public Task<XauDecision> EvaluateAsync(
            XauMarketState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;

            return Task.FromResult(
                new XauDecision(
                    ContractVersions.DecisionV1,
                    Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
                    state.MarketStateId,
                    _modelType,
                    TradeAction.Wait,
                    0.5,
                    0.5,
                    0.5,
                    0.5,
                    0.5,
                    0.5,
                    0.5,
                    0.5,
                    0.5,
                    0.5,
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
}
