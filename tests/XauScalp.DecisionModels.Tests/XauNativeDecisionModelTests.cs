using System.Text.Json;
using System.Text.Json.Serialization;
using XauScalp.Domain;

namespace XauScalp.DecisionModels.Tests;

public sealed class XauNativeDecisionModelTests
{
    [Fact]
    public async Task FrozenOfflineVector_MatchesCSharpInference()
    {
        LoadedNativeArtifact loaded = LoadFixture();
        FrozenParityVector vector = ReadParityVector();
        var model = new XauNativeDecisionModel(loaded);
        XauMarketState state = MakeState(
            vector.FeatureSchemaVersion,
            vector.Features);

        XauDecision decision = await model.EvaluateAsync(
            state,
            CancellationToken.None);

        Assert.Equal(DecisionModelType.XauNative, decision.ModelType);
        Assert.Equal(TradeAction.Long, decision.Action);
        Assert.Equal(
            vector.Expected.ActionProbability,
            decision.ActionProbability,
            precision: 12);
        Assert.Equal(
            vector.Expected.PUp5First,
            decision.PUp5First,
            precision: 12);
        Assert.Equal(
            vector.Expected.PDown5First,
            decision.PDown5First,
            precision: 12);
        Assert.Equal(
            vector.Expected.PUp10First,
            decision.PUp10First,
            precision: 12);
        Assert.Equal(
            vector.Expected.PDown10First,
            decision.PDown10First,
            precision: 12);
        Assert.Equal(
            vector.Expected.PAdverseBarrierFirst,
            decision.PAdverseBarrierFirst,
            precision: 12);
        Assert.Equal(
            vector.Expected.PContinuation,
            decision.PContinuation,
            precision: 12);
        Assert.Equal(
            vector.Expected.PReversal,
            decision.PReversal,
            precision: 12);
        Assert.Equal(
            vector.Expected.PFalseBreak,
            decision.PFalseBreak,
            precision: 12);
        Assert.Equal(
            vector.Expected.Confidence,
            decision.Confidence,
            precision: 12);

        Assert.Equal("xau-native", decision.ModelId);
        Assert.Equal("fixture-v1", decision.ModelVersion);
        Assert.Equal(
            ContractVersions.FeatureSchemaV1,
            decision.FeatureSchemaVersion);
    }

    [Fact]
    public void ArtifactHash_IsVerifiedAgainstManifest()
    {
        LoadedNativeArtifact loaded = LoadFixture();

        Assert.Equal(
            "43ceff0e62b1c5f0c5950df3b3257feb68bf1905fe266559ed12fdf8ffc9a4d9",
            loaded.ArtifactSha256);
        Assert.Equal(
            loaded.ArtifactSha256,
            loaded.Manifest.ArtifactSha256);
    }

    [Fact]
    public void TamperedArtifact_IsRejected()
    {
        string artifactPath = Fixture(
            "xau-native-fixture.json");
        string manifestPath = Fixture(
            "xau-native-fixture.artifact-manifest.json");

        string tempArtifact = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                tempArtifact,
                File.ReadAllText(artifactPath) + " ");

            Assert.Throws<InvalidDataException>(
                () => NativeArtifactLoader.Load(
                    tempArtifact,
                    manifestPath));
        }
        finally
        {
            File.Delete(tempArtifact);
        }
    }

    [Fact]
    public async Task MissingRequiredFeature_FailsClosed()
    {
        LoadedNativeArtifact loaded = LoadFixture();
        FrozenParityVector vector = ReadParityVector();
        Dictionary<string, double> missing =
            new(vector.Features, StringComparer.Ordinal);
        missing.Remove("Velocity1s");

        var model = new XauNativeDecisionModel(loaded);

        await Assert.ThrowsAsync<NativeModelInputException>(
            async () => await model.EvaluateAsync(
                MakeState(vector.FeatureSchemaVersion, missing),
                CancellationToken.None));
    }

    [Fact]
    public async Task SchemaMismatch_FailsClosed()
    {
        LoadedNativeArtifact loaded = LoadFixture();
        FrozenParityVector vector = ReadParityVector();
        var model = new XauNativeDecisionModel(loaded);

        await Assert.ThrowsAsync<NativeModelInputException>(
            async () => await model.EvaluateAsync(
                MakeState("unexpected-schema", vector.Features),
                CancellationToken.None));
    }

    [Fact]
    public async Task NotReadyState_FailsClosed()
    {
        LoadedNativeArtifact loaded = LoadFixture();
        FrozenParityVector vector = ReadParityVector();
        XauMarketState ready = MakeState(
            vector.FeatureSchemaVersion,
            vector.Features);

        var notReady = new XauMarketState(
            ready.ContractVersion,
            ready.MarketStateId,
            ready.TimestampUtc,
            ready.BrokerTimestamp,
            ready.SequenceId,
            ready.Symbol,
            ready.BrokerSymbol,
            ready.Bid,
            ready.Ask,
            ready.Mid,
            ready.FeatureSchemaVersion,
            ready.DataSourceId,
            ready.LiquiditySource,
            new DataReadiness(
                requiredP0Ready: false,
                tickHistoryReady: false,
                barHistoryReady: true,
                newsDataAvailable: true,
                missingRequirements: ["tick history"]),
            ready.Features);

        var model = new XauNativeDecisionModel(loaded);

        await Assert.ThrowsAsync<NativeModelInputException>(
            async () => await model.EvaluateAsync(
                notReady,
                CancellationToken.None));
    }

    [Fact]
    public async Task DecisionIdentity_IsReproducibleForSameStateAndArtifact()
    {
        LoadedNativeArtifact loaded = LoadFixture();
        FrozenParityVector vector = ReadParityVector();
        XauMarketState state = MakeState(
            vector.FeatureSchemaVersion,
            vector.Features);
        var model = new XauNativeDecisionModel(loaded);

        XauDecision first = await model.EvaluateAsync(
            state,
            CancellationToken.None);
        XauDecision second = await model.EvaluateAsync(
            state,
            CancellationToken.None);

        Assert.Equal(first.DecisionId, second.DecisionId);
    }

    [Fact]
    public async Task InferenceLatencyBenchmark_ProducesMeasuredDistribution()
    {
        LoadedNativeArtifact loaded = LoadFixture();
        FrozenParityVector vector = ReadParityVector();
        XauMarketState state = MakeState(
            vector.FeatureSchemaVersion,
            vector.Features);
        var model = new XauNativeDecisionModel(loaded);

        NativeInferenceBenchmarkResult result =
            await NativeInferenceBenchmark.MeasureAsync(
                model,
                state,
                iterations: 100);

        Assert.Equal(100, result.Iterations);
        Assert.True(result.Average >= TimeSpan.Zero);
        Assert.True(result.P50 >= TimeSpan.Zero);
        Assert.True(result.P95 >= result.P50);
        Assert.True(result.Maximum >= result.P95);
    }

    private static LoadedNativeArtifact LoadFixture()
    {
        return NativeArtifactLoader.Load(
            Fixture("xau-native-fixture.json"),
            Fixture("xau-native-fixture.artifact-manifest.json"));
    }

    private static FrozenParityVector ReadParityVector()
    {
        string json = File.ReadAllText(
            Fixture("xau-native-parity-vector.json"));

        return JsonSerializer.Deserialize<FrozenParityVector>(
            json,
            FixtureJsonOptions())
            ?? throw new InvalidDataException(
                "Frozen native parity vector is invalid.");
    }

    private static XauMarketState MakeState(
        string featureSchemaVersion,
        IReadOnlyDictionary<string, double> features)
    {
        DateTimeOffset timestamp = new(
            2026,
            9,
            19,
            12,
            0,
            0,
            TimeSpan.Zero);

        NumericFeatureValue[] values = features
            .Select(
                pair => new NumericFeatureValue(
                    pair.Key,
                    pair.Value,
                    "fixture",
                    isAvailable: true,
                    timestamp,
                    unavailableReason: null))
            .ToArray();

        return new XauMarketState(
            ContractVersions.MarketStateV1,
            Guid.Parse(
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            timestamp,
            timestamp,
            sequenceId: 42,
            symbol: "XAUUSD",
            brokerSymbol: "XAUUSD.G",
            bid: 100m,
            ask: 100.2m,
            mid: 100.1m,
            featureSchemaVersion,
            dataSourceId: "native-test",
            LiquiditySource.None,
            new DataReadiness(
                requiredP0Ready: true,
                tickHistoryReady: true,
                barHistoryReady: true,
                newsDataAvailable: true,
                missingRequirements: []),
            values);
    }

    private static string Fixture(string fileName)
    {
        return Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            fileName);
    }

    private static JsonSerializerOptions FixtureJsonOptions()
    {
        var options = new JsonSerializerOptions(
            JsonSerializerDefaults.Web);
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }

    private sealed record FrozenParityVector
    {
        public string FeatureSchemaVersion { get; init; } =
            string.Empty;

        public Dictionary<string, double> Features { get; init; } =
            new(StringComparer.Ordinal);

        public FrozenExpected Expected { get; init; } = new();
    }

    private sealed record FrozenExpected
    {
        public TradeAction Action { get; init; }

        public double ActionProbability { get; init; }

        public double PUp5First { get; init; }

        public double PDown5First { get; init; }

        public double PUp10First { get; init; }

        public double PDown10First { get; init; }

        public double PAdverseBarrierFirst { get; init; }

        public double PContinuation { get; init; }

        public double PReversal { get; init; }

        public double PFalseBreak { get; init; }

        public double Confidence { get; init; }
    }
}
