using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class PublishedIntegrationContractTests(TemplateManagementApiFixture fixture)
{
    private static readonly string[] RequiredOrderId = ["orderId"];
    private static readonly string[] RequiredCode = ["code"];

    [RequiresDockerFact]
    public async Task The_catalog_returns_the_decision_metadata_of_the_published_version()
    {
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        var key = await TemplateApi.CreateTemplateAsync(
            author, TemplateApi.NewKey(), defaultLocale: "pt-BR", sensitiveVariables: ["cpf"]);
        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        etag = await TemplateApi.PutContentAsync(author, key, version, "email/pt-BR", new
        {
            subject = "Pedido {{ orderId }}",
            body = "<p>Pedido {{ orderId }} atualizado.</p>",
            bodyText = "Pedido {{ orderId }} atualizado.",
        }, etag);
        etag = await TemplateApi.PutContentAsync(author, key, version, "sms/pt-BR", new
        {
            body = "Pedido {{ orderId }} atualizado.",
        }, etag);
        // 'cpf' is declared sensitive on the identity, so the schema has to
        // declare it too: a sensitive name the schema never declares masks
        // nothing, and publication now refuses that false assurance.
        await TemplateApi.PutSchemaAsync(author, key, version, new
        {
            type = "object",
            properties = new
            {
                orderId = new { type = "string" },
                cpf = new { type = "string" },
            },
            required = RequiredOrderId,
        }, etag);
        await TemplateApi.PublishAsync(publisher, key, version);

        Result<PublishedTemplateLookup> lookup = await FindTemplateAsync("araia-cambio", key);

        lookup.IsSuccess.ShouldBeTrue(lookup.Error);
        PublishedTemplate template = lookup.Value
            .ShouldBeOfType<PublishedTemplateLookup.Published>().Template;
        template.Application.ShouldBe("araia-cambio");
        template.TemplateKey.ShouldBe(key);
        template.Class.ShouldBe("transactional");
        template.OwnerTeam.ShouldBe("growth-squad");
        template.Purpose.ShouldBe("order-updates");
        template.LegalBasis.ShouldBe("execucao-de-contrato");
        template.SensitiveVariables.ShouldBe(["cpf"]);
        template.ChannelsWithContent.ShouldBe([Channel.Email, Channel.Sms], ignoreOrder: true);
        template.DefaultLocale.ShouldBe("pt-BR");
        template.Version.ShouldBe(version);

        var storedHash = await StoredVersionContentHashAsync(key, version);
        template.ContentHash.ShouldBe(storedHash);
    }

    [RequiresDockerFact]
    public async Task A_deprecated_template_reports_the_catalog_deprecation_reason()
    {
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, key, version);
        HttpResponseMessage deprecated = await publisher.PostAsJsonAsync(
            $"/v1/templates/{key}/deprecate", new { reason = "substituído pela nova jornada" });
        deprecated.EnsureSuccessStatusCode();

        Result<PublishedTemplateLookup> lookup = await FindTemplateAsync("araia-cambio", key);

        lookup.IsSuccess.ShouldBeTrue(lookup.Error);
        lookup.Value.ShouldBeOfType<PublishedTemplateLookup.Rejected>()
            .Reason.ShouldBe(TemplateRejectionReasons.Deprecated);
    }

    [RequiresDockerFact]
    public async Task A_disabled_template_reports_the_catalog_disablement_reason()
    {
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, key, version);
        HttpResponseMessage disabled = await publisher.PostAsJsonAsync(
            $"/v1/templates/{key}/disable", new { reason = "conteúdo incorreto em produção" });
        disabled.EnsureSuccessStatusCode();

        Result<PublishedTemplateLookup> lookup = await FindTemplateAsync("araia-cambio", key);

        lookup.IsSuccess.ShouldBeTrue(lookup.Error);
        lookup.Value.ShouldBeOfType<PublishedTemplateLookup.Rejected>()
            .Reason.ShouldBe(TemplateRejectionReasons.Disabled);
    }

    [RequiresDockerFact]
    public async Task A_template_of_another_application_is_not_found()
    {
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, key, version);

        Result<PublishedTemplateLookup> lookup = await FindTemplateAsync("araia-investimentos", key);

        lookup.IsFailure.ShouldBeTrue();
        lookup.ErrorKind.ShouldBe(ResultErrorKind.NotFound);
    }

    [RequiresDockerFact]
    public async Task A_provided_variable_the_published_schema_does_not_declare_fails_the_report()
    {
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, key, version);

        using IServiceScope scope = fixture.Services.CreateScope();
        IPublishedVariablesValidator validator =
            scope.ServiceProvider.GetRequiredService<IPublishedVariablesValidator>();
        Result<VariablesValidationReport> report = await validator.ValidateAsync(
            "araia-cambio",
            key,
            Variables("""{ "orderId": "42", "cupom": "MB10" }"""),
            CancellationToken.None);

        report.IsSuccess.ShouldBeTrue(report.Error);
        report.Value!.Passed.ShouldBeFalse();
        VariablesValidationCheck check = report.Value!.Checks.Single(candidate =>
            candidate.Name == "variables-declared");
        check.Status.ShouldBe(VariablesValidationStatuses.Failed);
        check.Message.ShouldContain("'cupom'");
    }

    [RequiresDockerFact]
    public async Task A_url_variable_outside_the_allowlist_fails_the_report()
    {
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        var key = await TemplateApi.CreateTemplateAsync(
            author, TemplateApi.NewKey(), defaultLocale: "pt-BR", linkDomainsAllowed: ["montebravo.com.br"]);
        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        etag = await TemplateApi.PutContentAsync(author, key, version, "email/pt-BR", new
        {
            subject = "Acesso",
            body = "<p>Acesse {{ portalUrl }}</p>",
            bodyText = "Acesse {{ portalUrl }}",
        }, etag);
        await TemplateApi.PutSchemaAsync(author, key, version, new
        {
            type = "object",
            properties = new { portalUrl = new { type = "string", format = "url" } },
        }, etag);
        await TemplateApi.PublishAsync(publisher, key, version);

        using IServiceScope scope = fixture.Services.CreateScope();
        IPublishedVariablesValidator validator =
            scope.ServiceProvider.GetRequiredService<IPublishedVariablesValidator>();
        Result<VariablesValidationReport> report = await validator.ValidateAsync(
            "araia-cambio",
            key,
            Variables("""{ "portalUrl": "https://phishing.example.io/login" }"""),
            CancellationToken.None);

        report.IsSuccess.ShouldBeTrue(report.Error);
        VariablesValidationCheck check = report.Value!.Checks.Single(candidate =>
            candidate.Name == "url-allowlist");
        check.Status.ShouldBe(VariablesValidationStatuses.Failed);
        check.Message.ShouldContain("'portalUrl'");
        check.Message.ShouldNotContain("phishing.example.io");
    }

    [RequiresDockerFact]
    public async Task The_full_and_masked_forms_render_with_the_pinned_layout_and_coherent_hashes()
    {
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishedLayoutAsync(author, publisher);
        var key = await TemplateApi.CreateTemplateAsync(
            author, TemplateApi.NewKey(), defaultLocale: "pt-BR", sensitiveVariables: ["code"]);
        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        etag = await TemplateApi.PutContentAsync(author, key, version, "email/pt-BR", new
        {
            subject = "Código de acesso",
            body = "<p>Código {{ code }}</p>",
            bodyText = "Código {{ code }}",
        }, etag);
        etag = await TemplateApi.PutSchemaAsync(author, key, version, new
        {
            type = "object",
            properties = new { code = new { type = "string" } },
            required = RequiredCode,
        }, etag);
        HttpResponseMessage pinned = await author.SendAsync(TemplateApi.PutJson(
            $"/v1/templates/{key}/versions/{version}/layout",
            new { layoutKey, layoutVersion },
            etag));
        pinned.EnsureSuccessStatusCode();
        await TemplateApi.PublishAsync(publisher, key, version);

        using IServiceScope scope = fixture.Services.CreateScope();
        IPublishedTemplateRenderer renderer =
            scope.ServiceProvider.GetRequiredService<IPublishedTemplateRenderer>();
        Result<PublishedTemplateRender> rendered = await renderer.RenderAsync(new PublishedRenderRequest
        {
            Application = "araia-cambio",
            TemplateKey = key,
            Channel = "email",
            Locale = "pt-BR",
            Variables = Variables("""{ "code": "998877" }"""),
            IncludeMaskedForm = true,
        }, CancellationToken.None);

        rendered.IsSuccess.ShouldBeTrue(rendered.Error);
        PublishedTemplateRender render = rendered.Value!;
        render.Channel.ShouldBe("email");
        render.ResolvedLocale.ShouldBe("pt-BR");
        render.Version.ShouldBe(version);

        render.Full.Subject.ShouldBe("Código de acesso");
        render.Full.Body.ShouldBe("<html><header>MB</header><p>Código 998877</p><footer>rodapé</footer></html>");
        render.Full.BodyText.ShouldBe("MB\nCódigo 998877\nrodapé");

        RenderedForm masked = render.Masked.ShouldNotBeNull();
        masked.Subject.ShouldBe("Código de acesso");
        masked.Body.ShouldBe("<html><header>MB</header><p>Código ***</p><footer>rodapé</footer></html>");
        masked.BodyText.ShouldBe("MB\nCódigo ***\nrodapé");

        // Each hash covers exactly the fields its form shipped, in the same
        // canonical dialect the stored content columns use.
        render.Full.ContentHash.ShouldBe(
            CanonicalHash.OfFields(render.Full.Subject, render.Full.Body, render.Full.BodyText));
        masked.ContentHash.ShouldBe(
            CanonicalHash.OfFields(masked.Subject, masked.Body, masked.BodyText));
        masked.ContentHash.ShouldNotBe(render.Full.ContentHash);
    }

    [RequiresDockerFact]
    public async Task A_layout_that_reads_a_template_variable_is_refused_the_payload()
    {
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");

        // The layout asks for a variable of the template payload. Publication
        // allows it, because a layout is only required to read the content
        // placeholder; the render is what has to refuse it, since the layout
        // sees the finished text and never the data it was rendered from.
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishableDraftAsync(
            author,
            body: "<html>{{ content }}<!-- {{ code }} --></html>");
        await LayoutApi.PublishAsync(publisher, layoutKey, layoutVersion);

        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey(), defaultLocale: "pt-BR");
        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        etag = await TemplateApi.PutContentAsync(author, key, version, "email/pt-BR", new
        {
            subject = "Código de acesso",
            body = "<p>Código {{ code }}</p>",
            bodyText = "Código {{ code }}",
        }, etag);
        etag = await TemplateApi.PutSchemaAsync(author, key, version, new
        {
            type = "object",
            properties = new { code = new { type = "string" } },
            required = RequiredCode,
        }, etag);
        HttpResponseMessage pinned = await author.SendAsync(TemplateApi.PutJson(
            $"/v1/templates/{key}/versions/{version}/layout",
            new { layoutKey, layoutVersion },
            etag));
        pinned.EnsureSuccessStatusCode();
        await TemplateApi.PublishAsync(publisher, key, version);

        using IServiceScope scope = fixture.Services.CreateScope();
        IPublishedTemplateRenderer renderer =
            scope.ServiceProvider.GetRequiredService<IPublishedTemplateRenderer>();
        Result<PublishedTemplateRender> rendered = await renderer.RenderAsync(new PublishedRenderRequest
        {
            Application = "araia-cambio",
            TemplateKey = key,
            Channel = "email",
            Locale = "pt-BR",
            Variables = Variables("""{ "code": "998877" }"""),
        }, CancellationToken.None);

        // Refused, not resolved to nothing: the failure is what keeps a value
        // out of a frame the caller never exposed it to.
        rendered.IsFailure.ShouldBeTrue();
        rendered.Error!.ShouldContain("code");
        rendered.Error!.ShouldNotContain("998877");
    }

    [RequiresDockerFact]
    public async Task A_payload_without_sensitive_variables_shares_one_form_and_one_hash()
    {
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, key, version);

        using IServiceScope scope = fixture.Services.CreateScope();
        IPublishedTemplateRenderer renderer =
            scope.ServiceProvider.GetRequiredService<IPublishedTemplateRenderer>();
        Result<PublishedTemplateRender> rendered = await renderer.RenderAsync(new PublishedRenderRequest
        {
            Application = "araia-cambio",
            TemplateKey = key,
            Channel = "email",
            Locale = "pt-BR",
            Variables = Variables("""{ "orderId": "42" }"""),
            IncludeMaskedForm = true,
        }, CancellationToken.None);

        rendered.IsSuccess.ShouldBeTrue(rendered.Error);
        RenderedForm masked = rendered.Value!.Masked.ShouldNotBeNull();
        masked.ShouldBe(rendered.Value!.Full);
    }

    [RequiresDockerFact]
    public async Task A_disabled_layout_refuses_the_published_render()
    {
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishedLayoutAsync(author, publisher);
        var key = await PublishTemplatePinnedToLayoutAsync(author, publisher, layoutKey, layoutVersion);
        HttpResponseMessage disabled = await publisher.PostAsJsonAsync(
            $"/v1/layouts/{layoutKey}/disable", new { reason = "conteúdo comprometido no próprio wrapper" });
        disabled.EnsureSuccessStatusCode();

        // Nothing rendered this layout before it was disabled, so no memoized
        // answer can be the one under test here: this is what the store says.
        Result<PublishedTemplateRender> rendered = await RenderPublishedEmailAsync(key);

        // The whole message stops, whatever the class of the template: a body
        // shipped without the wrapper carries a canonical hash that matches
        // nothing anyone approved.
        rendered.IsFailure.ShouldBeTrue();
        rendered.ErrorKind.ShouldBe(ResultErrorKind.BusinessRule);
        rendered.Error.ShouldBe(LayoutRejectionReasons.Disabled);
    }

    [RequiresDockerFact]
    public async Task A_deprecated_layout_still_frames_the_published_render()
    {
        // Falsification of the refusal above: without this pair, that
        // assertion would hold for any status other than active. Deprecation
        // says the layout takes no new reference, not that the versions
        // already pinned to it stop being reproducible.
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishedLayoutAsync(author, publisher);
        var key = await PublishTemplatePinnedToLayoutAsync(author, publisher, layoutKey, layoutVersion);
        HttpResponseMessage deprecated = await publisher.PostAsJsonAsync(
            $"/v1/layouts/{layoutKey}/deprecate", new { reason = "substituído pela nova identidade visual" });
        deprecated.EnsureSuccessStatusCode();

        Result<PublishedTemplateRender> rendered = await RenderPublishedEmailAsync(key);

        rendered.IsSuccess.ShouldBeTrue(rendered.Error);
        rendered.Value!.Full.Body.ShouldBe(
            "<html><header>MB</header><p>Pedido 42 atualizado.</p><footer>rodapé</footer></html>");
    }

    /// <summary>
    /// The ceiling on the variables payload reaches the render between
    /// modules, and it answers ahead of the catalog. The template key is one
    /// that was never created on purpose: a not-found here would be proof that
    /// the payload was carried past the gate, into the query and into the walk
    /// over every string value it contains.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_oversized_variables_payload_is_refused_before_the_catalog_and_the_scan()
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        IPublishedTemplateRenderer renderer =
            scope.ServiceProvider.GetRequiredService<IPublishedTemplateRenderer>();

        Result<PublishedTemplateRender> rendered = await renderer.RenderAsync(new PublishedRenderRequest
        {
            Application = "araia-cambio",
            TemplateKey = "template.that.was.never.created",
            Channel = "email",
            Locale = "pt-BR",
            Variables = Variables($$$"""{ "blob": "{{{new string('x', 300_000)}}}" }"""),
            IncludeMaskedForm = true,
        }, CancellationToken.None);

        rendered.IsFailure.ShouldBeTrue();
        DomainErrorInfo error = DomainError.Describe(rendered.Error, rendered.ErrorKind);
        error.Code.ShouldBe(ErrorCodes.VariablesPayloadTooLarge);

        // The refusal names the ceiling and nothing the caller sent: the same
        // rule the allowlist refusal follows, for the same reason.
        error.Detail.ShouldContain("262144");
        error.Detail.ShouldNotContain("xxx");
    }

    [RequiresDockerFact]
    public async Task The_published_class_policy_is_read_by_application_and_class()
    {
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        (var application, var version, _) = await ClassPolicyApi.CreateDraftAsync(author);
        await ClassPolicyApi.PublishAsync(publisher, application);

        using IServiceScope scope = fixture.Services.CreateScope();
        IPublishedCatalog catalog = scope.ServiceProvider.GetRequiredService<IPublishedCatalog>();
        Result<PublishedClassPolicy> policy = await catalog.FindClassPolicyAsync(
            application, ClassPolicyApi.DefaultClass, CancellationToken.None);

        policy.IsSuccess.ShouldBeTrue(policy.Error);
        policy.Value!.Application.ShouldBe(application);
        policy.Value!.Class.ShouldBe(ClassPolicyApi.DefaultClass);
        policy.Value!.Version.ShouldBe(version);
        policy.Value!.Definition.SchemaVersion.ShouldBe(1);
        policy.Value!.Definition.ChannelsAllowed.ShouldBe([Channel.Push, Channel.Sms]);
        policy.Value!.Definition.DedupeWindow.ShouldBe(TimeSpan.FromSeconds(60));

        var storedHash = await StoredPolicyContentHashAsync(application, version);
        policy.Value!.ContentHash.ShouldBe(storedHash);
    }

    [RequiresDockerFact]
    public async Task An_application_without_published_policy_is_not_found()
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        IPublishedCatalog catalog = scope.ServiceProvider.GetRequiredService<IPublishedCatalog>();

        Result<PublishedClassPolicy> policy = await catalog.FindClassPolicyAsync(
            ClassPolicyApi.NewApplication(), ClassPolicyApi.DefaultClass, CancellationToken.None);

        policy.IsFailure.ShouldBeTrue();
        policy.ErrorKind.ShouldBe(ResultErrorKind.NotFound);
    }

    private async Task<Result<PublishedTemplateLookup>> FindTemplateAsync(string application, string key)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        IPublishedCatalog catalog = scope.ServiceProvider.GetRequiredService<IPublishedCatalog>();
        return await catalog.FindTemplateAsync(application, key, CancellationToken.None);
    }

    private async Task<string> StoredVersionContentHashAsync(string key, int version)
    {
        var hash = string.Empty;
        await fixture.ExecuteDbAsync(async dbContext =>
        {
            TemplateVersion stored = await dbContext.TemplateVersions
                .AsNoTracking()
                .WhereTemplateKey(TemplateKey.Create(key).Value!)
                .SingleAsync(candidate => candidate.Version == version);
            hash = stored.ContentHash;
        });
        return hash;
    }

    private async Task<string> StoredPolicyContentHashAsync(string application, int version)
    {
        var hash = string.Empty;
        await fixture.ExecuteDbAsync(async dbContext =>
        {
            ClassPolicyVersion stored = await dbContext.ClassPolicyVersions
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Application == application
                    && candidate.Version == version);
            hash = stored.ContentHash;
        });
        return hash;
    }

    /// <summary>A published template version whose email content is framed by the given layout version.</summary>
    private static async Task<string> PublishTemplatePinnedToLayoutAsync(
        HttpClient author,
        HttpClient publisher,
        string layoutKey,
        int layoutVersion)
    {
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey(), defaultLocale: "pt-BR");
        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        etag = await TemplateApi.PutContentAsync(author, key, version, "email/pt-BR", new
        {
            subject = "Pedido {{ orderId }}",
            body = "<p>Pedido {{ orderId }} atualizado.</p>",
            bodyText = "Pedido {{ orderId }} atualizado.",
        }, etag);
        etag = await TemplateApi.PutSchemaAsync(author, key, version, new
        {
            type = "object",
            properties = new { orderId = new { type = "string" } },
            required = RequiredOrderId,
        }, etag);
        HttpResponseMessage pinned = await author.SendAsync(TemplateApi.PutJson(
            $"/v1/templates/{key}/versions/{version}/layout",
            new { layoutKey, layoutVersion },
            etag));
        pinned.EnsureSuccessStatusCode();
        await TemplateApi.PublishAsync(publisher, key, version);
        return key;
    }

    private async Task<Result<PublishedTemplateRender>> RenderPublishedEmailAsync(string key)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        IPublishedTemplateRenderer renderer =
            scope.ServiceProvider.GetRequiredService<IPublishedTemplateRenderer>();
        return await renderer.RenderAsync(new PublishedRenderRequest
        {
            Application = "araia-cambio",
            TemplateKey = key,
            Channel = "email",
            Locale = "pt-BR",
            Variables = Variables("""{ "orderId": "42" }"""),
        }, CancellationToken.None);
    }

    private static JsonElement Variables(string json)
        => JsonSerializer.Deserialize<JsonElement>(json);
}
