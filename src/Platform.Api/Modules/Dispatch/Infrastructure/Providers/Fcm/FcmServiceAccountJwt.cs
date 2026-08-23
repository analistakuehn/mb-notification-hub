using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Fcm;

/// <summary>
/// Builds the signed RS256 assertion of the OAuth JWT-bearer grant from the
/// service-account credentials. Hand-rolled on purpose: the flow needs one
/// compact JWT and one form post, which does not justify a provider SDK
/// dependency.
/// </summary>
internal static class FcmServiceAccountJwt
{
    private const string HeaderJson = """{"alg":"RS256","typ":"JWT"}""";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    internal static string CreateAssertion(
        string clientEmail,
        string privateKeyPem,
        string audience,
        string scope,
        DateTimeOffset issuedAt)
    {
        var issuedAtSeconds = issuedAt.ToUnixTimeSeconds();
        var claims = new Claims(
            clientEmail,
            scope,
            audience,
            issuedAtSeconds,
            issuedAtSeconds + (long)Lifetime.TotalSeconds);
        var payloadJson = JsonSerializer.Serialize(claims);

        var signingInput =
            $"{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(HeaderJson))}."
            + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payloadJson));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    private sealed record Claims(
        [property: JsonPropertyName("iss")] string Issuer,
        [property: JsonPropertyName("scope")] string Scope,
        [property: JsonPropertyName("aud")] string Audience,
        [property: JsonPropertyName("iat")] long IssuedAt,
        [property: JsonPropertyName("exp")] long ExpiresAt);
}
