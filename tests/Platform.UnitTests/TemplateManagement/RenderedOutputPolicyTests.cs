using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// The order of the four steps the output policy runs, read directly instead of
/// through a render. Each step has behavior tests of its own elsewhere; what
/// only these can see is which one answers first, and the order is the whole
/// design: three of the four cases below pass under a policy that runs the same
/// four steps in the wrong sequence.
/// <para>
/// Every literal that carries an invisible character is written with escapes on
/// purpose. The characters under test do not survive a round trip through an
/// editor, and a source file is not a trustworthy place to keep them.
/// </para>
/// </summary>
public sealed class RenderedOutputPolicyTests
{
    /// <summary>The composed accent, so the expected text is unambiguous.</summary>
    private const string Codigo = "Código";

    [Fact]
    public void An_authentication_sms_link_outside_the_allowlist_is_refused_as_a_banned_link()
    {
        // Both refusals are available here, and which one answers is not a
        // detail of wording. The consuming module recognizes the security
        // refusal by comparing the whole error text against its code, and maps
        // everything else to a generic render failure: if the destination guard
        // answered first, the real reason would be gone by the time anyone read
        // it, and a phishing link in an authentication SMS would be filed as a
        // template that failed to render.
        const string Body = Codigo + " 998877 https://evil.example.io/otp";
        Template template = MakeTemplate(TemplatePurposes.Authentication);

        // Premise: the destination guard refuses this same text on its own, so
        // what the assertion below reads is precedence and not the absence of a
        // second refusal.
        Result guard = RenderedDestinationPolicy.Validate(template, Channel.Sms, null, Body, null);
        guard.IsFailure.ShouldBeTrue();
        DomainError.Describe(guard.Error, guard.ErrorKind).Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);

        Result<RenderedOutput> result = RenderedOutputPolicy.Apply(
            template,
            Channel.Sms,
            new RenderedFields(null, Body, null),
            RefusalShape.Bare,
            AuthenticationLinkBan.Enforce);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(TemplateValidation.AuthenticationSmsLinkCode);
    }

    [Fact]
    public void An_authentication_sms_link_that_only_appears_after_normalization_is_refused()
    {
        // The direction that catches a real defect. A zero width joiner splits
        // the host so the ban sees nothing, and the normalizer then removes it,
        // because a joiner carries no text. Running the ban over the untouched
        // render would hand the carrier a clean, clickable shortener that no
        // check ever looked at.
        const string Raw = Codigo + " 998877 bit.l\u200Dy/x9k2p";
        const string Normalized = Codigo + " 998877 bit.ly/x9k2p";
        Template template = MakeTemplate(TemplatePurposes.Authentication, "bit.ly");

        // Premises, in the order they have to hold. The detector does not see
        // the split text; the normalizer puts the link back together; and the
        // shortener is inside the allowed domains, so the destination guard
        // cannot be the one refusing below.
        TemplateValidation.ContainsLinkLikeText(Raw).ShouldBeFalse();
        SmsContentNormalizer.Normalize(Raw).ShouldBe(Normalized);
        TemplateValidation.ContainsLinkLikeText(Normalized).ShouldBeTrue();
        RenderedDestinationPolicy.Validate(template, Channel.Sms, null, Normalized, null)
            .IsSuccess.ShouldBeTrue();

        Result<RenderedOutput> result = RenderedOutputPolicy.Apply(
            template,
            Channel.Sms,
            new RenderedFields(null, Raw, null),
            RefusalShape.Bare,
            AuthenticationLinkBan.Enforce);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(TemplateValidation.AuthenticationSmsLinkCode);
    }

    [Fact]
    public void The_hash_describes_the_normalized_text_and_not_the_untouched_render()
    {
        // An audit verifies a stored message by re-rendering it and comparing
        // hashes, so a hash taken before the channel rewrote the text marks
        // every SMS as tampered with.
        const string Raw = "Co\u0301digo 998877\u200B\r\nva\u0301lido";
        const string Normalized = Codigo + " 998877 válido";

        // Premise: the untouched render is not already in the shipped form, or
        // every assertion below would hold with no normalizer at all.
        Raw.ShouldNotBe(Normalized);

        Result<RenderedOutput> result = RenderedOutputPolicy.Apply(
            MakeTemplate("order-updates"),
            Channel.Sms,
            new RenderedFields(null, Raw, null),
            RefusalShape.Bare,
            AuthenticationLinkBan.Enforce);

        result.IsSuccess.ShouldBeTrue(result.Error);
        RenderedOutput output = result.Value!;

        // The hash is asserted before the body, because both fail if the
        // normalization moves and this is the one that names the harm.
        output.ContentHash.ShouldBe(CanonicalHash.OfFields(null, Normalized, null));
        output.ContentHash.ShouldNotBe(CanonicalHash.OfFields(null, Raw, null));
        output.Body.ShouldBe(Normalized);
    }

    [Fact]
    public void A_pass_that_already_answered_the_ban_ships_content_the_enforcing_pass_refuses()
    {
        // The premise the masked render rests on, which until now lived only in
        // prose: the second pass over the same content skips the ban. It is
        // sound because masking replaces a value with a fixed marker, so it can
        // remove a link and never write one.
        const string Body = Codigo + " 998877 https://montebravo.com.br/otp";
        Template template = MakeTemplate(TemplatePurposes.Authentication);
        var fields = new RenderedFields(null, Body, null);

        Result<RenderedOutput> enforced = RenderedOutputPolicy.Apply(
            template, Channel.Sms, fields, RefusalShape.Bare, AuthenticationLinkBan.Enforce);

        enforced.IsFailure.ShouldBeTrue();
        enforced.Error.ShouldBe(TemplateValidation.AuthenticationSmsLinkCode);

        Result<RenderedOutput> skipped = RenderedOutputPolicy.Apply(
            template, Channel.Sms, fields, RefusalShape.Bare, AuthenticationLinkBan.AlreadyEnforced);

        skipped.IsSuccess.ShouldBeTrue(skipped.Error);
        skipped.Value!.Body.ShouldBe(Body);
    }

    [Fact]
    public void The_bare_refusal_is_the_word_and_the_formatted_one_adds_a_sentence_that_quotes_nothing()
    {
        // The bare shape is a consumer contract: a sibling module compares the
        // whole error text against the code, so a prefix, a separator or a
        // sentence around it collapses a security refusal into a render
        // failure. The formatted shape is for a person, and it names the rule
        // without repeating what tripped it, because at this point the text is
        // the recipient's own data.
        const string Body = Codigo + " 998877 https://montebravo.com.br/otp?ticket=probe_9f3c1";
        Template template = MakeTemplate(TemplatePurposes.Authentication);
        var fields = new RenderedFields(null, Body, null);

        Result<RenderedOutput> bare = RenderedOutputPolicy.Apply(
            template, Channel.Sms, fields, RefusalShape.Bare, AuthenticationLinkBan.Enforce);

        bare.IsFailure.ShouldBeTrue();
        bare.ErrorKind.ShouldBe(ResultErrorKind.Validation);
        bare.Error.ShouldBe(TemplateValidation.AuthenticationSmsLinkCode);

        Result<RenderedOutput> formatted = RenderedOutputPolicy.Apply(
            template, Channel.Sms, fields, RefusalShape.Formatted, AuthenticationLinkBan.Enforce);

        formatted.IsFailure.ShouldBeTrue();
        formatted.ErrorKind.ShouldBe(ResultErrorKind.Validation);

        // Same code, and a payload the bare shape does not carry.
        DomainErrorInfo error = DomainError.Describe(formatted.Error, formatted.ErrorKind);
        error.Code.ShouldBe(TemplateValidation.AuthenticationSmsLinkCode);
        formatted.Error.ShouldNotBe(TemplateValidation.AuthenticationSmsLinkCode);
        error.Detail.ShouldNotBeNullOrWhiteSpace();
        error.Detail.ShouldNotContain("probe_9f3c1");
        error.Detail.ShouldNotContain("montebravo.com.br");
    }

    [Fact]
    public void A_channel_without_a_normalizer_keeps_its_text_and_takes_no_link_ban()
    {
        // Falsification for the two cases above: what refuses them is the
        // channel plus the purpose, not the policy refusing whatever it is
        // given. The same purpose on email carries a link, a joiner and a line
        // break through untouched, and the hash describes exactly that.
        const string Subject = Codigo + " de acesso";
        const string Body =
            "<p>Có\u200Ddigo 998877.\r\nAcesse <a href='https://montebravo.com.br/otp'>aqui</a></p>";
        const string BodyText = "Código 998877.\r\nAcesse https://montebravo.com.br/otp";

        // Premise: an SMS of this purpose would be refused over this text, so
        // the success below is about the channel and not about the content.
        TemplateValidation.ContainsLinkLikeText(BodyText).ShouldBeTrue();

        Result<RenderedOutput> result = RenderedOutputPolicy.Apply(
            MakeTemplate(TemplatePurposes.Authentication),
            Channel.Email,
            new RenderedFields(Subject, Body, BodyText),
            RefusalShape.Bare,
            AuthenticationLinkBan.Enforce);

        result.IsSuccess.ShouldBeTrue(result.Error);
        RenderedOutput output = result.Value!;
        output.Subject.ShouldBe(Subject);
        output.Body.ShouldBe(Body);
        output.BodyText.ShouldBe(BodyText);
        output.ContentHash.ShouldBe(CanonicalHash.OfFields(Subject, Body, BodyText));
    }

    private static Template MakeTemplate(string purpose, params string[] linkDomainsAllowed)
        => Template.Create(TemplateKey.Create("orders.status.changed").Value!, new TemplateMetadata
        {
            Application = "araia-cambio",
            Class = NotificationClass.Transactional,
            OwnerTeam = "growth-squad",
            Purpose = purpose,
            LegalBasis = "execucao-de-contrato",
            DefaultLocale = Locale.Create("pt-BR").Value,
            LinkDomainsAllowed = linkDomainsAllowed.Length == 0
                ? ["montebravo.com.br"]
                : linkDomainsAllowed,
        }).Value!;
}
