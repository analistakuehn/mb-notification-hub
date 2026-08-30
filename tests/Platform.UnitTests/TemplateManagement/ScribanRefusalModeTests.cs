using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using Scriban;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// The mode each sandbox limit reports beside the refusal it produces. The
/// engine knows the mode and never the identity of what it rendered, so this is
/// the only place the mapping itself can be pinned.
/// </summary>
public sealed class ScribanRefusalModeTests
{
    private static ScribanTemplateEngine Engine(
        int loopLimit = 1000,
        int recursionLimit = 16,
        int maxSizeChars = 10_000,
        int timeoutMs = 2000,
        int maxOutputChars = 1_000_000,
        int maxTemplateTokens = 8192,
        int maxCodeBlockTokens = 512)
        => new(Options.Create(new TemplatingOptions
        {
            LoopLimit = loopLimit,
            RecursionLimit = recursionLimit,
            MaxTemplateSizeChars = maxSizeChars,
            RenderTimeoutMilliseconds = timeoutMs,
            MaxOutputChars = maxOutputChars,
            MaxTemplateTokens = maxTemplateTokens,
            MaxCodeBlockTokens = maxCodeBlockTokens,
        }), new ScribanParseCache());

    private static JsonElement Variables(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task A_render_that_finishes_reports_no_refusal_at_all()
    {
        TemplateRenderOutcome outcome = await Engine().RenderOutcomeAsync(
            "Olá {{ name }}",
            Variables("""{"name":"Ana"}"""),
            CancellationToken.None);

        outcome.Result.IsSuccess.ShouldBeTrue(outcome.Result.Error);
        outcome.Result.Value.ShouldBe("Olá Ana");
        outcome.Refusal.ShouldBe(TemplateRefusal.None);
    }

    [Fact]
    public async Task A_source_over_the_character_ceiling_reports_the_size_mode()
    {
        // The ceiling has to be tightened here to be reached at all: it ships
        // equal to the source ceiling the aggregates enforce on a write, so no
        // stored source can cross it, and configuration is bounded so it cannot
        // be raised above it either. What is being pinned is the mapping, not
        // the reachability.
        TemplateRenderOutcome outcome = await Engine(maxSizeChars: 10).RenderOutcomeAsync(
            new string('x', 11),
            variables: null,
            CancellationToken.None);

        outcome.Result.IsFailure.ShouldBeTrue();
        outcome.Refusal.ShouldBe(TemplateRefusal.SourceSize);
    }

    [Fact]
    public async Task A_source_over_the_token_ceiling_reports_the_source_token_mode()
    {
        TemplateRenderOutcome outcome = await Engine(maxTemplateTokens: 4).RenderOutcomeAsync(
            "{{ a.b.c.d.e }}",
            variables: null,
            CancellationToken.None);

        outcome.Result.IsFailure.ShouldBeTrue();
        outcome.Refusal.ShouldBe(TemplateRefusal.SourceTokens);
    }

    [Fact]
    public async Task An_expression_over_the_block_ceiling_reports_the_block_token_mode()
    {
        TemplateRenderOutcome outcome = await Engine(maxCodeBlockTokens: 4).RenderOutcomeAsync(
            "{{ a.b.c.d.e }}",
            variables: null,
            CancellationToken.None);

        outcome.Result.IsFailure.ShouldBeTrue();
        outcome.Refusal.ShouldBe(TemplateRefusal.SourceCodeBlockTokens);
    }

    [Fact]
    public async Task A_source_the_parser_rejects_reports_the_parse_mode()
    {
        TemplateRenderOutcome outcome = await Engine().RenderOutcomeAsync(
            "{{ 1 + }}",
            variables: null,
            CancellationToken.None);

        outcome.Result.IsFailure.ShouldBeTrue();
        outcome.Refusal.ShouldBe(TemplateRefusal.ParseFailed);
    }

    [Fact]
    public async Task A_render_over_the_wall_clock_deadline_reports_the_time_mode()
    {
        TemplateRenderOutcome outcome = await Engine(loopLimit: 10_000_000, timeoutMs: 1)
            .RenderOutcomeAsync(
                "{{ for i in 1..9000000 }}{{ i }}{{ end }}",
                variables: null,
                CancellationToken.None);

        outcome.Result.IsFailure.ShouldBeTrue();
        outcome.Refusal.ShouldBe(TemplateRefusal.TimeLimit);
    }

    [Fact]
    public async Task A_catastrophic_regex_reports_the_time_mode_on_every_run()
    {
        // Repeated on purpose. The engine's regex timeout and the render
        // deadline are the same number, so which of the two stops the render is
        // decided by timer resolution: measured, this shape splits about evenly
        // between the two doors. A single run would pass by luck half the time
        // against an implementation that only names one of them.
        ScribanTemplateEngine engine = Engine(timeoutMs: 200);
        var modes = new List<TemplateRefusal>();
        for (var run = 0; run < 20; run++)
        {
            TemplateRenderOutcome outcome = await engine.RenderOutcomeAsync(
                """{{ "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!" | regex.match `(a+)+$` }}""",
                variables: null,
                CancellationToken.None);

            outcome.Result.IsFailure.ShouldBeTrue();
            modes.Add(outcome.Refusal);
        }

        modes.ShouldAllBe(mode => mode == TemplateRefusal.TimeLimit);
    }

    [Fact]
    public async Task Output_past_the_ceiling_reports_the_output_mode()
    {
        TemplateRenderOutcome outcome = await Engine(maxOutputChars: 1_000).RenderOutcomeAsync(
            "{{ for i in 1..100 }}{{ for j in 1..100 }}0123456789{{ end }}{{ end }}",
            variables: null,
            CancellationToken.None);

        outcome.Result.IsFailure.ShouldBeTrue();
        outcome.Refusal.ShouldBe(TemplateRefusal.OutputLimit);
    }

    [Fact]
    public async Task A_runaway_loop_reports_the_unclassified_mode()
    {
        TemplateRenderOutcome outcome = await Engine(loopLimit: 10).RenderOutcomeAsync(
            "{{ for i in 1..100000 }}x{{ end }}",
            variables: null,
            CancellationToken.None);

        outcome.Result.IsFailure.ShouldBeTrue();
        outcome.Refusal.ShouldBe(TemplateRefusal.Unclassified);
    }

    /// <summary>
    /// The one mode that holds more than it names is held there by an engine
    /// this test pins. Nothing else notices an upgrade: the catalogue keeps
    /// compiling and every case above keeps passing, so without this the
    /// residual would outlive the reason for it silently.
    /// </summary>
    [Fact]
    public void The_engine_is_pinned_to_the_version_the_unclassified_mode_was_measured_against()
    {
        // The file version, not the assembly version: this package ships every
        // 7.x under assembly version 7.0.0.0, so pinning that would be a green
        // that spans the whole major and sees no upgrade at all.
        var pinned = typeof(Template).Assembly
            .GetCustomAttribute<AssemblyFileVersionAttribute>()!
            .Version;

        string.Equals(pinned, "7.2.6.0", StringComparison.Ordinal).ShouldBeTrue(
            $"o motor de template mudou para {pinned}. Antes de mover este pino, verifique se a "
            + "versão nova expõe subtipo de exceção, hook virtual utilizável ou tipo de nó que não "
            + "colida para o limite de laço, o limite de recursão e o erro de autor em render. Se "
            + "expuser qualquer um deles, reabra a discriminação e divida o modo Unclassified.");
    }
}
