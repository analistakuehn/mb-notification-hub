using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>
/// The rendered SMS that leaves the published catalog, and the hash that
/// claims to describe it. The two are asserted together on purpose: the audit
/// verifies a stored message by re-rendering it and comparing hashes, so a
/// hash taken before normalization would call every SMS tampered with.
/// </summary>
[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class SmsRenderNormalizationContractTests(TemplateManagementApiFixture fixture)
{
    private static readonly string[] RequiredCode = ["code"];

    /// <summary>Accents as combining marks, a zero width space and a line break.</summary>
    private const string SourceBody = "Código {{ code }}​\r\nválido por 5 min";

    private const string RenderedWithoutNormalization = "Código 998877​\r\nválido por 5 min";

    private const string Normalized = "Código 998877 válido por 5 min";

    [RequiresDockerFact]
    public async Task The_sms_render_ships_the_normalized_text_and_hashes_exactly_that()
    {
        HttpClient author = fixture.CreateAuthorClient("author-sms-norm");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-sms-norm");
        var key = await TemplateApi.CreateTemplateAsync(
            author, TemplateApi.NewKey("smsnorm"), defaultLocale: "pt-BR");
        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        etag = await TemplateApi.PutContentAsync(
            author, key, version, "sms/pt-BR", new { body = SourceBody }, etag);
        await TemplateApi.PutSchemaAsync(author, key, version, new
        {
            type = "object",
            properties = new { code = new { type = "string" } },
            required = RequiredCode,
        }, etag);
        await TemplateApi.PublishAsync(publisher, key, version);

        using IServiceScope scope = fixture.Services.CreateScope();
        IPublishedTemplateRenderer renderer =
            scope.ServiceProvider.GetRequiredService<IPublishedTemplateRenderer>();
        Result<PublishedTemplateRender> rendered = await renderer.RenderAsync(new PublishedRenderRequest
        {
            Application = "araia-cambio",
            TemplateKey = key,
            Channel = "sms",
            Locale = "pt-BR",
            Variables = JsonDocument.Parse("""{ "code": "998877" }""").RootElement,
            IncludeMaskedForm = true,
        }, CancellationToken.None);

        rendered.IsSuccess.ShouldBeTrue(rendered.Error);
        PublishedTemplateRender render = rendered.Value!;

        // Premise: the untouched render is not already in the shipped form, or
        // every assertion below would hold with no normalizer at all.
        RenderedWithoutNormalization.ShouldNotBe(Normalized);

        // The hash is asserted before the body on purpose. Both fail if the
        // normalization disappears, and the one that has to name the harm is
        // this one: an audit re-renders a stored message and compares hashes,
        // so a hash taken over the untouched render marks every SMS as
        // tampered with.
        render.Full.ContentHash.ShouldBe(
            CanonicalHash.OfFields(null, Normalized, null),
            "the audited hash must describe the normalized SMS, the exact text the provider receives");
        render.Full.ContentHash.ShouldNotBe(
            CanonicalHash.OfFields(null, RenderedWithoutNormalization, null),
            "the audited hash must not describe the untouched render");
        render.Full.Body.ShouldBe(Normalized);

        // The masked form of a payload with nothing to mask is the same form,
        // hash included, so the normalization cannot diverge between them.
        render.Masked.ShouldNotBeNull().ShouldBe(render.Full);
    }
}
