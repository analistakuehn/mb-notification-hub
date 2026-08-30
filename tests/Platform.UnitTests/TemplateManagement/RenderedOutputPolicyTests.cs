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
            AuthenticationLinkBan.Enforce,
            RenderedSizeCeiling.Enforce);

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
            AuthenticationLinkBan.Enforce,
            RenderedSizeCeiling.Enforce);

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
            AuthenticationLinkBan.Enforce,
            RenderedSizeCeiling.Enforce);

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
            template,
            Channel.Sms,
            fields,
            RefusalShape.Bare,
            AuthenticationLinkBan.Enforce,
            RenderedSizeCeiling.Enforce);

        enforced.IsFailure.ShouldBeTrue();
        enforced.Error.ShouldBe(TemplateValidation.AuthenticationSmsLinkCode);

        Result<RenderedOutput> skipped = RenderedOutputPolicy.Apply(
            template,
            Channel.Sms,
            fields,
            RefusalShape.Bare,
            AuthenticationLinkBan.AlreadyEnforced,
            RenderedSizeCeiling.Enforce);

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
            template,
            Channel.Sms,
            fields,
            RefusalShape.Bare,
            AuthenticationLinkBan.Enforce,
            RenderedSizeCeiling.Enforce);

        bare.IsFailure.ShouldBeTrue();
        bare.ErrorKind.ShouldBe(ResultErrorKind.Validation);
        bare.Error.ShouldBe(TemplateValidation.AuthenticationSmsLinkCode);

        Result<RenderedOutput> formatted = RenderedOutputPolicy.Apply(
            template,
            Channel.Sms,
            fields,
            RefusalShape.Formatted,
            AuthenticationLinkBan.Enforce,
            RenderedSizeCeiling.Enforce);

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
            AuthenticationLinkBan.Enforce,
            RenderedSizeCeiling.Enforce);

        result.IsSuccess.ShouldBeTrue(result.Error);
        RenderedOutput output = result.Value!;
        output.Subject.ShouldBe(Subject);
        output.Body.ShouldBe(Body);
        output.BodyText.ShouldBe(BodyText);
        output.ContentHash.ShouldBe(CanonicalHash.OfFields(Subject, Body, BodyText));
    }

    [Fact]
    public void An_sms_render_above_ten_segments_is_refused_with_a_stable_code()
    {
        // The ceiling is counted in what the carrier bills, and the code is the
        // bare word because a sibling module compares the whole error text
        // against it.
        var body = new string('a', 1531);
        Template template = MakeTemplate("order-updates");

        // Premises: nothing else in the policy has anything to refuse here, so
        // the refusal below is the ceiling and not one of its neighbours.
        TemplateValidation.ContainsLinkLikeText(body).ShouldBeFalse();
        RenderedDestinationPolicy.Validate(template, Channel.Sms, null, body, null)
            .IsSuccess.ShouldBeTrue();

        Result<RenderedOutput> result = RenderedOutputPolicy.Apply(
            template,
            Channel.Sms,
            new RenderedFields(null, body, null),
            RefusalShape.Bare,
            AuthenticationLinkBan.Enforce,
            RenderedSizeCeiling.Enforce);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(RenderedContentRejectionReasons.TooLarge);
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }

    [Fact]
    public void An_sms_render_of_fifteen_hundred_and_thirty_plain_characters_is_admitted()
    {
        // Falsification for the refusal above. One character shorter is the
        // largest message the ceiling admits, so the test pair tells a working
        // ceiling from a policy that refuses long text in general.
        var body = new string('a', 1530);

        Result<RenderedOutput> result = RenderedOutputPolicy.Apply(
            MakeTemplate("order-updates"),
            Channel.Sms,
            new RenderedFields(null, body, null),
            RefusalShape.Bare,
            AuthenticationLinkBan.Enforce,
            RenderedSizeCeiling.Enforce);

        result.IsSuccess.ShouldBeTrue(result.Error);
        result.Value!.Body.Length.ShouldBe(1530);
    }

    [Fact]
    public void The_size_ceiling_answers_after_the_authentication_link_ban()
    {
        // Both refusals are available, and which one answers decides what an
        // operator learns. If the size answered first, nobody would find out
        // that this authentication SMS also carried a link, and the producer
        // would shorten the text until the real refusal finally surfaced.
        var body = new string('a', 1531) + " https://montebravo.com.br/otp";
        Template template = MakeTemplate(TemplatePurposes.Authentication);

        // Premises: the text really is above the ceiling and really does carry
        // a link, so this is precedence and not the absence of one of them.
        SmsSegmentCeiling.Admits(body).ShouldBeFalse();
        TemplateValidation.ContainsLinkLikeText(body).ShouldBeTrue();

        Result<RenderedOutput> result = RenderedOutputPolicy.Apply(
            template,
            Channel.Sms,
            new RenderedFields(null, body, null),
            RefusalShape.Bare,
            AuthenticationLinkBan.Enforce,
            RenderedSizeCeiling.Enforce);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(TemplateValidation.AuthenticationSmsLinkCode);
    }

    [Fact]
    public void The_size_ceiling_answers_after_the_destination_guard()
    {
        // Same precedence question against the other security check. A
        // destination outside the allowlist is a fact about where the message
        // sends people, and losing it behind a capacity complaint would file a
        // link to a host nobody approved as a message that was merely too long.
        var body = new string('a', 1531) + " https://evil.example.io/x";
        Template template = MakeTemplate("order-updates");

        // Premises: above the ceiling, and refused on its own by the guard.
        SmsSegmentCeiling.Admits(body).ShouldBeFalse();
        Result guard = RenderedDestinationPolicy.Validate(template, Channel.Sms, null, body, null);
        guard.IsFailure.ShouldBeTrue();

        Result<RenderedOutput> result = RenderedOutputPolicy.Apply(
            template,
            Channel.Sms,
            new RenderedFields(null, body, null),
            RefusalShape.Bare,
            AuthenticationLinkBan.Enforce,
            RenderedSizeCeiling.Enforce);

        result.IsFailure.ShouldBeTrue();
        DomainError.Describe(result.Error, result.ErrorKind).Code.ShouldBe(ErrorCodes.UrlDomainNotAllowed);
    }

    [Fact]
    public void The_size_ceiling_answers_before_the_canonical_hash()
    {
        // No output ever leaves this policy describing content the ceiling
        // refused. An audit re-renders a stored message and compares hashes, so
        // a hash over a message that was never allowed to exist would be a
        // record of something nobody sent.
        var refused = new string('a', 1531);
        var admitted = new string('a', 1530);
        Template template = MakeTemplate("order-updates");

        Result<RenderedOutput> above = RenderedOutputPolicy.Apply(
            template,
            Channel.Sms,
            new RenderedFields(null, refused, null),
            RefusalShape.Bare,
            AuthenticationLinkBan.Enforce,
            RenderedSizeCeiling.Enforce);

        above.IsFailure.ShouldBeTrue();
        above.Value.ShouldBeNull();

        // The contrast that makes the line above worth asserting: one character
        // less and the policy does produce the hash, so what withheld it was
        // the ceiling and not a policy that never hashes.
        Result<RenderedOutput> within = RenderedOutputPolicy.Apply(
            template,
            Channel.Sms,
            new RenderedFields(null, admitted, null),
            RefusalShape.Bare,
            AuthenticationLinkBan.Enforce,
            RenderedSizeCeiling.Enforce);

        within.IsSuccess.ShouldBeTrue(within.Error);
        within.Value!.ContentHash.ShouldBe(CanonicalHash.OfFields(null, admitted, null));
    }

    [Fact]
    public void Composition_expands_the_text_before_the_ceiling_measures_it()
    {
        // Composing to the shipped form makes text longer as well as shorter. A
        // ceiling measured on the untouched render would admit this one in a
        // single comparison and hand the carrier three times the text.
        var raw = new string('\ufb2c', 400);
        var normalized = SmsContentNormalizer.Normalize(raw);
        Template template = MakeTemplate("order-updates");

        // Premises: the untouched render is short enough that the shortcut
        // would admit it without counting, and the composed form is three times
        // as long.
        raw.Length.ShouldBe(400);
        SmsSegmentCeiling.Admits(raw).ShouldBeTrue();
        normalized.Length.ShouldBe(1200);

        Result<RenderedOutput> result = RenderedOutputPolicy.Apply(
            template,
            Channel.Sms,
            new RenderedFields(null, raw, null),
            RefusalShape.Bare,
            AuthenticationLinkBan.Enforce,
            RenderedSizeCeiling.Enforce);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(RenderedContentRejectionReasons.TooLarge);
    }

    [Fact]
    public void Composition_shortens_the_text_before_the_ceiling_measures_it()
    {
        // The other direction, and the one that costs a producer a legitimate
        // message. Measured untouched this render is past the point where no
        // encoding can fit, so a ceiling that ran first would refuse a message
        // that occupies six segments.
        var raw = string.Concat(Enumerable.Repeat("a\u0300", 800));
        var normalized = SmsContentNormalizer.Normalize(raw);
        Template template = MakeTemplate("order-updates");

        // Premises: untouched it is refused outright by length alone, and
        // composed it is an ordinary GSM message of six segments.
        raw.Length.ShouldBe(1600);
        SmsSegmentCeiling.Admits(raw).ShouldBeFalse();
        normalized.Length.ShouldBe(800);
        SmsSegmentCount.Of(normalized).ShouldBe(6);

        Result<RenderedOutput> result = RenderedOutputPolicy.Apply(
            template,
            Channel.Sms,
            new RenderedFields(null, raw, null),
            RefusalShape.Bare,
            AuthenticationLinkBan.Enforce,
            RenderedSizeCeiling.Enforce);

        result.IsSuccess.ShouldBeTrue(result.Error);
        result.Value!.Body.Length.ShouldBe(800);
    }

    [Fact]
    public void The_masked_form_is_not_measured_against_the_ceiling()
    {
        // Masking may make a field longer, because the marker is three
        // characters and the value it replaces can be one: a single-digit
        // authentication code costs two characters more once masked. The
        // masked form is the copy a trail may store and never the message, so
        // measuring it would refuse a message that fits over the size of its
        // own audit record.
        var message = new string('a', 1530);
        var maskedForm = message[..1529] + VariableMasking.MaskedValue;
        Template template = MakeTemplate("order-updates");

        // Premise: masking really did make it longer, and past the ceiling.
        maskedForm.Length.ShouldBe(1532);
        SmsSegmentCeiling.Admits(message).ShouldBeTrue();
        SmsSegmentCeiling.Admits(maskedForm).ShouldBeFalse();

        Result<RenderedOutput> exempt = RenderedOutputPolicy.Apply(
            template,
            Channel.Sms,
            new RenderedFields(null, maskedForm, null),
            RefusalShape.Bare,
            AuthenticationLinkBan.AlreadyEnforced,
            RenderedSizeCeiling.Exempt);

        exempt.IsSuccess.ShouldBeTrue(exempt.Error);
        exempt.Value!.Body.Length.ShouldBe(1532);

        // What the success above is worth: the same text under the enforcing
        // pass is refused, so the exemption is what admitted it and not the
        // ceiling failing to answer.
        Result<RenderedOutput> enforced = RenderedOutputPolicy.Apply(
            template,
            Channel.Sms,
            new RenderedFields(null, maskedForm, null),
            RefusalShape.Bare,
            AuthenticationLinkBan.Enforce,
            RenderedSizeCeiling.Enforce);

        enforced.IsFailure.ShouldBeTrue();
        enforced.Error.ShouldBe(RenderedContentRejectionReasons.TooLarge);
    }

    [Fact]
    public void The_pinned_layout_counts_toward_the_rendered_segments()
    {
        // What leaves the platform is the content inside its wrapper, and the
        // caller frames the body before handing it here. A ceiling that saw
        // only the interpolated content would let a heavy layout push every
        // message that pins it past what the carrier delivers, while every
        // template author measured a body that fits.
        var content = new string('a', 100);
        var framed = new string('b', 1450) + content;
        Template template = MakeTemplate("order-updates");

        // Premise: the content on its own is nowhere near the ceiling, so what
        // refuses the framed body below is the wrapper's own text.
        Result<RenderedOutput> unframed = RenderedOutputPolicy.Apply(
            template,
            Channel.Sms,
            new RenderedFields(null, content, null),
            RefusalShape.Bare,
            AuthenticationLinkBan.Enforce,
            RenderedSizeCeiling.Enforce);

        unframed.IsSuccess.ShouldBeTrue(unframed.Error);

        Result<RenderedOutput> result = RenderedOutputPolicy.Apply(
            template,
            Channel.Sms,
            new RenderedFields(null, framed, null),
            RefusalShape.Bare,
            AuthenticationLinkBan.Enforce,
            RenderedSizeCeiling.Enforce);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(RenderedContentRejectionReasons.TooLarge);
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
