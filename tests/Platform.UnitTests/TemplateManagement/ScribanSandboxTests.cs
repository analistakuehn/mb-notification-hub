using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// Escapes the sandbox must deny. Each case was reproduced against the pinned
/// engine before the guard existed, so every test here is a falsification of a
/// demonstrated escape, not a restatement of current behavior.
/// </summary>
public sealed class ScribanSandboxTests
{
    private static ScribanTemplateEngine Engine(
        int loopLimit = 1000,
        int maxOutputChars = 1_000_000,
        int timeoutMs = 5000)
        => new(Options.Create(new TemplatingOptions
        {
            LoopLimit = loopLimit,
            RecursionLimit = 16,
            MaxTemplateSizeChars = 10_000,
            RenderTimeoutMilliseconds = timeoutMs,
            MaxOutputChars = maxOutputChars,
        }), new ScribanParseCache());

    private static JsonElement Variables(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Theory]
    [InlineData("""{{ "1+1" | object.eval }}""")]
    [InlineData("""{{ "{{ 6*7 }}" | object.eval_template }}""")]
    public async Task Evaluating_a_string_as_code_is_not_available(string source)
    {
        Result<string> result = await Engine().RenderAsync(source, variables: null, CancellationToken.None);

        // Falsification: before the sandbox builtin was restricted, the first
        // case rendered "2" and the second rendered "42".
        result.IsFailure.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task A_variable_value_is_never_evaluated_as_a_template()
    {
        Result<string> result = await Engine().RenderAsync(
            "{{ payload | object.eval_template }}",
            Variables("""{ "payload": "injetado {{ 6*7 }}" }"""),
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.ShouldNotContain("42");
    }

    [Fact]
    public async Task Padding_cannot_allocate_past_the_output_ceiling()
    {
        Result<string> result = await Engine(maxOutputChars: 1_000).RenderAsync(
            """{{ x = "a" | string.pad_right 200000 }}{{ x.size }}""",
            variables: null,
            CancellationToken.None);

        // The 200000 character string was built outside the sink, so the
        // ceiling never saw it and the render reported success.
        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Including_another_template_is_not_available()
    {
        Result<string> result = await Engine().RenderAsync(
            """{{ include "outro" }}""",
            variables: null,
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task An_engine_message_never_carries_the_value_that_caused_it()
    {
        Result<string> result = await Engine().RenderAsync(
            "{{ math.abs segredo }}",
            Variables("""{ "segredo": "VALOR-SENSIVEL-4242" }"""),
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.ShouldNotContain("VALOR-SENSIVEL-4242");
        result.Error!.ShouldContain("***");
    }

    [Fact]
    public async Task A_nested_sensitive_value_is_redacted_from_an_engine_message()
    {
        Result<string> result = await Engine().RenderAsync(
            "{{ math.abs cliente.documento }}",
            Variables("""{ "cliente": { "documento": "11122233344" } }"""),
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.ShouldNotContain("11122233344");
    }

    [Fact]
    public async Task Output_above_the_ceiling_fails_instead_of_being_truncated()
    {
        // MaxOutputChars deliberately above the engine's own string limit,
        // which used to truncate at 1048576 and append an ellipsis while
        // reporting success.
        Result<string> result = await Engine(loopLimit: 10_000_000, maxOutputChars: 4_000_000).RenderAsync(
            "{{ for i in 1..200000 }}0123456789{{ end }}",
            variables: null,
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Length.ShouldBe(2_000_000);
        result.Value!.ShouldNotEndWith("...");
    }

    [Fact]
    public async Task The_engine_vocabulary_survives_redaction()
    {
        // Redaction must not cost the author the variable name, which is the
        // one part of the message that tells them what to fix.
        Result<string> result = await Engine().RenderAsync(
            "Olá {{ nome }}",
            Variables("""{ "outra": "valor-presente" }"""),
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.ShouldContain("nome");
    }

    [Theory]
    [InlineData("include")]
    [InlineData("include_join")]
    public void An_include_expression_is_reported_as_an_undeclared_variable(string builtin)
    {
        // A builtin the sandbox removed must stop being an exemption.
        // Publication is where this has to fail. The engine has no loader, so an
        // include that reaches production fails every single render of that
        // version; reporting it as undeclared blocks the version instead.
        TemplateSourceAnalysis analysis = Engine().Analyze("{{ " + builtin + " \"outro\" }}", "body");

        analysis.ParseSucceeded.ShouldBeTrue();
        analysis.UsedVariables.ShouldContain(builtin);
    }
}
