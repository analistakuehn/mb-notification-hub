using System.Net;
using System.Net.Http.Headers;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace NotificationHub.IntegrationTests;

/// <summary>
/// The OpenAPI document is the machine contract of the API, so it is reachable
/// in every environment and it never answers an anonymous caller.
/// <para>
/// Both halves need pinning. The document describes the whole administrative
/// surface, including template authoring and publication, so serving it
/// anonymously would publish that map; and the route it is served from carries
/// no authorization metadata from the framework, which means its protection is
/// a decision this host makes and a future package version could silently
/// change. The reachability half matters just as much: the producer
/// integration guide sends producers to this URL to generate their clients,
/// and the decision not to ship a shared client library rests on it, so
/// narrowing the route to Development would break a published contract without
/// any other test noticing.
/// </para>
/// </summary>
public sealed class OpenApiDocumentExposureTests(TestApplicationFactory factory)
    : IClassFixture<TestApplicationFactory>
{
    private const string DocumentRoute = "/openapi/v1.json";

    [Fact]
    public async Task An_anonymous_caller_never_reads_the_document()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(DocumentRoute);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_authenticated_caller_reads_the_versioned_surface()
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            DevelopmentToken.Issue());

        HttpResponseMessage response = await client.GetAsync(DocumentRoute);
        var document = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        document.ShouldContain("\"openapi\"");
        document.ShouldContain("/v1/notifications");
    }

    /// <summary>
    /// Signs with the committed development key, which the host accepts only in
    /// Development. Any authenticated principal is enough here on purpose: the
    /// contract carries no role requirement beyond authentication, and a token
    /// without roles proves it.
    /// </summary>
    private static class DevelopmentToken
    {
        private const string Issuer = "notification-hub-dev-only";
        private const string Audience = "notification-hub";
        private const string SigningKey =
            "ZGV2LW9ubHkgc2lnbmluZyBrZXkgLSBuZXZlciB1c2Ugb3V0c2lkZSBsb2NhbGhvc3Q=";

        internal static string Issue()
            => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = Audience,
                Expires = DateTime.UtcNow.AddMinutes(10),
                Claims = new Dictionary<string, object> { ["sub"] = "openapi-reader" },
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(Convert.FromBase64String(SigningKey)),
                    SecurityAlgorithms.HmacSha256),
            });
    }
}
