using System.Text;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>
/// Documents that parse and do not transcode, plus the only way to get one into
/// the store once the doors refuse them. A row like this is what a store
/// written before the doors existed holds, and it is the state every read path
/// has to answer instead of fail on.
/// </summary>
internal static class UnreadableDocumentSeed
{
    /// <summary>
    /// Raw text on purpose. Spelled as a C# escape the compiler would fold it
    /// into one code unit and the document would never carry the six characters
    /// that make it legal JSON text nobody can transcode.
    /// </summary>
    internal const string LoneSurrogateEscape = @"\ud800";

    /// <summary>
    /// The escape sits in a value the declaration walk never reads, so the
    /// publication catalog passes over it and the failure lands further in.
    /// </summary>
    internal const string SchemaWithSurrogateInValue =
        "{\"properties\":{\"a\":{\"type\":\"string\",\"title\":\"" + LoneSurrogateEscape + "\"}}}";

    /// <summary>The escape sits in a property name, which the declaration walk does read.</summary>
    internal const string SchemaWithSurrogateInName =
        "{\"properties\":{\"" + LoneSurrogateEscape + "\":{\"type\":\"string\"}}}";

    internal const string DefinitionWithSurrogate =
        "{\"schemaVersion\":1,\"channelsAllowed\":[\"sms\"],\"deliveryPlan\":[{\"channel\":\"sms\"}],"
        + "\"defaultTtl\":\"300s\",\"dedupeWindow\":\"60s\",\"consentPurpose\":\""
        + LoneSurrogateEscape + "\"}";

    /// <summary>A hash that stands in for the persisted column and is never the value under test.</summary>
    private const string StoredHash = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// Writes a version straight to the store, bypassing the doors, which is
    /// the only arrangement that reproduces a row the doors would refuse today.
    /// The hash travels with it so rehydration never has to derive one from a
    /// document it cannot read.
    /// </summary>
    internal static Task SeedVersionAsync(
        TemplateManagementApiFixture fixture,
        string key,
        int version,
        string status,
        string schemaJson,
        string body = "<p>Pedido atualizado.</p>")
        => fixture.ExecuteDbAsync(async dbContext =>
        {
            dbContext.TemplateVersions.Add(TemplateVersion.Rehydrate(new TemplateVersionState
            {
                TemplateKey = key,
                Version = version,
                Status = status,
                CreatedBy = "seed-author",
                CreatedAt = DateTimeOffset.UtcNow,
                PublishedAt = status == "draft" ? null : DateTimeOffset.UtcNow,
                VariablesSchemaJson = schemaJson,
                ContentHash = StoredHash,
                Editors = ["seed-author"],
                Contents = [new TemplateContentState("email", "pt-BR", "Pedido", body, "Pedido atualizado.")],
            }));
            await dbContext.SaveChangesAsync();
        });

    /// <summary>
    /// A request whose body is exactly the text given. Serializing an object
    /// would rewrite the escape before it left the test and the request under
    /// test would never carry the fault it exists to carry.
    /// </summary>
    internal static HttpRequestMessage PutRawJson(string url, string body, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }
}
