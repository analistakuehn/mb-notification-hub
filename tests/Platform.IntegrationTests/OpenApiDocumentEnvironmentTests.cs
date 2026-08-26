using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace NotificationHub.IntegrationTests;

/// <summary>
/// The document route carries no environment gate. Two guards in the host do
/// refuse to boot outside Development, and both exist to contain a committed
/// secret; neither is a statement about which routes a deployed API answers.
/// This pins the difference, because the producer integration guide publishes
/// this URL as the machine contract and a deployed API has to honour it.
/// </summary>
public sealed class OpenApiDocumentEnvironmentTests
{
    private const string Issuer = "notification-hub-tests";
    private const string Audience = "notification-hub";
    private const string SigningKey =
        "dGVzdC1vbmx5IHNpZ25pbmcga2V5IGZvciB0aGUgb3BlbmFwaSBlbnZpcm9ubWVudCB0ZXN0";

    [Fact]
    public async Task The_document_answers_an_authenticated_caller_outside_development()
    {
        using var factory = new ProductionHostFactory();
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", IssueToken());

        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_document_refuses_an_anonymous_caller_outside_development()
    {
        using var factory = new ProductionHostFactory();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static string IssueToken()
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

    /// <summary>
    /// Boots in Production with a signing key minted for this test, never the
    /// committed development one: the host refuses to boot with that key
    /// outside Development, which is the guard this class is careful not to
    /// confuse with a route gate.
    /// </summary>
    private sealed class ProductionHostFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration)
                => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:Schemes:Bearer:ValidIssuer"] = Issuer,
                    ["Authentication:Schemes:Bearer:ValidAudiences:0"] = Audience,
                    ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = Issuer,
                    ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = SigningKey,
                    ["Platform:Cryptography:Envelope:KeyId"] = "openapi-environment-test",
                    ["Platform:Cryptography:Envelope:MasterKey"] =
                        "Y2hhdmUtbWVzdHJhIGRlIHRlc3RlIGRlIGFtYmllbnRlIHBhcmEgbyBvcGVuYXBp",
                    ["Modules:TemplateManagement:Cache:Redis:ConnectionString"] = "localhost:6379",
                    ["Modules:TemplateManagement:Cache:Redis:InstanceName"] = "integration-tests:",
                    ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] =
                        "Host=localhost;Database=integration_tests;Username=test",
                }));
        }
    }
}
