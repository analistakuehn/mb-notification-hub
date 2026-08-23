using System.Text;
using NotificationHub.Api.Modules.ContactConsent.Domain;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Privacy;

/// <summary>
/// Reduces a plaintext contact value to the shape a query surface may show.
/// The rule is per channel and deterministic, so the same stored value always
/// masks to the same string: an e-mail keeps the first character of the local
/// part and the whole domain, a phone number keeps the last four digits and a
/// leading country marker. The masked form is what leaves this module; the
/// plaintext is opened here and discarded.
/// </summary>
internal static class ContactValueMask
{
    private const char MaskCharacter = '*';
    private const int VisibleTrailingCharacters = 4;

    internal static string Apply(string channel, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(value);

        return channel == ContactChannels.Email ? MaskEmail(value) : MaskTrailing(value);
    }

    /// <summary>
    /// Keeps the first character of the local part and the domain: the domain
    /// is what tells a support agent which mailbox family answered, and the
    /// local part is what identifies the person.
    /// </summary>
    private static string MaskEmail(string value)
    {
        var separator = value.LastIndexOf('@');
        if (separator <= 0)
        {
            return MaskTrailing(value);
        }

        var local = value[..separator];
        var domain = value[separator..];
        var visible = local.Length > 1 ? local[..1] : string.Empty;
        return $"{visible}{new string(MaskCharacter, local.Length - visible.Length)}{domain}";
    }

    /// <summary>
    /// Keeps the last four characters, the ones a support agent confirms with
    /// the customer, plus a leading plus sign when the number carries one.
    /// Everything shorter than the visible tail masks whole.
    /// </summary>
    private static string MaskTrailing(string value)
    {
        if (value.Length <= VisibleTrailingCharacters)
        {
            return new string(MaskCharacter, value.Length);
        }

        var hasCountryMarker = value[0] == '+';
        var maskedLength = value.Length - VisibleTrailingCharacters - (hasCountryMarker ? 1 : 0);
        var builder = new StringBuilder(value.Length);
        if (hasCountryMarker)
        {
            builder.Append('+');
        }

        builder.Append(MaskCharacter, maskedLength);
        builder.Append(value.AsSpan(value.Length - VisibleTrailingCharacters));
        return builder.ToString();
    }
}
