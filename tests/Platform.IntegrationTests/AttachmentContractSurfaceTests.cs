using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;
using NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;

namespace NotificationHub.IntegrationTests;

/// <summary>
/// The published surface of the request that carries attachments. There is one
/// write contract and one machine contract: the manifest is a member of the
/// body every producer already sends, and asking for attachments is asking on
/// the same route, behind the same door and against the same budget.
/// </summary>
public sealed class AttachmentContractSurfaceTests(TestApplicationFactory factory)
    : IClassFixture<TestApplicationFactory>
{
    private const string PublishedDocument = "/openapi/v1.json";
    private const string IngestionRoute = "/v1/notifications";

    /// <summary>
    /// A list of opaque references and nothing else: a body that described a
    /// name, a media type or a length would invite a producer to send
    /// properties the released attachment already fixed.
    /// </summary>
    [Fact]
    public async Task The_published_document_names_the_manifest_as_a_list_of_opaque_references()
    {
        JsonObject document = await ReadDocumentAsync(PublishedDocument);

        JsonObject manifest = IngestionBody(document)["properties"]
            .ShouldNotBeNull()
            .AsObject()["attachments"]
            .ShouldNotBeNull()
            .AsObject();

        JsonObject item = manifest["items"].ShouldNotBeNull().AsObject();
        item["type"].ShouldNotBeNull().ToJsonString().ShouldContain("string");
        item.ContainsKey("properties").ShouldBeFalse(
            "O item do manifesto passou a descrever membros, portanto o corpo "
            + "convida o produtor a repetir o que o anexo liberado já fixou.");
    }

    /// <summary>
    /// The manifest is carried by the route every notification is requested
    /// on. A second address for it would be a second write contract, and a
    /// producer would have to choose one before knowing it exists.
    /// </summary>
    [Fact]
    public async Task The_route_that_carries_the_manifest_is_the_route_every_notification_is_requested_on()
    {
        JsonObject document = await ReadDocumentAsync(PublishedDocument);

        var paths = document["paths"]
            .ShouldNotBeNull()
            .AsObject()
            .Select(path => path.Key)
            .ToArray();

        paths.ShouldContain(IngestionRoute);

        // Every versioned address the document declares, read from the paths
        // themselves. A second contract for the same resource arrives as a
        // second version here, whatever it is called, and the routes that
        // carry no version at all are none of this rule's business.
        var versions = paths
            .Select(path => path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(segment => segment is ['v', >= '0' and <= '9', ..])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        versions.ShouldBe(["v1"]);
    }

    /// <summary>
    /// One document, and no second one beside it. A document of its own for
    /// the request that carries attachments would split the surface producers
    /// generate their clients from, and a member added to one of them would
    /// never reach the other.
    /// </summary>
    [Fact]
    public async Task No_second_machine_contract_is_served_beside_the_published_one()
    {
        HttpClient client = AuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/openapi/v2.json");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The request that carries a manifest is guarded exactly as the ingestion
    /// is, because it is the ingestion. Read from the endpoint the host built
    /// and held against the name the module declares, so a route mapped
    /// without the metadata, or with the policy of another operation, fails
    /// here.
    /// </summary>
    [Fact]
    public void The_route_that_carries_the_manifest_requires_the_authorization_the_ingestion_declares()
    {
        RouteEndpoint ingestion = PostEndpoint(IngestionRoute);

        ingestion.Metadata.GetMetadata<IAuthorizeData>()
            .ShouldNotBeNull()
            .Policy
            .ShouldBe(NotificationsAuthorizationSetup.SendPolicyName);
    }

    [Fact]
    public void The_route_that_carries_the_manifest_spends_the_budget_the_ingestion_declares()
    {
        RouteEndpoint ingestion = PostEndpoint(IngestionRoute);

        ingestion.Metadata.GetMetadata<EnableRateLimitingAttribute>()
            .ShouldNotBeNull()
            .PolicyName
            .ShouldBe(NotificationsRateLimitingSetup.PolicyName);
    }

    private RouteEndpoint PostEndpoint(string route)
    {
        // Building a client is what starts the host and materializes the
        // routes; reading the source before that would answer from an empty
        // table and make both rules above pass over nothing.
        using HttpClient client = factory.CreateClient();

        return factory.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            // A group that maps the empty pattern leaves the trailing separator
            // in the raw text, so the route is matched without it.
            .Where(endpoint => endpoint.RoutePattern.RawText?.TrimEnd('/') == route)
            .Single(endpoint => endpoint.Metadata
                .GetMetadata<IHttpMethodMetadata>()!
                .HttpMethods
                .Contains(HttpMethods.Post, StringComparer.Ordinal));
    }

    private static JsonObject IngestionBody(JsonObject document)
        => ResolveSchema(
            document,
            document["paths"]
                .ShouldNotBeNull()
                .AsObject()[IngestionRoute]
                .ShouldNotBeNull()
                .AsObject()["post"]
                .ShouldNotBeNull()
                .AsObject()["requestBody"]
                .ShouldNotBeNull()
                .AsObject()["content"]
                .ShouldNotBeNull()
                .AsObject()["application/json"]
                .ShouldNotBeNull()
                .AsObject()["schema"]
                .ShouldNotBeNull()
                .AsObject());

    private static JsonObject ResolveSchema(JsonObject document, JsonObject schema)
    {
        JsonNode? reference = schema["$ref"];
        if (reference is null)
        {
            return schema;
        }

        var value = reference.GetValue<string>();
        return document["components"]
            .ShouldNotBeNull()
            .AsObject()["schemas"]
            .ShouldNotBeNull()
            .AsObject()[value[(value.LastIndexOf('/') + 1)..]]
            .ShouldNotBeNull()
            .AsObject();
    }

    private async Task<JsonObject> ReadDocumentAsync(string route)
    {
        HttpResponseMessage response = await AuthenticatedClient().GetAsync(route);
        var document = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return JsonNode.Parse(document).ShouldNotBeNull().AsObject();
    }

    private HttpClient AuthenticatedClient()
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            DevelopmentToken.Issue());
        return client;
    }

    /// <summary>
    /// Signs with the committed development key, which the host accepts only
    /// in Development. Any authenticated principal is enough: the document
    /// carries no role requirement beyond authentication.
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
