using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XauScalp.DecisionModels;

public sealed record JevHttpProviderClientOptions
{
    public JevHttpProviderClientOptions(
        Uri endpoint,
        string authorizationScheme = "Bearer",
        int maxResponseBytes = 1_048_576,
        bool allowInsecureHttp = false)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "JEV HTTP endpoint must be an absolute URI.",
                nameof(endpoint));
        }

        bool loopback = endpoint.IsLoopback;
        bool secure = string.Equals(
            endpoint.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase);

        if (!secure
            && !(allowInsecureHttp || loopback))
        {
            throw new ArgumentException(
                "JEV HTTP endpoint must use HTTPS unless insecure HTTP is explicitly enabled or the endpoint is loopback.",
                nameof(endpoint));
        }

        if (string.IsNullOrWhiteSpace(authorizationScheme))
        {
            throw new ArgumentException(
                "Authorization scheme is required.",
                nameof(authorizationScheme));
        }

        if (maxResponseBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResponseBytes),
                maxResponseBytes,
                "Maximum response size must be positive.");
        }

        AuthorizationScheme = authorizationScheme.Trim();
        MaxResponseBytes = maxResponseBytes;
        AllowInsecureHttp = allowInsecureHttp;
    }

    public Uri Endpoint { get; }

    public string AuthorizationScheme { get; }

    public int MaxResponseBytes { get; }

    public bool AllowInsecureHttp { get; }
}

public sealed class JevHttpProviderClient : IJevProviderClient
{
    private readonly HttpClient _httpClient;
    private readonly JevHttpProviderClientOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    public JevHttpProviderClient(
        HttpClient httpClient,
        JevHttpProviderClientOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
        };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task<JevProviderResponse> EvaluateAsync(
        JevProviderRequest request,
        JevSecret credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credential);
        cancellationToken.ThrowIfCancellationRequested();

        string json = JsonSerializer.Serialize(request, _jsonOptions);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            _options.Endpoint)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json"),
        };

        message.Headers.Authorization = new AuthenticationHeaderValue(
            _options.AuthorizationScheme,
            credential.DangerousReveal());

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new JevProviderUnavailableException(
                "JEV HTTP provider could not be reached.",
                exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                ThrowHttpFailure(response.StatusCode);
            }

            if (response.Content.Headers.ContentLength is long length
                && length > _options.MaxResponseBytes)
            {
                throw new JevResponseValidationException(
                    "provider-response-too-large",
                    "JEV HTTP provider response exceeds the configured maximum size.");
            }

            byte[] payload = await ReadBoundedAsync(
                response.Content,
                _options.MaxResponseBytes,
                cancellationToken).ConfigureAwait(false);

            try
            {
                JevProviderResponse? value =
                    JsonSerializer.Deserialize<JevProviderResponse>(
                        payload,
                        _jsonOptions);

                return value
                    ?? throw new JevResponseValidationException(
                        "provider-empty-response",
                        "JEV HTTP provider returned an empty JSON response.");
            }
            catch (JsonException exception)
            {
                throw new JevResponseValidationException(
                    "provider-malformed-json",
                    $"JEV HTTP provider returned malformed JSON ({exception.GetType().Name}).");
            }
        }
    }

    private static void ThrowHttpFailure(HttpStatusCode statusCode)
    {
        int numeric = (int)statusCode;

        if (statusCode == HttpStatusCode.RequestTimeout
            || numeric == 429
            || numeric >= 500)
        {
            throw new JevProviderUnavailableException(
                $"JEV HTTP provider returned transient status {numeric}.");
        }

        throw new JevResponseValidationException(
            "provider-http-rejected",
            $"JEV HTTP provider rejected the request with status {numeric}.");
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];

        while (true)
        {
            int read = await stream
                .ReadAsync(chunk, cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new JevResponseValidationException(
                    "provider-response-too-large",
                    "JEV HTTP provider response exceeds the configured maximum size.");
            }

            await buffer
                .WriteAsync(chunk.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        return buffer.ToArray();
    }
}
