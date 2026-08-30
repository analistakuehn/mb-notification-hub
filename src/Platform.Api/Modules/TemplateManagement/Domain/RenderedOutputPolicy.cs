using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// The fields one render produced, as the engine handed them back: interpolated
/// and framed by the layout, and untouched by the channel.
/// </summary>
internal sealed record RenderedFields(string? Subject, string Body, string? BodyText);

/// <summary>
/// The fields a provider or a caller receives, and the canonical hash of
/// exactly those fields. The hash travels with the text it describes so the
/// two can never be taken from different states of the same render.
/// </summary>
internal sealed record RenderedOutput(string? Subject, string Body, string? BodyText, string ContentHash);

/// <summary>
/// How a caller needs the authentication-SMS refusal worded. The code is the
/// same either way; what changes is whether anything is wrapped around it.
/// </summary>
/// <remarks>
/// No member takes the zero value, so a caller that leaves the decision to a
/// default gets a value the policy refuses to act on instead of silently
/// getting one of the two shapes.
/// </remarks>
internal enum RefusalShape
{
    /// <summary>
    /// The bare word. A sibling module compares the whole error text against
    /// it for equality, so anything wrapped around it collapses a security
    /// refusal into an ordinary render failure.
    /// </summary>
    Bare = 1,

    /// <summary>
    /// The same code carrying a sentence, for a surface a person reads.
    /// </summary>
    Formatted = 2,
}

/// <summary>
/// Whether this pass still owes the authentication-SMS check, or whether an
/// earlier pass over the same content already ran it.
/// </summary>
/// <remarks>
/// No member takes the zero value, for the same reason as above.
/// </remarks>
internal enum AuthenticationLinkBan
{
    /// <summary>Run the check.</summary>
    Enforce = 1,

    /// <summary>
    /// Skip it, because a previous pass over the same render already refused
    /// what it would refuse. This is sound only for a derivation that can
    /// remove a link and never create one, which is what masking is: it
    /// replaces a value with a fixed marker and adds nothing to the text.
    /// </summary>
    AlreadyEnforced = 2,
}

/// <summary>
/// Whether this pass owes the size ceiling, or whether it is not the message
/// and must not be measured at all.
/// </summary>
/// <remarks>
/// No member takes the zero value, for the same reason as above.
/// <para>
/// This is a separate axis from <see cref="AuthenticationLinkBan"/> and must
/// stay separate, because the two exemptions rest on opposite facts about the
/// same derivation. The ban may be skipped on the masked form because masking
/// only ever removes a link. The ceiling must be skipped on it because masking
/// may add text: the marker is three characters, so a one-character
/// authentication code makes the masked field two characters longer than the
/// message. Deriving one from the other would let a future pass that skips the
/// ban silently stop being measured too.
/// </para>
/// </remarks>
internal enum RenderedSizeCeiling
{
    /// <summary>Measure the rendered text against what the channel carries.</summary>
    Enforce = 1,

    /// <summary>
    /// Do not measure. This exists for the masked form and nothing else: that
    /// form is the copy a trail may store and never the message a recipient
    /// receives, so its size is not a fact about anything that gets sent, and
    /// measuring it would refuse a message that fits over the length of its
    /// own audit copy.
    /// </summary>
    Exempt = 2,
}

/// <summary>
/// The last thing that happens to rendered content before anyone sees it. One
/// implementation for every render path of this module: the preview an author
/// reads and the published render a sibling module dispatches take the same
/// four steps in the same order, so the two can no longer answer differently
/// about the same text.
/// </summary>
internal static class RenderedOutputPolicy
{
    /// <summary>
    /// Normalizes for the channel, bans a link inside an authentication SMS,
    /// guards the destination, measures the result against what the channel
    /// carries, and hashes what is left.
    /// </summary>
    /// <remarks>
    /// The order is the rule, not an arrangement of it.
    /// <para>
    /// Normalization runs first because every step after it decides about the
    /// text a provider actually receives: the ban and the destination guard
    /// read the same bytes the carrier reads, and the hash has to describe
    /// them, so normalizing afterwards would leave the audit calling every SMS
    /// tampered with. The ceiling needs it for a second reason of its own:
    /// composing to the normalized form changes the length in both directions,
    /// shortening a letter written with a combining accent into one character
    /// and expanding a single precomposed code point into as many as three, so
    /// a measure taken before it refuses messages that fit and admits messages
    /// that do not.
    /// </para>
    /// <para>
    /// The ban runs before the guard because it refuses a whole class of
    /// content rather than a host.
    /// </para>
    /// <para>
    /// The ceiling runs after both of them, and that is a deliberate cost. The
    /// two checks before it are security decisions and capacity is not: if the
    /// size answered first, an operator reading "too large" would never learn
    /// that the message also carried a phishing link inside an authentication
    /// SMS, and the producer would shorten the text until the second refusal
    /// finally appeared. It runs before the hash for the same reason the others
    /// do, which is that a refused render produces no output to describe.
    /// </para>
    /// </remarks>
    internal static Result<RenderedOutput> Apply(
        Template template,
        Channel channel,
        RenderedFields fields,
        RefusalShape refusal,
        AuthenticationLinkBan ban,
        RenderedSizeCeiling ceiling)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(fields);

        var subject = fields.Subject;
        var body = fields.Body;
        var bodyText = fields.BodyText;
        if (channel == Channel.Sms)
        {
            subject = subject is null ? null : SmsContentNormalizer.Normalize(subject);
            body = SmsContentNormalizer.Normalize(body);
            bodyText = bodyText is null ? null : SmsContentNormalizer.Normalize(bodyText);
        }

        if (ban == AuthenticationLinkBan.Enforce
            && CarriesAuthenticationSmsLink(template, channel, subject, body, bodyText))
        {
            return Refuse(
                refusal,
                TemplateValidation.AuthenticationSmsLinkCode,
                "An authentication SMS may carry no link, and a variable value introduced one.");
        }

        Result destinationGuard = RenderedDestinationPolicy.Validate(template, channel, subject, body, bodyText);
        if (destinationGuard.IsFailure)
        {
            return new Result<RenderedOutput>(false, default, destinationGuard.ErrorKind, destinationGuard.Error);
        }

        if (ceiling == RenderedSizeCeiling.Enforce && ExceedsChannelCapacity(channel, body))
        {
            return Refuse(
                refusal,
                RenderedContentRejectionReasons.TooLarge,
                $"A rendered SMS may occupy at most {SmsSegmentCeiling.MaxSegments} segments, "
                + "and a variable value grew this one past that.");
        }

        return Result.Success(new RenderedOutput(
            subject,
            body,
            bodyText,
            CanonicalHash.OfFields(subject, body, bodyText)));
    }

    /// <summary>
    /// Whether this render puts something clickable inside an authentication
    /// SMS. One authentication code is the price of a false positive here; a
    /// false negative is a phishing link inside the one message people are
    /// trained to act on without thinking.
    /// </summary>
    private static bool CarriesAuthenticationSmsLink(
        Template template,
        Channel channel,
        string? subject,
        string body,
        string? bodyText)
        => channel == Channel.Sms
            && TemplatePurposes.IsAuthentication(template.Purpose)
            && (TemplateValidation.ContainsLinkLikeText(body)
                || TemplateValidation.ContainsLinkLikeText(subject)
                || TemplateValidation.ContainsLinkLikeText(bodyText));

    /// <summary>
    /// Whether the rendered text is larger than the channel carries. Only SMS
    /// answers today, and only over the body: that is the one field a carrier
    /// receives, and it already carries the layout, because the caller frames
    /// the body before handing it here. The other channels are silent on
    /// purpose rather than by omission, each for a reason recorded with the
    /// module rather than guessed at here.
    /// </summary>
    private static bool ExceedsChannelCapacity(Channel channel, string body)
        => channel == Channel.Sms && !SmsSegmentCeiling.Admits(body);

    /// <summary>
    /// The refusal never quotes what tripped it. At this point the text is the
    /// recipient's own data, and the detector answers on ordinary prose by
    /// design, so an echo would carry a stranger's message into an error
    /// response and every log derived from it. The size refusal quotes nothing
    /// either, for a plainer reason: the length of a message is a fact about
    /// the recipient's data too.
    /// </summary>
    private static Result<RenderedOutput> Refuse(RefusalShape refusal, string code, string sentence)
        => refusal switch
        {
            RefusalShape.Bare => Result.ValidationError<RenderedOutput>(code),
            RefusalShape.Formatted => Result.ValidationError<RenderedOutput>(
                DomainError.Format(code, sentence)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(refusal), refusal, "Unsupported refusal shape."),
        };
}
