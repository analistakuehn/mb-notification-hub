using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;

/// <summary>
/// What the provider accepts for one call, and the arithmetic that says how
/// much of it a set spends.
/// <para>
/// These are the provider's own numbers and nobody else's. How many
/// attachments a notification may carry and how many raw bytes they may add up
/// to is a product rule, owned and measured by the context that owns
/// attachments and settled before this call; what is settled here is whether
/// the message this adapter is about to compose fits what this provider takes.
/// The two questions have different owners and different units, and answering
/// the product one here would be a second arithmetic over the same rule, free
/// to disagree with the first the day either number moves.
/// </para>
/// </summary>
internal static class SendGridMailLimits
{
    /// <summary>Total message size the provider accepts, headers and body included.</summary>
    internal const long MaxMessageBytes = 30_000_000;

    /// <summary>
    /// Room kept for what the transport adds around the body: request line,
    /// authorization, content type and length. The ceiling above is on the
    /// whole message and the measurement below is of the body alone, so the
    /// reserve is what keeps the two comparable. It is the same reserve the
    /// conservative reading of this ceiling already uses.
    /// </summary>
    internal const long HeaderReserveBytes = 102_400;

    /// <summary>The body this adapter allows itself to compose.</summary>
    internal const long MaxBodyBytes = MaxMessageBytes - HeaderReserveBytes;

    /// <summary>The one disposition this hub sends an accepted set under.</summary>
    internal const string AttachmentDisposition = "attachment";

    /// <summary>Base64 expands three raw bytes into four characters, padded.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative.</exception>
    internal static long Base64Length(long rawBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rawBytes);
        return 4 * ((rawBytes + 2) / 3);
    }
}

/// <summary>
/// One attachment of a composed body: which content it carries, how many raw
/// bytes it promises, and how many bytes of body that becomes.
/// </summary>
internal sealed record SendGridAttachmentSlot(
    string ContentIdentity,
    long RawLength,
    long EncodedLength);

/// <summary>
/// One Mail Send body, cut into the literal parts and the attachment slots
/// between them, with the exact length it will measure on the wire.
/// <para>
/// The body is composed once, with a placeholder standing in for each
/// attachment, and then cut at those placeholders. What this leaves is a body
/// whose envelope is byte for byte the one the serializer produces, and whose
/// attachment fields are written straight onto the connection as they are
/// read. The whole message therefore never exists in memory, at any size, and
/// the cost of a send stops depending on what it carries.
/// </para>
/// <para>
/// The placeholder is raw bytes, like the content it stands in for, so the
/// needle to cut on is the base64 of it. Cutting on the encoded form is what
/// keeps the cut honest: a write call that expanded the field would not leave
/// the needle in the body at all, and the composition refuses instead of
/// quietly describing a message nobody sends.
/// </para>
/// </summary>
internal sealed class SendGridMailBody
{
    /// <summary>
    /// Bytes of a placeholder. A multiple of three, so its base64 carries no
    /// padding and cannot end on a boundary shared with the text around it.
    /// </summary>
    private const int MarkerBytes = 15;

    private SendGridMailBody(
        byte[][] segments,
        SendGridAttachmentSlot[] attachments,
        long declaredLength)
    {
        Segments = segments;
        Attachments = attachments;
        DeclaredLength = declaredLength;
    }

    /// <summary>The literal parts, one more than there are attachments.</summary>
    internal IReadOnlyList<byte[]> Segments { get; }

    /// <summary>The attachments, in the order the set was accepted in.</summary>
    internal IReadOnlyList<SendGridAttachmentSlot> Attachments { get; }

    /// <summary>
    /// Exactly how many bytes the body measures, known before a byte of it
    /// moves. It is declared to the transport, so the provider is told the
    /// size of what it is receiving whatever version of the protocol is
    /// negotiated, and a body that ran short cannot read as a complete
    /// message.
    /// </summary>
    internal long DeclaredLength { get; }

    /// <summary>
    /// Composes the body of one send, attachments included, and measures it
    /// against what the provider accepts.
    /// <para>
    /// This is the one place a Mail Send body is assembled. A send that
    /// carries no attachment is the same composition with no slots in it, and
    /// its bytes are the ones the serializer produces on its own: two
    /// assemblies of the same message would be two shapes free to drift, and
    /// the drift would land on the message this hub actually sends.
    /// </para>
    /// <para>
    /// The measurement happens here, before anything is opened and before
    /// anything is called, and it costs one serialization of an envelope that
    /// carries placeholders rather than content. A set that does not fit is
    /// refused with no call behind it and no byte read out of custody.
    /// </para>
    /// </summary>
    internal static SendGridBodyComposition Compose(
        EmailDeliveryTarget target,
        EmailMessage message,
        SendGridOptions config,
        DispatchCorrelation? correlation,
        AcceptedAttachmentSet? attachments)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(config);

        AcceptedAttachment[] accepted = attachments is null ? [] : [.. attachments];
        var needles = new byte[accepted.Length][];
        var wire = new SendGridAttachment[accepted.Length];
        for (var index = 0; index < accepted.Length; index++)
        {
            // Unpredictable per send, and not merely unique. A placeholder a
            // sender could guess would let content chosen by that sender carry
            // the needle the cut looks for, and the body would be cut inside
            // the message instead of at the field.
            var marker = RandomNumberGenerator.GetBytes(MarkerBytes);
            needles[index] = Encoding.UTF8.GetBytes(Convert.ToBase64String(marker));
            wire[index] = new SendGridAttachment(
                new SendGridAttachmentContent(marker),
                accepted[index].Name,
                accepted[index].MediaType,
                SendGridMailLimits.AttachmentDisposition);
        }

        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            SendGridChannelProvider.BuildRequest(
                target, message, config, correlation, accepted.Length == 0 ? null : wire),
            SendGridChannelProvider.BodySerialization);
        var segments = Cut(serialized, needles);
        return Measure(segments, accepted);
    }

    /// <summary>
    /// Measures the cut body against the ceiling and turns it into slots.
    /// <para>
    /// Counted down from the ceiling rather than up to it. A sum of the
    /// lengths could overflow before it was ever compared, and an overflowed
    /// sum reads as a small number, which is the one wrong answer this
    /// measurement must not be able to give: it would clear for sending
    /// exactly the message that cannot be sent.
    /// </para>
    /// </summary>
    private static SendGridBodyComposition Measure(
        byte[][] segments,
        AcceptedAttachment[] accepted)
    {
        var remaining = SendGridMailLimits.MaxBodyBytes;
        foreach (var segment in segments)
        {
            remaining -= segment.Length;
        }

        // The envelope alone can be past the ceiling, with no attachment
        // involved at all, so it is measured rather than assumed away.
        if (remaining < 0)
        {
            return SendGridBodyComposition.OverProviderCeiling();
        }

        var slots = new SendGridAttachmentSlot[accepted.Length];
        for (var index = 0; index < accepted.Length; index++)
        {
            // The raw length is compared first, and it is what makes the
            // encoded length safe to compute: base64 never shrinks, so a raw
            // length past what is left is already past it encoded, and the
            // multiplication that follows can no longer overflow.
            var raw = accepted[index].Length;
            if (raw > remaining)
            {
                return SendGridBodyComposition.OverProviderCeiling();
            }

            var encoded = SendGridMailLimits.Base64Length(raw);
            if (encoded > remaining)
            {
                return SendGridBodyComposition.OverProviderCeiling();
            }

            remaining -= encoded;
            slots[index] = new SendGridAttachmentSlot(
                accepted[index].ContentIdentity, raw, encoded);
        }

        return SendGridBodyComposition.Composed(new SendGridMailBody(
            segments, slots, SendGridMailLimits.MaxBodyBytes - remaining));
    }

    /// <summary>
    /// Cuts the serialized body at each needle, in order, and answers with the
    /// literal parts around them.
    /// <para>
    /// A needle that is not there, and a needle that is there more than once,
    /// are both refused. The first says the write call did not put the field
    /// on the wire in the form this cut assumes; the second says something
    /// else in the message carries the same bytes, and cutting on either
    /// occurrence would send a body assembled in an order nobody composed.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A needle is absent from the serialized body, or appears in it more than
    /// once.
    /// </exception>
    internal static byte[][] Cut(byte[] body, byte[][] needles)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(needles);

        var segments = new byte[needles.Length + 1][];
        var offset = 0;
        for (var index = 0; index < needles.Length; index++)
        {
            var needle = needles[index];
            var found = body.AsSpan(offset).IndexOf(needle);
            if (found < 0 || body.AsSpan(offset + found + needle.Length).IndexOf(needle) >= 0)
            {
                throw new InvalidOperationException(
                    "O campo de conteúdo do anexo não aparece exatamente uma vez no corpo "
                    + "serializado na forma codificada; o recorte não é confiável e a "
                    + "mensagem não é composta.");
            }

            segments[index] = body[offset..(offset + found)];
            offset += found + needle.Length;
        }

        segments[^1] = body[offset..];
        return segments;
    }
}

/// <summary>
/// What composing one body answered. A composition that fits carries the body;
/// one that does not carries none, and the two cannot be confused because
/// there is no way to build an outcome that mixes them.
/// </summary>
internal sealed class SendGridBodyComposition
{
    private SendGridBodyComposition(SendGridMailBody? body) => Body = body;

    /// <summary>
    /// The composed body, or nothing when the message is larger than one call
    /// may carry.
    /// </summary>
    internal SendGridMailBody? Body { get; }

    internal static SendGridBodyComposition Composed(SendGridMailBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new SendGridBodyComposition(body);
    }

    /// <summary>
    /// The message this send would compose is larger than one call may carry.
    /// It is answered before anything is opened and before anything is called,
    /// which is what makes it a refusal rather than a failure.
    /// </summary>
    internal static SendGridBodyComposition OverProviderCeiling() => new(null);
}
