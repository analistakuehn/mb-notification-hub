using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

/// <summary>
/// Execution limits of the template sandbox. Defaults are deliberate ceilings
/// for authoring-time compilation and preview rendering; configuration can
/// tighten or relax them per environment without a deploy of code.
/// </summary>
public sealed class TemplatingOptions
{
    public const string SectionName = "Modules:TemplateManagement:Templating";

    [Range(1, 1_000_000)]
    public int LoopLimit { get; init; } = 1000;

    [Range(1, 1000)]
    public int RecursionLimit { get; init; } = 64;

    // Bounded by the parse memoization, and bound at compile time on purpose.
    // A source that alone outweighs the budget is refused by the store without
    // a signal and reparsed on every single call, which reads as a slow
    // renderer and never as a misconfiguration. Failing at startup is the loud
    // version of the same fact.
    //
    // The bound is no longer the budget itself. The budget is denominated in
    // bytes of memory while this ceiling counts characters, and the two are not
    // the same currency: a character costs two bytes as text and up to a couple
    // of hundred more as parsed tree, so what the store can promise to hold is
    // the length whose worst case still fits. The default sits well inside it
    // and the range only refuses a configuration that would ask for more.
    [Range(1, ScribanParseCache.MaxMemoizableSourceChars)]
    public int MaxTemplateSizeChars { get; init; } = 131_072;

    [Range(1, 60_000)]
    public int RenderTimeoutMilliseconds { get; init; } = 2000;

    /// <summary>
    /// Tokens one source may carry, counted by the engine's own lexer before
    /// the source is parsed. The render deadline above starts only once the
    /// parse has returned, and the parse takes no cancellation token, so this
    /// is what bounds it: measured, parse cost follows the token count at
    /// roughly 0.8 us per token, which puts the default near 7 ms of parse for
    /// the most expensive shape that still fits. The richest legitimate source
    /// probed, 128 KB of marketing HTML with 200 interpolations and loops, is
    /// 2781 tokens.
    /// </summary>
    [Range(1, 1_000_000)]
    public int MaxTemplateTokens { get; init; } = 8192;

    /// <summary>
    /// Tokens one code block may carry. Every syntax node costs at least one
    /// token, so this bounds how deep a single expression nests, and with it
    /// the recursion of everything that walks the parsed tree. The default
    /// admits an expression some 250 levels deep, the same order as the
    /// engine's own limit for the nesting it does count, and sits far under the
    /// depth measured to overflow the stack.
    /// </summary>
    [Range(1, 100_000)]
    public int MaxCodeBlockTokens { get; init; } = 512;

    /// <summary>
    /// Ceiling for the rendered output. Loops multiplying large fragments can
    /// inflate the output far beyond the template size; the render aborts the
    /// moment the accumulated output crosses this limit.
    /// </summary>
    [Range(1, 16_000_000)]
    public int MaxOutputChars { get; init; } = 1_000_000;
}
