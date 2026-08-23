using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Fcm;

namespace NotificationHub.UnitTests.Dispatch;

public sealed class FcmServiceAccountJwtTests
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Builds_a_compact_jwt_with_the_grant_claims()
    {
        using var rsa = RSA.Create(2048);

        var assertion = FcmServiceAccountJwt.CreateAssertion(
            "svc@project.iam.gserviceaccount.com",
            rsa.ExportPkcs8PrivateKeyPem(),
            "https://oauth2.googleapis.com/token",
            "https://www.googleapis.com/auth/firebase.messaging",
            IssuedAt);

        var parts = assertion.Split('.');
        parts.Length.ShouldBe(3);

        using JsonDocument header = DecodeJson(parts[0]);
        header.RootElement.GetProperty("alg").GetString().ShouldBe("RS256");
        header.RootElement.GetProperty("typ").GetString().ShouldBe("JWT");

        using JsonDocument payload = DecodeJson(parts[1]);
        payload.RootElement.GetProperty("iss").GetString()
            .ShouldBe("svc@project.iam.gserviceaccount.com");
        payload.RootElement.GetProperty("scope").GetString()
            .ShouldBe("https://www.googleapis.com/auth/firebase.messaging");
        payload.RootElement.GetProperty("aud").GetString()
            .ShouldBe("https://oauth2.googleapis.com/token");
        var issuedAtSeconds = IssuedAt.ToUnixTimeSeconds();
        payload.RootElement.GetProperty("iat").GetInt64().ShouldBe(issuedAtSeconds);
        payload.RootElement.GetProperty("exp").GetInt64().ShouldBe(issuedAtSeconds + 3600);
    }

    [Fact]
    public void Signs_the_header_and_payload_with_the_service_account_key()
    {
        using var rsa = RSA.Create(2048);

        var assertion = FcmServiceAccountJwt.CreateAssertion(
            "svc@project.iam.gserviceaccount.com",
            rsa.ExportPkcs8PrivateKeyPem(),
            "https://oauth2.googleapis.com/token",
            "scope",
            IssuedAt);

        var parts = assertion.Split('.');
        var signedBytes = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
        var signature = Base64Url.DecodeFromChars(parts[2]);

        rsa.VerifyData(signedBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .ShouldBeTrue();
    }

    private static JsonDocument DecodeJson(string base64UrlSegment)
        => JsonDocument.Parse(Base64Url.DecodeFromChars(base64UrlSegment));
}
