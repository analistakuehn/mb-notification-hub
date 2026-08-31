using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class TemplateTests
{
    private static readonly TemplateKey Key = TemplateKey.Create("auth.otp.login").Value!;

    [Fact]
    public void A_new_template_starts_active_with_its_metadata_preserved()
    {
        Result<Template> result = Template.Create(Key, Metadata());

        result.IsSuccess.ShouldBeTrue();
        Template template = result.Value!;
        template.Key.Value.ShouldBe("auth.otp.login");
        template.Application.ShouldBe("araia-cambio");
        template.Class.ShouldBe(NotificationClass.Critical);
        template.OwnerTeam.ShouldBe("identity-squad");
        template.Purpose.ShouldBe("authentication");
        template.LegalBasis.ShouldBe("execucao-de-contrato");
        template.Status.ShouldBe(TemplateStatus.Active);
        template.DefaultLocale.ShouldBeNull();
        template.LinkDomainsAllowed.ShouldBeEmpty();
    }

    [Fact]
    public void Metadata_text_fields_are_trimmed()
    {
        Result<Template> result = Template.Create(Key, Metadata() with
        {
            Application = " araia-cambio ",
            OwnerTeam = " ops ",
            Purpose = " reminders ",
            LegalBasis = " legitimo-interesse ",
        });

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Application.ShouldBe("araia-cambio");
        result.Value!.OwnerTeam.ShouldBe("ops");
    }

    [Fact]
    public void The_aggregate_stores_the_purpose_in_lower_case()
    {
        // The single write door of this column. Everything downstream compares
        // the purpose against one lower-case word with an ordinal comparison,
        // including a SQL predicate that cannot be taught to ignore case
        // without losing its index, so the canonical form has to be minted
        // here and nowhere else.
        Result<Template> result = Template.Create(Key, Metadata() with { Purpose = "  Authentication  " });

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Purpose.ShouldBe("authentication");
    }

    [Fact]
    public void The_owner_team_and_the_legal_basis_keep_the_case_they_were_given()
    {
        // Falsification of the rule above: only the purpose is canonized. The
        // owner team and the legal basis go through the same required-text
        // guard and are read by people, never by an equality comparison, so
        // lowering their case would silently rewrite two values nobody asked
        // to change.
        Result<Template> result = Template.Create(Key, Metadata() with
        {
            OwnerTeam = " Identity-Squad ",
            LegalBasis = " Execucao-De-Contrato ",
        });

        result.IsSuccess.ShouldBeTrue();
        result.Value!.OwnerTeam.ShouldBe("Identity-Squad");
        result.Value!.LegalBasis.ShouldBe("Execucao-De-Contrato");
    }

    [Theory]
    [InlineData("Araia-Cambio")]
    [InlineData("araia cambio")]
    [InlineData("")]
    [InlineData("araia_cambio")]
    public void Rejects_applications_outside_the_naming_convention(string application)
    {
        Result<Template> result = Template.Create(Key, Metadata() with { Application = application });

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }

    [Theory]
    [InlineData("", "authentication", "legal")]
    [InlineData("owners", "", "legal")]
    [InlineData("owners", "authentication", "")]
    public void Rejects_blank_governance_fields(string ownerTeam, string purpose, string legalBasis)
    {
        Result<Template> result = Template.Create(Key, Metadata() with
        {
            OwnerTeam = ownerTeam,
            Purpose = purpose,
            LegalBasis = legalBasis,
        });

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }

    [Fact]
    public void Link_domains_are_normalized_to_lowercase_and_deduplicated()
    {
        Result<Template> result = Template.Create(Key, Metadata() with
        {
            LinkDomainsAllowed = [" MonteBravo.com.br ", "montebravo.com.br", "example.com"],
        });

        result.IsSuccess.ShouldBeTrue();
        result.Value!.LinkDomainsAllowed.ShouldBe(["montebravo.com.br", "example.com"]);
    }

    [Theory]
    [InlineData("https://montebravo.com.br")]
    [InlineData("montebravo.com.br/path")]
    [InlineData("montebravo")]
    [InlineData("")]
    public void Rejects_link_domains_that_are_not_bare_host_names(string domain)
    {
        Result<Template> result = Template.Create(Key, Metadata() with { LinkDomainsAllowed = [domain] });

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }

    [Fact]
    public void A_host_is_allowed_when_it_matches_an_allowed_domain_or_one_of_its_subdomains()
    {
        Template template = Template.Create(Key, Metadata() with
        {
            LinkDomainsAllowed = ["montebravo.com.br"],
        }).Value!;

        template.IsLinkDomainAllowed("montebravo.com.br").ShouldBeTrue();
        template.IsLinkDomainAllowed("app.montebravo.com.br").ShouldBeTrue();
        template.IsLinkDomainAllowed("MONTEBRAVO.com.br").ShouldBeTrue();
        template.IsLinkDomainAllowed("evilmontebravo.com.br").ShouldBeFalse();
        template.IsLinkDomainAllowed("montebravo.com.br.evil.io").ShouldBeFalse();
        template.IsLinkDomainAllowed("example.com").ShouldBeFalse();
    }

    private static TemplateMetadata Metadata() => new()
    {
        Application = "araia-cambio",
        Class = NotificationClass.Critical,
        OwnerTeam = "identity-squad",
        Purpose = "authentication",
        LegalBasis = "execucao-de-contrato",
    };
}
