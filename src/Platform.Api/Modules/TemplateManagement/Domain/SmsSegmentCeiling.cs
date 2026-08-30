namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// The ceiling on how large a rendered SMS may be, expressed in the unit the
/// carrier bills and the recipient receives: segments, counted over the text
/// after the channel normalizer has rewritten it.
/// <para>
/// The number is derived and the derivation is written down, so the day the
/// input to it is confirmed or changes, the change is one constant and the
/// justification is already here.
/// </para>
/// </summary>
public static class SmsSegmentCeiling
{
    /// <summary>
    /// Characters of an SMS body a provider request is assumed to accept.
    /// <para>
    /// This is an inference about the provider limit and it is NOT verified
    /// anywhere in this repository. The same 1600 already governs the template
    /// source at publication, which is a different measurement over a
    /// different thing, and the two agreeing is not evidence that either is the
    /// provider's real number.
    /// </para>
    /// </summary>
    private const int AssumedProviderBodyChars = 1600;

    /// <summary>
    /// Segments a rendered SMS may occupy, derived as
    /// <c>floor(AssumedProviderBodyChars / Gsm7ConcatenatedSeptets)</c>, which
    /// is <c>floor(1600 / 153) = 10</c>.
    /// <para>
    /// Ten is what makes the two numbers one rule instead of two unrelated
    /// ceilings: <c>153 * 10 = 1530 &lt;= 1600</c>, so nothing this ceiling
    /// admits can overflow the assumed body limit in any encoding. Eleven would
    /// give <c>153 * 11 = 1683 &gt; 1600</c> and the two would contradict each
    /// other.
    /// </para>
    /// <para>
    /// It is the largest defensible ceiling and not a spending policy. Whoever
    /// pays the carrier bill may lower it; raising it needs the assumed limit
    /// confirmed first, and then the derivation above gives the new value.
    /// </para>
    /// </summary>
    public const int MaxSegments = AssumedProviderBodyChars / SmsSegmentCount.Gsm7ConcatenatedSeptets;

    /// <summary>
    /// Length up to which no text can exceed the ceiling, so the answer is one
    /// comparison. The worst case per unit is a concatenated UCS-2 segment
    /// whose boundary lands inside a surrogate pair, which carries 66 units,
    /// giving <c>10 * 66 = 660</c>.
    /// <para>
    /// The nominal UCS-2 rate of 67 would put this at 670 and would be wrong in
    /// the direction that matters: 331 astral characters are 662 units and cost
    /// 11 segments, so a shortcut at 670 admits without measuring a message the
    /// ceiling exists to refuse. A shortcut on the admitting side has to be
    /// conservative, because being wrong there is a bypass and not a rounding
    /// error. Text longer than this and no longer than <see cref="NeverWithin"/>
    /// simply runs the counter, which answers exactly.
    /// </para>
    /// <para>
    /// The bound is sound and conservative by exactly one unit: every text of
    /// 661 units also fits, because the last segment has nothing after it to
    /// split and therefore takes its full 67, while 662 units of astral text
    /// cost eleven segments. The derived form is kept anyway. It falls out of
    /// the ceiling and the capacity and can be rechecked by hand whenever
    /// either changes, and one unit of admitted length is not worth a constant
    /// that no arithmetic explains.
    /// </para>
    /// <para>
    /// GSM-7 is not the worst case at this length and cannot become it. The
    /// most expensive GSM text is every character from the extension table, at
    /// 76 characters per concatenated segment once the escape sequence is kept
    /// whole, so 660 of them are nine segments against the ten that 660 units
    /// of astral text cost. The bound therefore stays governed by UCS-2.
    /// </para>
    /// </summary>
    public const int AlwaysWithin = MaxSegments * SmsSegmentCount.Ucs2ConcatenatedUnitsWorstCase;

    /// <summary>
    /// Length above which no text can stay within the ceiling, so the answer is
    /// again one comparison. The best case per unit is GSM-7 with every
    /// character in the basic table, one septet each, at 153 septets per
    /// concatenated segment, giving <c>10 * 153 = 1530</c>: one character more
    /// costs an eleventh segment however it is encoded.
    /// </summary>
    public const int NeverWithin = MaxSegments * SmsSegmentCount.Gsm7ConcatenatedSeptets;

    /// <summary>
    /// Whether the text stays within the ceiling. The counter only runs on the
    /// window between the two bounds above, which is what makes the rule
    /// affordable on the render path.
    /// </summary>
    public static bool Admits(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= AlwaysWithin)
        {
            return true;
        }

        return text.Length <= NeverWithin && SmsSegmentCount.Of(text) <= MaxSegments;
    }
}
