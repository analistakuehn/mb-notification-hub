using System.ComponentModel.DataAnnotations;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

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

    // Bounded on both ends by the domain, because both ends have a failure
    // mode and neither of them is the memoization.
    //
    // Above: the source ceiling is what the aggregates enforce, and it is not
    // configurable. A number above it asks for a limit nothing downstream
    // honors, so the write would be refused before the render ever read this
    // value, and the operator who raised it would see no effect.
    //
    // Below: a subject is source the engine analyzes, so a ceiling under the
    // longest subject a version may carry opens a dead band on the subject
    // axis: the write is accepted, and then the analysis refuses the version
    // with a message that calls the subject a template.
    //
    // The parse memoization used to be the upper bound here, and that check
    // moved rather than disappeared. It is a compile-time assertion beside the
    // memoization itself, which is strictly better: the tie between the source
    // ceiling and the memoization budget now breaks the build instead of
    // breaking a deploy, and nothing about it depends on a host starting.
    [Range(TemplateVersion.MaxSubjectLength, TemplateSourceSize.MaxChars)]
    public int MaxTemplateSizeChars { get; init; } = TemplateSourceSize.MaxChars;

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
