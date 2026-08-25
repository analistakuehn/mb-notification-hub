using System.Globalization;
using System.Text;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

/// <summary>
/// Puts rendered SMS text into the single form this hub is willing to hand a
/// carrier. It runs at render time, never at the adapter, because the audited
/// content hash has to describe the exact bytes the provider received: an
/// adapter that rewrote the text would leave the trail describing a message
/// nobody sent.
/// <para>
/// Three transformations, each with its own reason. Composed form (NFC),
/// because the same accented word written decomposed costs extra characters,
/// changes the encoding the carrier picks and hashes differently, so the same
/// message would look like two. No control characters, because a bidi override
/// or a zero-width joiner inside an authentication message is a spoofing
/// device and never content: this is what makes the rule pay for the emoji
/// sequences it also removes. No line breaks, because a carrier is free to
/// re-wrap them and the segment count stops being predictable.
/// </para>
/// </summary>
internal static class SmsContentNormalizer
{
    /// <summary>
    /// Normalizes one rendered field. A line break becomes a single space
    /// instead of vanishing, because deleting it would weld the last word of
    /// one line to the first of the next; every other control character is
    /// dropped outright, since none of them is content. Runs of whitespace
    /// then collapse and the result is trimmed, so the same message written
    /// with different spacing produces one text and one hash.
    /// </summary>
    internal static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return text;

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (IsSeparator(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (IsDiscardable(rune)) continue;

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(rune);
        }

        var stripped = builder.ToString();
        try
        {
            return stripped.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Unpaired surrogates cannot be normalized. The text is already
            // stripped, and refusing the render over a broken code unit would
            // trade a deliverable message for none.
            return stripped;
        }
    }

    /// <summary>
    /// Whitespace of any kind, plus the line and paragraph separators the
    /// Unicode tables classify apart from it. They all collapse to one space.
    /// </summary>
    private static bool IsSeparator(Rune rune)
        => Rune.IsWhiteSpace(rune)
            || Rune.GetUnicodeCategory(rune) is UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator;

    /// <summary>
    /// Characters that carry no text: the C0 and C1 controls and the format
    /// characters, which is where zero-width joiners, soft hyphens and the
    /// bidirectional overrides live.
    /// </summary>
    private static bool IsDiscardable(Rune rune)
        => Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format;
}
