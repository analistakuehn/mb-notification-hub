using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class CanonicalSchemaRoundTripTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Publishes_a_version_whose_schema_number_literals_survive_the_database_round_trip()
    {
        HttpClient author = fixture.CreateAuthorClient("author-canon-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-canon-1");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey(), defaultLocale: "pt-BR");
        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        etag = await TemplateApi.PutContentAsync(author, key, version, "email/pt-BR", new
        {
            subject = "Pedido {{ orderId }}",
            body = "<p>Pedido {{ orderId }} atualizado.</p>",
            bodyText = "Pedido {{ orderId }} atualizado.",
        }, etag);

        // Raw JSON on purpose: 1e2, -0, 1.50 and 0.1e-3 are literals a jsonb
        // column would rewrite on round trip, while the content hash covers
        // the submitted bytes.
        const string schema = """
            {"type":"object","properties":{"orderId":{"type":"string"}},"required":["orderId"],"examples":[1e2,-0,1.50,0.1e-3]}
            """;
        var schemaRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"/v1/templates/{key}/versions/{version}/variables-schema")
        {
            Content = new StringContent(schema, Encoding.UTF8, "application/json"),
        };
        schemaRequest.Headers.TryAddWithoutValidation("If-Match", etag);
        HttpResponseMessage schemaResponse = await author.SendAsync(schemaRequest);
        schemaResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The publish request runs in a fresh scope with a fresh DbContext, so
        // it reloads the schema from the database and re-verifies the hash
        // before approving anything.
        HttpResponseMessage publishResponse = await publisher.PostAsync(
            $"/v1/templates/{key}/versions/{version}/publish", content: null);

        publishResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement published = await TemplateApi.ReadJsonAsync(publishResponse);
        published.GetProperty("status").GetString().ShouldBe("published");

        await fixture.ExecuteDbAsync(async db =>
        {
            var stored = await db.TemplateVersions
                .AsNoTracking()
                .WhereTemplateKey(TemplateKey.Trusted(key))
                .Where(candidate => candidate.Version == version)
                .Select(candidate => candidate.VariablesSchemaJson)
                .SingleAsync();
            stored.ShouldNotBeNull();
            stored.ShouldContain("1e2");
            stored.ShouldContain("-0");
            stored.ShouldContain("1.50");
            stored.ShouldContain("0.1e-3");
        });
    }
}
