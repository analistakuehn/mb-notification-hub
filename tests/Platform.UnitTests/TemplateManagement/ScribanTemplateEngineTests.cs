using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class ScribanTemplateEngineTests
{
    private static ScribanTemplateEngine Engine(
        int loopLimit = 1000,
        int recursionLimit = 16,
        int maxSizeChars = 10_000,
        int timeoutMs = 2000)
        => new(Options.Create(new TemplatingOptions
        {
            LoopLimit = loopLimit,
            RecursionLimit = recursionLimit,
            MaxTemplateSizeChars = maxSizeChars,
            RenderTimeoutMilliseconds = timeoutMs,
        }));

    private static JsonElement Variables(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Analysis_collects_root_variables_and_ignores_loop_variables_members_and_builtins()
    {
        TemplateSourceAnalysis analysis = Engine().Analyze(
            "Olá {{ user.name }}, código {{ string.upcase code }}."
            + "{{ for item in items }}{{ item.label }}{{ end }}",
            "body");

        analysis.ParseSucceeded.ShouldBeTrue();
        analysis.UsedVariables.ShouldBe(["code", "items", "user"]);
    }

    [Fact]
    public void Analysis_ignores_template_local_assignments_and_function_names()
    {
        TemplateSourceAnalysis analysis = Engine().Analyze(
            "{{ total = amount + fee }}{{ func label(x)\nret x\nend }}{{ label total }}",
            "body");

        analysis.ParseSucceeded.ShouldBeTrue();
        analysis.UsedVariables.ShouldBe(["amount", "fee"]);
    }

    [Fact]
    public void A_broken_template_reports_the_parse_error_instead_of_variables()
    {
        TemplateSourceAnalysis analysis = Engine().Analyze("Código {{ if }}", "body");

        analysis.ParseSucceeded.ShouldBeFalse();
        analysis.ParseError!.ShouldContain("if statement");
        analysis.UsedVariables.ShouldBeEmpty();
    }

    [Fact]
    public void A_template_over_the_size_limit_is_rejected_before_parsing()
    {
        TemplateSourceAnalysis analysis = Engine(maxSizeChars: 10).Analyze(new string('x', 11), "body");

        analysis.ParseSucceeded.ShouldBeFalse();
        analysis.ParseError!.ShouldContain("character limit");
    }

    [Fact]
    public async Task Rendering_substitutes_nested_variable_data()
    {
        Result<string> result = await Engine().RenderAsync(
            "Olá {{ user.name }}, seu código é {{ code }}.",
            Variables("""{ "user": { "name": "Ana" }, "code": "123456" }"""),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("Olá Ana, seu código é 123456.");
    }

    [Fact]
    public async Task Reflection_style_member_access_yields_no_type_information()
    {
        Result<string> result = await Engine().RenderAsync(
            "{{ payload.GetType }}{{ payload.get_type }}",
            Variables("""{ "payload": "hello" }"""),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Chained_reflection_access_fails_without_reaching_dotnet_types()
    {
        Result<string> result = await Engine().RenderAsync(
            "{{ payload.get_type.assembly }}",
            Variables("""{ "payload": "hello" }"""),
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.ShouldContain("null object");
        result.Error!.ShouldNotContain("System.");
    }

    [Fact]
    public async Task A_runaway_loop_is_stopped_by_the_engine_loop_limit()
    {
        Result<string> result = await Engine(loopLimit: 10).RenderAsync(
            "{{ for i in 1..100000 }}x{{ end }}",
            variables: null,
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.ShouldContain("LoopLimit");
    }

    [Fact]
    public async Task Unbounded_recursion_is_stopped_by_the_engine_recursion_limit()
    {
        Result<string> result = await Engine(recursionLimit: 8).RenderAsync(
            "{{ func f(n)\nret f(n)\nend\nf 1 }}",
            variables: null,
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.ShouldContain("recursive depth limit");
    }

    [Fact]
    public async Task A_render_over_the_wall_clock_limit_is_discarded()
    {
        Result<string> result = await Engine(loopLimit: 10_000_000, timeoutMs: 1).RenderAsync(
            "{{ for i in 1..9000000 }}{{ i }}{{ end }}",
            variables: null,
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.ShouldContain("time limit");
    }

    [Fact]
    public async Task A_template_over_the_size_limit_is_not_rendered()
    {
        Result<string> result = await Engine(maxSizeChars: 10).RenderAsync(
            new string('x', 11),
            variables: null,
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.ShouldContain("character limit");
    }

    [Fact]
    public async Task An_undeclared_variable_fails_the_render_with_its_name()
    {
        Result<string> result = await Engine().RenderAsync(
            "Olá {{ nome }}",
            Variables("""{ "outra": "x" }"""),
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.ShouldContain("nome");
    }
}
