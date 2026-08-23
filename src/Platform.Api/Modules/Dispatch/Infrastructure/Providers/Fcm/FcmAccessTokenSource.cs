using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Fcm;

/// <summary>
/// Caches the service-account access token until shortly before expiry and
/// renews it single-flight. Token acquisition is idempotent at the endpoint,
/// so its HTTP client is the only one in this module allowed to retry.
/// </summary>
internal sealed class FcmAccessTokenSource(
    IHttpClientFactory httpClientFactory,
    IOptions<FcmOptions> options,
    TimeProvider timeProvider,
    ILogger<FcmAccessTokenSource> logger) : IDisposable
{
    internal const string HttpClientName = "dispatch-fcm-token";
    private const string JwtBearerGrantType = "urn:ietf:params:oauth:grant-type:jwt-bearer";
    private static readonly TimeSpan ExpirySafetyMargin = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _renewGate = new(1, 1);
    private CachedToken? _token;

    internal async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        CachedToken? cached = Volatile.Read(ref _token);
        if (cached is not null && !IsExpiring(cached))
        {
            return cached.Value;
        }

        await _renewGate.WaitAsync(cancellationToken);
        try
        {
            cached = Volatile.Read(ref _token);
            if (cached is not null && !IsExpiring(cached))
            {
                return cached.Value;
            }

            CachedToken renewed = await RenewAsync(cancellationToken);
            Volatile.Write(ref _token, renewed);
            return renewed.Value;
        }
        finally
        {
            _renewGate.Release();
        }
    }

    public void Dispose() => _renewGate.Dispose();

    private async Task<CachedToken> RenewAsync(CancellationToken cancellationToken)
    {
        FcmOptions config = options.Value;
        EnsureConfigured(config);

        DateTimeOffset now = timeProvider.GetUtcNow();
        var assertion = FcmServiceAccountJwt.CreateAssertion(
            config.ServiceAccountEmail,
            config.ServiceAccountPrivateKeyPem,
            config.TokenUri,
            config.TokenScope,
            now);

        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = JwtBearerGrantType,
            ["assertion"] = assertion,
        });

        try
        {
            using HttpResponseMessage response = await client.PostAsync(
                new Uri(config.TokenUri), form, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                logger.FcmTokenEndpointRejected(statusCode);
                throw new FcmTokenUnavailableException(
                    $"The OAuth token endpoint answered HTTP {statusCode}.");
            }

            FcmTokenResponse? token = await response.Content
                .ReadFromJsonAsync<FcmTokenResponse>(cancellationToken);
            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken) || token.ExpiresInSeconds <= 0)
            {
                throw new FcmTokenUnavailableException(
                    "The OAuth token endpoint answered without a usable access token.");
            }

            var expiresInSeconds = token.ExpiresInSeconds;
            logger.FcmTokenRenewed(expiresInSeconds);
            return new CachedToken(token.AccessToken, now.AddSeconds(expiresInSeconds));
        }
        catch (HttpRequestException exception)
        {
            logger.FcmTokenEndpointUnreachable(exception);
            throw new FcmTokenUnavailableException("The OAuth token endpoint was unreachable.", exception);
        }
    }

    private bool IsExpiring(CachedToken candidate)
        => timeProvider.GetUtcNow() >= candidate.ExpiresAt - ExpirySafetyMargin;

    private static void EnsureConfigured(FcmOptions config)
    {
        if (string.IsNullOrWhiteSpace(config.ServiceAccountEmail))
        {
            throw new InvalidOperationException(
                $"Missing configuration '{FcmOptions.SectionName}:ServiceAccountEmail'.");
        }

        if (string.IsNullOrWhiteSpace(config.ServiceAccountPrivateKeyPem))
        {
            throw new InvalidOperationException(
                $"Missing configuration '{FcmOptions.SectionName}:ServiceAccountPrivateKeyPem'.");
        }
    }

    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt);
}
