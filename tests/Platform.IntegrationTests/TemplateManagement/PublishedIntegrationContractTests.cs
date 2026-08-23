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
        await TemplateApi.PutSchemaAsync(author, key, version, new
        {
            type = "object",
            properties = new { orderId = new { type = "string" } },
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

    private static JsonElement Variables(string json)
        => JsonSerializer.Deserialize<JsonElement>(json);
}
