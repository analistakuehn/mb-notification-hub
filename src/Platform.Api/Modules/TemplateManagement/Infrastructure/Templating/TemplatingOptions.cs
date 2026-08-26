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

    // Bounded by the parse memoization budget, and bound at compile time on
    // purpose. A source larger than that budget is refused by the store
    // without a signal and reparsed on every single call, which reads as a
    // slow renderer and never as a misconfiguration. Failing at startup is the
    // loud version of the same fact.
    [Range(1, ScribanParseCache.MaxSourceChars)]
    public int MaxTemplateSizeChars { get; init; } = 131_072;

    [Range(1, 60_000)]
    public int RenderTimeoutMilliseconds { get; init; } = 2000;

    /// <summary>
    /// Ceiling for the rendered output. Loops multiplying large fragments can
    /// inflate the output far beyond the template size; the render aborts the
    /// moment the accumulated output crosses this limit.
    /// </summary>
    [Range(1, 16_000_000)]
    public int MaxOutputChars { get; init; } = 1_000_000;
}
