namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// The ceiling on how large a template source may be, counted in characters
/// over the text an author submits and before anything parses it. One number
/// governs the whole axis: the body and the text body of a template version,
/// the body and the text body of a layout version, and the ceiling the engine
/// refuses a source by.
/// <para>
/// The number is an anchor taken from measurement and not a value arithmetic
/// produces, so what is written down beside it is where each of its readings
/// came from. The day one of them moves, the change is one constant and the
/// justification is already here.
/// </para>
/// </summary>
public static class TemplateSourceSize
{
    /// <summary>
    /// Characters one template or layout source may carry.
    /// <para>
    /// This is a MEASUREMENT ANCHOR and NOT an arithmetic derivation. No
    /// division between other constants produces it, and reading it as if one
    /// did is the mistake the next paragraph names. Three independent readings
    /// put it here. The richest legitimate source ever probed is 128 KB of
    /// marketing HTML carrying 200 interpolations and 2781 tokens, so the
    /// ceiling clears real content by construction. At the very same character
    /// count, plain text parses in 0.6 ms while a single chain of member
    /// accesses parses in 92 ms, which says the cost of a source follows its
    /// shape and never its length: length is the wrong knob to spend on parse
    /// cost, and the token ceilings are what bound it. And 131072 is the size
    /// this module was sized around on 2026-08-27, when those readings were
    /// taken.
    /// </para>
    /// <para>
    /// It is NOT the largest number the parse memoization could admit, which
    /// is 208411 characters, and the distance between the two is the point.
    /// That number is the remainder of a division between five constants of
    /// the memoization, one of which the memoization itself declares will move
    /// on the next engine upgrade, and the hypothesis that produces it, two
    /// parsed nodes per source character, is unreal by a factor of 25.6: the
    /// densest shape the token ceiling admits delivers 0.078 nodes per
    /// character. A safety ceiling whose renumbering is already announced is
    /// not a ceiling.
    /// </para>
    /// </summary>
    public const int MaxChars = 131_072;
}
