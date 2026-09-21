using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XauScalp.Domain;

namespace XauScalp.DecisionModels.Tests;

public sealed class JevHttpProviderClientTests
{
    [Fact]
    public async Task EvaluateAsync_PostsTypedJsonWithCredentialAndParsesResponse()
    {
        JevProviderRequest request = Request();
        JevProviderResponse response = Response(request);

        var handler = new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    Serialize(response),
                    Encoding.UTF8,
                    "application/json"),
            });

        using var client = new HttpClient(handler);
        var provider = new JevHttpProviderClient(
            client,
            new JevHttpProviderClientOptions(
                new Uri("https://jev.example.test/evaluate")));

        JevProviderResponse actual = await provider.EvaluateAsync(
            request,
            new JevSecret("unit-test-secret"),
            CancellationToken.None);

        Assert.Equal(request.MarketStateId, actual.MarketStateId);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("unit-test-secret", handler.AuthorizationParameter);
        Assert.NotNull(handler.RequestBody);

        using JsonDocument sent = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(
            request.MarketStateId.ToString(),
            sent.RootElement.GetProperty("marketStateId").GetString());
        Assert.Equal(
            request.ProviderModelVersion,
            sent.RootElement.GetProperty("providerModelVersion").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task TransientHttpStatus_IsProviderUnavailable(
        HttpStatusCode statusCode)
    {
        var handler = new RecordingHandler(
            _ => new HttpResponseMessage(statusCode));

        using var client = new HttpClient(handler);
        var provider = new JevHttpProviderClient(
            client,
            new JevHttpProviderClientOptions(
                new Uri("https://jev.example.test/evaluate")));

        await Assert.ThrowsAsync<JevProviderUnavailableException>(
            () => provider.EvaluateAsync(
                Request(),
                new JevSecret("unit-test-secret"),
                CancellationToken.None));
    }

    [Fact]
    public async Task PermanentHttpReject_DoesNotBecomeTransientRetrySignal()
    {
        var handler = new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest));

        using var client = new HttpClient(handler);
        var provider = new JevHttpProviderClient(
            client,
            new JevHttpProviderClientOptions(
                new Uri("https://jev.example.test/evaluate")));

        JevResponseValidationException exception =
            await Assert.ThrowsAsync<JevResponseValidationException>(
                () => provider.EvaluateAsync(
                    Request(),
                    new JevSecret("unit-test-secret"),
                    CancellationToken.None));

        Assert.Equal("provider-http-rejected", exception.Code);
    }

    [Fact]
    public async Task MalformedOrOversizedResponse_FailsValidation()
    {
        var malformedHandler = new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{not-json"),
            });

        using (var malformedClient = new HttpClient(malformedHandler))
        {
            var provider = new JevHttpProviderClient(
                malformedClient,
                new JevHttpProviderClientOptions(
                    new Uri("https://jev.example.test/evaluate")));

            JevResponseValidationException malformed =
                await Assert.ThrowsAsync<JevResponseValidationException>(
                    () => provider.EvaluateAsync(
                        Request(),
                        new JevSecret("unit-test-secret"),
                        CancellationToken.None));

            Assert.Equal("provider-malformed-json", malformed.Code);
        }

        var oversizedHandler = new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[101]),
            });

        using var oversizedClient = new HttpClient(oversizedHandler);
        var oversizedProvider = new JevHttpProviderClient(
            oversizedClient,
            new JevHttpProviderClientOptions(
                new Uri("https://jev.example.test/evaluate"),
                maxResponseBytes: 100));

        JevResponseValidationException oversized =
            await Assert.ThrowsAsync<JevResponseValidationException>(
                () => oversizedProvider.EvaluateAsync(
                    Request(),
                    new JevSecret("unit-test-secret"),
                    CancellationToken.None));

        Assert.Equal("provider-response-too-large", oversized.Code);
    }

    [Fact]
    public void RemotePlainHttp_IsRejectedUnlessExplicitlyAllowed()
    {
        Assert.Throws<ArgumentException>(
            () => new JevHttpProviderClientOptions(
                new Uri("http://jev.example.test/evaluate")));

        var explicitInsecure = new JevHttpProviderClientOptions(
            new Uri("http://jev.example.test/evaluate"),
            allowInsecureHttp: true);

        var loopback = new JevHttpProviderClientOptions(
            new Uri("http://127.0.0.1:8080/evaluate"));

        Assert.True(explicitInsecure.AllowInsecureHttp);
        Assert.True(loopback.Endpoint.IsLoopback);
    }

    [Fact]
    public async Task ReferenceSecretProvider_RoutesEnvReferenceWithoutPersistingSecret()
    {
        string variable = "XAUSCALP_TEST_" + Guid.NewGuid().ToString("N");
        string reference = "env:" + variable;
        string? previous = Environment.GetEnvironmentVariable(variable);

        try
        {
            Environment.SetEnvironmentVariable(variable, "secret-from-env");

            var provider = new ReferenceJevSecretProvider(
                environment: new EnvironmentJevSecretProvider(),
                credentialManager: new RejectingSecretProvider());

            JevSecret secret = await provider.GetSecretAsync(
                reference,
                CancellationToken.None);

            Assert.Equal("secret-from-env", secret.DangerousReveal());
            Assert.Equal("***", secret.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public async Task MissingEnvironmentSecret_FailsClosed()
    {
        string variable = "XAUSCALP_MISSING_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, null);

        var provider = new EnvironmentJevSecretProvider();

        await Assert.ThrowsAsync<JevSecretUnavailableException>(
            async () => await provider.GetSecretAsync(
                "env:" + variable,
                CancellationToken.None));
    }

    private static JevProviderRequest Request()
    {
        return new JevProviderRequest(
            JevDecisionModel.RequestSchemaVersion,
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
            new DateTimeOffset(
                2026,
                9,
                21,
                12,
                0,
                0,
                TimeSpan.Zero),
            42,
            "XAUUSD",
            "XAUUSD.G",
            2500m,
            2500.2m,
            2500.1m,
            ContractVersions.FeatureSchemaV1,
            "jev",
            "jev-test-v1",
            []);
    }

    private static JevProviderResponse Response(
        JevProviderRequest request)
    {
        return new JevProviderResponse(
            JevDecisionModel.ResponseSchemaVersion,
            request.MarketStateId,
            TradeAction.Wait,
            0.7,
            0.6,
            0.4,
            0.55,
            0.45,
            0.2,
            0.5,
            0.3,
            0.2,
            0.7,
            PHold: 0.8,
            PExitNow: 0.1,
            PTp5FromHere: 0.5,
            PTp10FromHere: 0.3,
            request.ProviderModelId,
            request.ProviderModelVersion,
            request.FeatureSchemaVersion,
            request.StateTimestampUtc);
    }

    private static string Serialize(JevProviderResponse response)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Serialize(response, options);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            HttpResponseMessage> _responseFactory;

        public RecordingHandler(
            Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            RequestBody = request.Content is null
                ? null
                : await request.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

            return _responseFactory(request);
        }
    }

    private sealed class RejectingSecretProvider : IJevSecretProvider
    {
        public ValueTask<JevSecret> GetSecretAsync(
            string secretReference,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(
                "Credential provider must not be called for env: references.");
        }
    }
}
