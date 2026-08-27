using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>
/// The publication gate against links in authentication SMS content, exercised
/// through the real four-eyes flow. Publication is the cheap place to refuse:
/// the alternative is finding out at render time, one authentication code at a
/// time.
/// </summary>
[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class AuthenticationSmsLinkPublicationTests(TemplateManagementApiFixture fixture)
{
    private static readonly string[] RequiredCode = ["code"];
    private static readonly string[] AllowedDomains = ["banco.example.com"];

    [RequiresDockerFact]
    public async Task An_authentication_sms_carrying_a_link_does_not_publish()
    {
        HttpClient author = fixture.CreateAuthorClient("author-auth-sms-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-auth-sms-1");
        (var key, var version) = await DraftAsync(
            author, "Código {{ code }}. Confirme em https://banco.example.com/otp");

        HttpResponseMessage response = await publisher.PostAsync(
            $"/v1/templates/{key}/versions/{version}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-validation-failed");
        List<JsonElement> checks = [.. problem.GetProperty("checks").EnumerateArray()];
        checks.ShouldContain(check =>
            check.GetProperty("name").GetString() == "authentication-sms-links"
            && check.GetProperty("status").GetString() == "failed"
            && check.GetProperty("location").GetString() == "sms/pt-BR/body");

        HttpResponseMessage versionResponse = await author.GetAsync($"/v1/templates/{key}/versions/{version}");
        JsonElement body = await TemplateApi.ReadJsonAsync(versionResponse);
        body.GetProperty("status").GetString().ShouldBe("draft");
    }

    [RequiresDockerFact]
    public async Task A_shortened_link_without_a_scheme_does_not_publish_either()
    {
        // The shape SMS phishing actually uses. A gate that only knows
        // https:// would call this content clean.
        HttpClient author = fixture.CreateAuthorClient("author-auth-sms-2");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-auth-sms-2");
        (var key, var version) = await DraftAsync(author, "Código {{ code }}. Detalhes: bit.ly/x9k2p");

        HttpResponseMessage response = await publisher.PostAsync(
            $"/v1/templates/{key}/versions/{version}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        List<JsonElement> checks = [.. problem.GetProperty("checks").EnumerateArray()];
        checks.ShouldContain(check =>
            check.GetProperty("name").GetString() == "authentication-sms-links"
            && check.GetProperty("status").GetString() == "failed");
    }

    [RequiresDockerFact]
    public async Task The_same_authentication_sms_without_a_link_publishes()
    {
        // Falsification: what blocks the two publications above is the link,
        // not the purpose, the channel or the four-eyes flow.
        HttpClient author = fixture.CreateAuthorClient("author-auth-sms-3");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-auth-sms-3");
        (var key, var version) = await DraftAsync(
            author, "Seu código de acesso é {{ code }}. Não compartilhe.");

        HttpResponseMessage response = await publisher.PostAsync(
            $"/v1/templates/{key}/versions/{version}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("status").GetString().ShouldBe("published");
    }

    [RequiresDockerTheory]
    [InlineData("authentication")]
    [InlineData("Authentication")]
    [InlineData("AUTHENTICATION")]
    public async Task An_authentication_sms_carrying_a_link_does_not_publish_whatever_the_case_of_the_purpose(
        string purpose)
    {
        // The gate has to read the purpose the same way whichever case the
        // author typed: the message that goes out is identical, and so is the
        // person who receives it.
        HttpClient author = fixture.CreateAuthorClient($"author-auth-sms-case-{purpose}");
        HttpClient publisher = fixture.CreatePublisherClient($"publisher-auth-sms-case-{purpose}");
        (var key, var version) = await DraftAsync(
            author, "Código {{ code }}. Confirme em https://banco.example.com/otp", purpose);

        HttpResponseMessage response = await publisher.PostAsync(
            $"/v1/templates/{key}/versions/{version}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        List<JsonElement> checks = [.. problem.GetProperty("checks").EnumerateArray()];
        checks.ShouldContain(check =>
            check.GetProperty("name").GetString() == "authentication-sms-links"
            && check.GetProperty("status").GetString() == "failed"
            && check.GetProperty("location").GetString() == "sms/pt-BR/body");
    }

    [RequiresDockerFact]
    public async Task The_creation_response_reports_the_canonical_purpose()
    {
        // Nothing edits a template's metadata after creation, so a value the
        // platform rewrites in silence is a value its author cannot undo. The
        // 201 body is the one place the correction can still be seen.
        HttpClient author = fixture.CreateAuthorClient("author-auth-sms-echo");

        HttpResponseMessage created = await author.PostAsJsonAsync("/v1/templates", new
        {
            key = TemplateApi.NewKey("authecho"),
            application = "araia-cambio",
            @class = "transactional",
            ownerTeam = "Growth-Squad",
            purpose = "Authentication",
            legalBasis = "execucao-de-contrato",
            defaultLocale = "pt-BR",
            linkDomainsAllowed = AllowedDomains,
        });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        JsonElement body = await TemplateApi.ReadJsonAsync(created);
        body.GetProperty("purpose").GetString().ShouldBe("authentication");

        // Only the purpose is canonized: the owner team is read by people and
        // keeps every character the author sent.
        body.GetProperty("ownerTeam").GetString().ShouldBe("Growth-Squad");
    }

    [RequiresDockerFact]
    public async Task Every_persisted_purpose_is_canonical()
    {
        // The premise the ordinal comparison rests on, asserted against the
        // migrated database instead of against the aggregate: with one write
        // door canonizing and the migration cleaning whatever came before, no
        // stored row can hold a purpose the comparison would read past.
        HttpClient author = fixture.CreateAuthorClient("author-auth-sms-scan");
        HttpResponseMessage created = await author.PostAsJsonAsync("/v1/templates", new
        {
            key = TemplateApi.NewKey("authscan"),
            application = "araia-cambio",
            @class = "transactional",
            ownerTeam = "growth-squad",
            purpose = "  AUTHENTICATION  ",
            legalBasis = "execucao-de-contrato",
            defaultLocale = "pt-BR",
            linkDomainsAllowed = AllowedDomains,
        });
        created.EnsureSuccessStatusCode();

        var uncanonical = 0L;
        var stored = 0L;
        await fixture.ExecuteDbAsync(async db =>
        {
            uncanonical = await db.Database
                .SqlQuery<long>($"""
                    SELECT count(*) AS "Value"
                    FROM templatemanagement.template
                    WHERE purpose <> lower(purpose)
                    """)
                .SingleAsync();
            stored = await db.Database
                .SqlQuery<long>($"""
                    SELECT count(*) AS "Value"
                    FROM templatemanagement.template
                    """)
                .SingleAsync();
        });

        uncanonical.ShouldBe(0L);

        // An empty table would satisfy the count above for free.
        stored.ShouldBeGreaterThan(0L);
    }

    private static async Task<(string Key, int Version)> DraftAsync(
        HttpClient author,
        string smsBody,
        string purpose = "authentication")
    {
        var key = TemplateApi.NewKey("authsms");
        HttpResponseMessage created = await author.PostAsJsonAsync("/v1/templates", new
        {
            key,
            application = "araia-cambio",
            @class = "transactional",
            ownerTeam = "growth-squad",
            purpose,
            legalBasis = "execucao-de-contrato",
            defaultLocale = "pt-BR",
            linkDomainsAllowed = AllowedDomains,
        });
        created.EnsureSuccessStatusCode();

        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        etag = await TemplateApi.PutContentAsync(
            author, key, version, "sms/pt-BR", new { body = smsBody }, etag);
        await TemplateApi.PutSchemaAsync(author, key, version, new
        {
            type = "object",
            properties = new { code = new { type = "string" } },
            required = RequiredCode,
        }, etag);
        return (key, version);
    }
}
