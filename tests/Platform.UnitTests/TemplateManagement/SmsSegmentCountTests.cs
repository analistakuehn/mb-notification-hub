using System.Text;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// What the segment counter answers, and the two shortcuts that let the render
/// path avoid calling it. Every accented or invisible character is written as
/// an escape and every long text is generated, because a source file is not a
/// trustworthy place to keep either.
/// </summary>
public sealed class SmsSegmentCountTests
{
    /// <summary>An emoji, which is one character in two UTF-16 units.</summary>
    private const string Astral = "\U0001F600";

    /// <summary>An accented letter outside the GSM tables, which forces UCS-2.</summary>
    private const string OutsideGsm = "\u00e3";

    /// <summary>A letter of the GSM basic table, one septet.</summary>
    private const char Basic = 'a';

    /// <summary>A character of the GSM extension table, an escape plus itself.</summary>
    private const char Extension = '{';

    [Fact]
    public void Segment_count_never_exceeds_the_utf16_upper_bound()
    {
        // The permanent oracle. Whatever the alphabet tables say, a segment
        // always carries at least the worst-case UCS-2 capacity, so the count
        // can never exceed the text divided by it. An error in either table
        // therefore cannot let a message through for less than this bound, and
        // the bound is the one the ceiling's fast path is derived from.
        //
        // The rate is the worst case and not the nominal 67: the nominal rate
        // is not an upper bound at all once surrogate pairs are in the text.
        string[] pools =
        [
            "abcXYZ 0123456789.,!?",
            "^{}[~]|\u20ac\\",
            OutsideGsm + "\u0107\u4e2d\u05d0\u0391\ufb2c",
            Astral + "\U0001F4A9\U00020000\U0001D400",
        ];

        // Recipes and not a uniform draw over the pools. A uniform draw almost
        // never produces text that is entirely one kind, and the arms that are
        // entirely one kind are exactly the ones where a rate divides badly:
        // all extension, where the escape sequence cannot be split, and all
        // astral, where the surrogate pair cannot be split.
        int[][] recipes =
        [
            [8, 1, 1, 0],
            [0, 1, 0, 0],
            [1, 1, 0, 0],
            [0, 0, 0, 1],
            [0, 0, 1, 1],
            [2, 2, 2, 2],
            [0, 0, 1, 0],
        ];

        var random = new Random(20260829);
        var violations = new List<string>();
        for (var iteration = 0; iteration < 20_000; iteration++)
        {
            var recipe = recipes[random.Next(recipes.Length)];
            var weight = recipe.Sum();
            var builder = new StringBuilder();
            var pieces = random.Next(0, 900);
            for (var piece = 0; piece < pieces; piece++)
            {
                var draw = random.Next(weight);
                var chosen = 0;
                while (draw >= recipe[chosen])
                {
                    draw -= recipe[chosen];
                    chosen++;
                }

                var pool = pools[chosen];
                builder.Append(pool[random.Next(pool.Length)]);
            }

            var text = builder.ToString();
            var counted = SmsSegmentCount.Of(text);
            var bound = CeilingDivide(text.Length, SmsSegmentCount.Ucs2ConcatenatedUnitsWorstCase);
            if (counted > bound)
            {
                violations.Add($"length {text.Length} counted {counted} bound {bound}");
            }
        }

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void A_surrogate_pair_never_splits_across_segments()
    {
        // A pair is one character in two units, and a segment that ended
        // between them would ship two halves that decode to nothing. The
        // segment gives the unit up instead, so it carries 66.
        var text = string.Concat(Enumerable.Repeat(Astral, 67));

        // Premises: the text really is 67 characters and 134 UTF-16 units, so
        // the count below is about the boundary rule and not about the length.
        text.Length.ShouldBe(134);
        text.EnumerateRunes().Count().ShouldBe(67);

        // 66 units, then 66, then 2. Dividing by the nominal rate predicts two
        // segments and bills a message that occupies three.
        SmsSegmentCount.Of(text).ShouldBe(3);
        CeilingDivide(text.Length, SmsSegmentCount.Ucs2ConcatenatedUnits).ShouldBe(2);
    }

    [Fact]
    public void The_fast_path_lower_bound_is_sound_and_conservative_by_one_unit()
    {
        // Three lengths, because two would not say what this bound is. It is
        // derived as ten segments times the worst-case capacity, and the
        // derivation is deliberately kept over a hand-tuned number even though
        // it gives up one unit.
        SmsSegmentCeiling.AlwaysWithin.ShouldBe(660);

        // At the bound the most expensive text there is still fits.
        var atBound = string.Concat(Enumerable.Repeat(Astral, 330));
        atBound.Length.ShouldBe(SmsSegmentCeiling.AlwaysWithin);
        SmsSegmentCount.Of(atBound).ShouldBe(SmsSegmentCeiling.MaxSegments);
        SmsSegmentCeiling.Admits(atBound).ShouldBeTrue();

        // One unit past the bound every text still fits, so the shortcut is
        // conservative rather than exact. The reason is that the last segment
        // has nothing after it to split, so it takes its full 67 units: ten
        // segments reach 661 units but not 662. The bound stays at 660 because
        // it falls out of the ceiling and the capacity and can be checked by
        // hand, while 661 would be a number with no derivation behind it.
        var oneUnitPast = string.Concat(Enumerable.Repeat(Astral, 330)) + Basic;
        oneUnitPast.Length.ShouldBe(661);
        SmsSegmentCount.Of(oneUnitPast).ShouldBe(SmsSegmentCeiling.MaxSegments);

        // Two units past, a text does exceed the ceiling, so the shortcut could
        // not be widened by two and the counter is what has to answer here.
        var pastBound = string.Concat(Enumerable.Repeat(Astral, 331));
        pastBound.Length.ShouldBe(662);
        SmsSegmentCount.Of(pastBound).ShouldBe(SmsSegmentCeiling.MaxSegments + 1);
        SmsSegmentCeiling.Admits(pastBound).ShouldBeFalse();
    }

    [Fact]
    public void The_fast_path_upper_bound_is_the_largest_length_that_can_still_fit()
    {
        // The mirror of the bound above. The cheapest a character can possibly
        // be is one septet, so at the bound the best possible text exactly
        // fills the ceiling and one character more cannot be carried by any
        // encoding.
        SmsSegmentCeiling.NeverWithin.ShouldBe(1530);

        var atBound = new string(Basic, SmsSegmentCeiling.NeverWithin);
        SmsSegmentCount.Of(atBound).ShouldBe(SmsSegmentCeiling.MaxSegments);
        SmsSegmentCeiling.Admits(atBound).ShouldBeTrue();

        var pastBound = new string(Basic, SmsSegmentCeiling.NeverWithin + 1);
        SmsSegmentCount.Of(pastBound).ShouldBe(SmsSegmentCeiling.MaxSegments + 1);
        SmsSegmentCeiling.Admits(pastBound).ShouldBeFalse();
    }

    [Fact]
    public void An_escape_sequence_never_splits_across_segments()
    {
        // The GSM twin of the surrogate rule. A character of the extension
        // table travels as an escape plus itself, and the pair is indivisible,
        // so a segment holding 152 of its 153 septets gives the last position
        // up rather than split it: 76 such characters per segment, not 76 and
        // a half.
        var atBound = new string(Extension, 760);
        var pastBound = new string(Extension, 761);

        // Premises: both are past the single-segment allowance, so the
        // concatenated rate is what governs, and the two differ by one
        // character and nothing else.
        (760 * 2).ShouldBeGreaterThan(SmsSegmentCount.Gsm7SingleSeptets);
        SmsSegmentCount.Gsm7ConcatenatedExtensionChars.ShouldBe(76);

        // Both sides, or the test cannot tell this boundary from a looser one.
        SmsSegmentCount.Of(atBound).ShouldBe(10);
        SmsSegmentCount.Of(pastBound).ShouldBe(11);

        // What the line above is worth: dividing the septet total by the rate
        // predicts ten for a text that occupies eleven, and the ceiling would
        // have admitted it.
        (((761 * 2) + 152) / 153).ShouldBe(10);
        SmsSegmentCeiling.Admits(pastBound).ShouldBeFalse();
    }

    [Fact]
    public void The_gsm_arm_is_never_the_worst_case_at_either_fast_path_bound()
    {
        // Both shortcuts are derived from UCS-2, so they are only sound while
        // no GSM text of the same length costs more. The expensive GSM text is
        // every character from the extension table, and it is asserted here
        // rather than assumed, because the escape rule changed what it costs.
        var extensionAtLowerBound = new string(Extension, SmsSegmentCeiling.AlwaysWithin);
        var astralAtLowerBound = string.Concat(Enumerable.Repeat(Astral, SmsSegmentCeiling.AlwaysWithin / 2));

        extensionAtLowerBound.Length.ShouldBe(astralAtLowerBound.Length);
        SmsSegmentCount.Of(extensionAtLowerBound).ShouldBe(9);
        SmsSegmentCount.Of(astralAtLowerBound).ShouldBe(10);

        // And at the upper bound the cheapest character is still the basic one,
        // so no text of that length can cost fewer than ten segments.
        var basicAtUpperBound = new string(Basic, SmsSegmentCeiling.NeverWithin);
        var extensionAtUpperBound = new string(Extension, SmsSegmentCeiling.NeverWithin);

        SmsSegmentCount.Of(basicAtUpperBound).ShouldBe(SmsSegmentCeiling.MaxSegments);
        SmsSegmentCount.Of(extensionAtUpperBound)
            .ShouldBeGreaterThan(SmsSegmentCeiling.MaxSegments);
    }

    [Fact]
    public void The_ceiling_is_ten_segments_derived_from_the_concatenated_gsm_rate()
    {
        // The derivation, pinned so a hand-edited constant cannot drift from
        // the rule that produced it, and so the arithmetic that makes the two
        // limits one rule stays visible.
        SmsSegmentCeiling.MaxSegments.ShouldBe(10);
        (SmsSegmentCount.Gsm7ConcatenatedSeptets * SmsSegmentCeiling.MaxSegments)
            .ShouldBeLessThanOrEqualTo(1600);
        (SmsSegmentCount.Gsm7ConcatenatedSeptets * (SmsSegmentCeiling.MaxSegments + 1))
            .ShouldBeGreaterThan(1600);
    }

    [Fact]
    public void One_character_outside_the_gsm_table_more_than_doubles_the_segments()
    {
        // The carrier re-encodes the whole message, not the offending
        // character, which is why the producer guide tells an author that an
        // accent can more than double what the same text costs.
        var plain = new string(Basic, 300);
        var accented = plain[..299] + OutsideGsm;

        accented.Length.ShouldBe(plain.Length);
        SmsSegmentCount.Of(plain).ShouldBe(2);
        SmsSegmentCount.Of(accented).ShouldBe(5);
    }

    [Fact]
    public void An_extension_character_costs_two_septets_and_a_basic_one_costs_one()
    {
        // Falsification for the table lookups: without the two-septet rule the
        // second text would count the same as the first, and without the basic
        // table both would be UCS-2 and count differently again.
        var basic = new string(Basic, 160);
        var extension = new string('{', 80);

        SmsSegmentCount.Of(basic).ShouldBe(1);
        SmsSegmentCount.Of(extension).ShouldBe(1);
        SmsSegmentCount.Of(basic + Basic).ShouldBe(2);
        SmsSegmentCount.Of(extension + '{').ShouldBe(2);
    }

    [Fact]
    public void Empty_text_costs_no_segment()
    {
        // Zero and not one, so the upper bound of the oracle stays total: a
        // count of one over a text of no units would exceed every bound
        // derived from its length.
        SmsSegmentCount.Of(string.Empty).ShouldBe(0);
        SmsSegmentCeiling.Admits(string.Empty).ShouldBeTrue();
    }

    private static int CeilingDivide(int value, int divisor) => (value + divisor - 1) / divisor;
}
