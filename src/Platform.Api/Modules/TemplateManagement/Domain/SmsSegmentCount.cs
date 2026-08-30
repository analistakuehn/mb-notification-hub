using System.Buffers;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Counts the segments a carrier splits one SMS into. Pure arithmetic over the
/// text, with no knowledge of any provider: what it answers is how many parts
/// the text costs, and whoever holds a budget decides what to do with the
/// number.
/// <para>
/// The alphabet decides the rate. Text that fits the GSM 03.38 tables travels
/// as septets; anything else forces the carrier to re-encode the whole message
/// as UCS-2, which is why a single accent outside the GSM table can more than
/// double the cost of a message that did not change otherwise.
/// </para>
/// <para>
/// Both alphabets are segmented by walking the text, and neither is counted by
/// dividing a total by a capacity. Each alphabet has a unit that may not be
/// split across a boundary, the surrogate pair on one side and the escape
/// sequence on the other, so a segment that cannot fit the next whole unit
/// gives its last position up. Division ignores that and undercounts, and a
/// count that is short by one segment is what lets a budget be exceeded by a
/// message the budget admitted.
/// </para>
/// </summary>
public static class SmsSegmentCount
{
    /// <summary>Septets an unconcatenated GSM-7 message carries.</summary>
    public const int Gsm7SingleSeptets = 160;

    /// <summary>
    /// Septets a concatenated GSM-7 segment carries. The seven missing from
    /// the 160 are the header that tells the handset how to reassemble parts.
    /// </summary>
    public const int Gsm7ConcatenatedSeptets = 153;

    /// <summary>
    /// Characters of the extension table a concatenated GSM-7 segment carries
    /// in the worst case. Each of them travels as an escape plus itself, and
    /// that pair may not straddle a segment boundary any more than a surrogate
    /// pair may, so a segment with one septet left gives it up rather than
    /// split the escape: 76 characters and 152 of the 153 septets.
    /// <para>
    /// This is why the count walks the text instead of dividing the septet
    /// total by the rate. Seven hundred and sixty-one extension characters are
    /// 1522 septets, which divided by 153 predicts ten segments and really
    /// occupies eleven.
    /// </para>
    /// </summary>
    public const int Gsm7ConcatenatedExtensionChars = Gsm7ConcatenatedSeptets / 2;

    /// <summary>UTF-16 units an unconcatenated UCS-2 message carries.</summary>
    public const int Ucs2SingleUnits = 70;

    /// <summary>Nominal UTF-16 units a concatenated UCS-2 segment carries.</summary>
    public const int Ucs2ConcatenatedUnits = 67;

    /// <summary>
    /// UTF-16 units a concatenated UCS-2 segment carries once the rule against
    /// splitting a surrogate pair applies, and the only number any bound over
    /// this counter may use.
    /// <para>
    /// Do not "optimize" a bound back to <see cref="Ucs2ConcatenatedUnits"/>.
    /// The 67 is the nominal capacity; a pair is one character in two units and
    /// a segment that ended between them would ship two invalid halves, so a
    /// boundary landing mid-pair costs the segment one unit. Sixty-seven emoji
    /// are 134 units: 66, then 66, then 2, which is three segments, while
    /// dividing by 67 predicts two. Over the all-astral lengths from 2 to 5000
    /// units the nominal rate understates the count 1378 times and this one
    /// never does.
    /// </para>
    /// </summary>
    public const int Ucs2ConcatenatedUnitsWorstCase = Ucs2ConcatenatedUnits - 1;

    /// <summary>
    /// The GSM 03.38 default alphabet. Every character here costs one septet.
    /// The escape slot of the table is not a character and is absent on
    /// purpose: it only ever introduces a member of the extension table below.
    /// </summary>
    private static readonly SearchValues<char> Gsm7Basic = SearchValues.Create(
        "@\u00a3$\u00a5\u00e8\u00e9\u00f9\u00ec\u00f2\u00c7\n\u00d8\u00f8\r\u00c5\u00e5"
        + "\u0394_\u03a6\u0393\u039b\u03a9\u03a0\u03a8\u03a3\u0398\u039e\u00c6\u00e6\u00df\u00c9"
        + " !\"#\u00a4%&'()*+,-./0123456789:;<=>?"
        + "\u00a1ABCDEFGHIJKLMNOPQRSTUVWXYZ\u00c4\u00d6\u00d1\u00dc\u00a7"
        + "\u00bfabcdefghijklmnopqrstuvwxyz\u00e4\u00f6\u00f1\u00fc\u00e0");

    /// <summary>
    /// The GSM 03.38 extension table. Every character here travels as the
    /// escape plus itself, so it costs two septets. A message of ordinary
    /// prose that happens to carry braces pays for them twice.
    /// </summary>
    private static readonly SearchValues<char> Gsm7Extension = SearchValues.Create(
        "\f^{}\\[~]|\u20ac");

    /// <summary>
    /// Segments the text costs. Empty text costs none, because nothing is sent
    /// and because a total answer keeps every bound over this counter total.
    /// </summary>
    public static int Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return 0;
        }

        if (TryCountGsm7Septets(text, out var septets))
        {
            return septets <= Gsm7SingleSeptets ? 1 : Gsm7ConcatenatedSegments(text);
        }

        return text.Length <= Ucs2SingleUnits ? 1 : Ucs2ConcatenatedSegments(text);
    }

    /// <summary>
    /// Whether the whole text fits the GSM tables and, if it does, what it
    /// costs in septets. One pass answers both, because a character outside the
    /// tables makes the septet count meaningless the moment it is found: the
    /// carrier re-encodes the entire message, not the offending character.
    /// </summary>
    private static bool TryCountGsm7Septets(string text, out int septets)
    {
        septets = 0;
        foreach (var character in text)
        {
            if (Gsm7Basic.Contains(character))
            {
                septets++;
            }
            else if (Gsm7Extension.Contains(character))
            {
                septets += 2;
            }
            else
            {
                septets = 0;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Walks the text into concatenated GSM-7 segments. A character that costs
    /// two septets and finds one left in the segment moves whole into the next
    /// one, because the escape and the character it introduces are a single
    /// indivisible sequence.
    /// </summary>
    private static int Gsm7ConcatenatedSegments(string text)
    {
        var segments = 1;
        var used = 0;
        foreach (var character in text)
        {
            var cost = Gsm7Basic.Contains(character) ? 1 : 2;
            if (used + cost > Gsm7ConcatenatedSeptets)
            {
                segments++;
                used = 0;
            }

            used += cost;
        }

        return segments;
    }

    /// <summary>
    /// Walks the text into concatenated UCS-2 segments. A segment takes 67
    /// units unless the last of them would be the high half of a surrogate
    /// pair, in which case the pair moves whole into the next segment and this
    /// one carries 66.
    /// </summary>
    private static int Ucs2ConcatenatedSegments(string text)
    {
        var segments = 0;
        var index = 0;
        while (index < text.Length)
        {
            var end = index + Math.Min(Ucs2ConcatenatedUnits, text.Length - index);
            if (end < text.Length
                && char.IsHighSurrogate(text[end - 1])
                && char.IsLowSurrogate(text[end]))
            {
                end--;
            }

            segments++;
            index = end;
        }

        return segments;
    }

}
