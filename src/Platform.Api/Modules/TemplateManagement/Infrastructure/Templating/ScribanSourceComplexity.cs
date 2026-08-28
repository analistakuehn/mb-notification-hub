using Scriban.Parsing;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

/// <summary>Which admission ceiling a source crossed, measured before the parse.</summary>
internal enum SourceComplexityLimit
{
    /// <summary>Nothing was crossed, and the source may be parsed.</summary>
    None,

    /// <summary>The whole source carries more tokens than the ceiling allows.</summary>
    Tokens,

    /// <summary>One code block carries more tokens than the ceiling allows.</summary>
    CodeBlockTokens,
}

/// <summary>
/// Admission check for a template source, applied before it is parsed because
/// the parse is the one phase of an engine call that nothing can stop: the
/// parser takes no cancellation token, and the render deadline only starts once
/// the parse has returned.
/// </summary>
/// <remarks>
/// Measured on this module's own ceilings, parse cost follows the token count
/// and almost nothing else: roughly 0.8 us per token plus 0.005 us per
/// character, an estimate that over-predicts every shape probed, which is the
/// safe direction for a ceiling. The character term is already bounded by
/// <see cref="TemplatingOptions.MaxTemplateSizeChars"/>, so the token term is
/// the one still missing. Bounding size alone does not stand in for it: at the
/// same 131072 characters, plain text parses in 0.6 ms and a single member
/// chain in 92 ms, a factor of 150 between two sources of identical size.
/// <para>
/// The per-block ceiling bounds a different quantity, how deep one expression
/// nests. The engine stops nested expressions at its own depth limit, but a
/// left-associative postfix chain (<c>a.b.c</c>, <c>a[0][0]</c>) is parsed in a
/// loop and never reaches that counter, so it grows to whatever the source
/// affords. The parse survives it; everything that walks the tree afterwards
/// recurses once per link and does not. Measured against this engine, a chain
/// of 1500 links, a source of 3007 characters, kills the process with a stack
/// overflow while rendering, and one of 16000 links does it while analyzing. A
/// stack overflow cannot be caught, so the only place that stops it is here,
/// before the tree exists. Every syntax node costs at least one token, so a
/// ceiling on the tokens of one block is a ceiling on the depth inside it.
/// </para>
/// <para>
/// The scan runs the engine's own lexer rather than a private one, for the same
/// reason the sandbox builtin surface is derived from the engine default: a
/// second implementation drifts, and this one would drift into a bypass. A
/// hand-written counter of what sits between <c>{{</c> and <c>}}</c> reads the
/// <c>}}</c> inside a string literal as the end of the block, and everything
/// after it as literal text, so <c>{{ "}}" + a.b.c...</c> would measure as a
/// handful of characters and parse as the chain it is.
/// </para>
/// </remarks>
internal static class ScribanSourceComplexity
{
    /// <summary>
    /// The first ceiling the source crosses, or <see cref="SourceComplexityLimit.None"/>.
    /// The scan stops at the crossing, so a source written to be expensive pays
    /// the ceiling and never its own length.
    /// </summary>
    internal static SourceComplexityLimit Exceeded(string source, int maxTokens, int maxCodeBlockTokens)
    {
        ArgumentNullException.ThrowIfNull(source);

        // No token is shorter than one character, so a source shorter than the
        // tightest ceiling cannot reach it. This is what keeps the dispatch path
        // free: the sources a notification renders are subjects and bodies of
        // tens to hundreds of characters, and none of them is ever scanned.
        if (source.Length <= Math.Min(maxTokens, maxCodeBlockTokens))
        {
            return SourceComplexityLimit.None;
        }

        var tokens = 0;
        var blockTokens = 0;
        var insideCodeBlock = false;

        foreach (Token token in new Lexer(source))
        {
            // End of input is a position, not a token the parser builds from.
            if (token.Type == TokenType.Eof)
            {
                break;
            }

            if (++tokens > maxTokens)
            {
                return SourceComplexityLimit.Tokens;
            }

            switch (token.Type)
            {
                case TokenType.CodeEnter:
                case TokenType.LiquidTagEnter:
                    insideCodeBlock = true;
                    blockTokens = 0;
                    break;
                case TokenType.CodeExit:
                case TokenType.LiquidTagExit:
                    insideCodeBlock = false;
                    break;
                default:
                    // Literal text is one token per run and never nests, so only
                    // what the parser turns into expressions is counted here.
                    if (insideCodeBlock && ++blockTokens > maxCodeBlockTokens)
                    {
                        return SourceComplexityLimit.CodeBlockTokens;
                    }

                    break;
            }
        }

        return SourceComplexityLimit.None;
    }
}
