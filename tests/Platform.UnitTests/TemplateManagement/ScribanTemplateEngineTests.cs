using System.Diagnostics;
using System.Text;
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
        int timeoutMs = 2000,
        int maxOutputChars = 1_000_000,
        int maxTemplateTokens = 8192,
        int maxCodeBlockTokens = 512,
        ScribanParseCache? parseCache = null)
        => new(Options.Create(new TemplatingOptions
        {
            LoopLimit = loopLimit,
            RecursionLimit = recursionLimit,
            MaxTemplateSizeChars = maxSizeChars,
            RenderTimeoutMilliseconds = timeoutMs,
            MaxOutputChars = maxOutputChars,
            MaxTemplateTokens = maxTemplateTokens,
            MaxCodeBlockTokens = maxCodeBlockTokens,
        }), parseCache ?? new ScribanParseCache());

    private static string Repeat(string unit, int chars)
    {
        var builder = new StringBuilder(chars + unit.Length);
        while (builder.Length < chars)
        {
            builder.Append(unit);
        }

        return builder.ToString(0, chars);
    }

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

    // The two cases below used to match the engine's own message, and both
    // matched it by accident. Matching 'LoopLimit' held for a range loop and
    // went red on a collection, on a while and on an inner iteration, so it
    // covered a quarter of the limit it claimed. Matching 'recursive depth
    // limit' went green over a plain parse error, because what fires under a
    // deep enough source is the stack guard and not the depth counter. What the
    // module can actually tell apart is that the render was refused and that
    // the mode does not name a limit, which is one assertion for every loop
    // shape and both recursion shapes at once.

    [Fact]
    public async Task A_runaway_loop_is_refused_under_a_mode_that_names_no_limit()
    {
        TemplateRenderOutcome outcome = await Engine(loopLimit: 10).RenderOutcomeAsync(
            "{{ for i in 1..100000 }}x{{ end }}",
            variables: null,
            CancellationToken.None);

        outcome.Result.IsFailure.ShouldBeTrue();
        outcome.Refusal.ShouldBe(TemplateRefusal.Unclassified);
    }

    [Fact]
    public async Task Unbounded_recursion_is_refused_under_a_mode_that_names_no_limit()
    {
        TemplateRenderOutcome outcome = await Engine(recursionLimit: 8).RenderOutcomeAsync(
            "{{ func f(n)\nret f(n)\nend\nf 1 }}",
            variables: null,
            CancellationToken.None);

        outcome.Result.IsFailure.ShouldBeTrue();
        outcome.Refusal.ShouldBe(TemplateRefusal.Unclassified);
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
    public async Task A_catastrophic_regex_releases_the_render_around_the_configured_deadline()
    {
        var watch = Stopwatch.StartNew();
        Result<string> result = await Engine(timeoutMs: 500).RenderAsync(
            """{{ "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!" | regex.match `(a+)+$` }}""",
            variables: null,
            CancellationToken.None);
        watch.Stop();

        result.IsFailure.ShouldBeTrue();
        result.Error!.ShouldContain("time limit");

        // Well under the 10s default regex timeout: the engine regex deadline
        // is aligned with the configured render deadline.
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Nested_loops_multiplying_output_hit_the_output_ceiling()
    {
        Result<string> result = await Engine(maxOutputChars: 1_000).RenderAsync(
            "{{ for i in 1..100 }}{{ for j in 1..100 }}0123456789{{ end }}{{ end }}",
            variables: null,
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.ShouldContain("output");
        result.Error!.ShouldContain("1000 character limit");
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

    [Fact]
    public async Task A_source_past_the_token_ceiling_is_refused_instead_of_parsed()
    {
        Result<string> result = await Engine(maxSizeChars: 131_072).RenderAsync(
            Repeat("{{a.b.c.d.e.f.g.h.i.j}}", 131_072),
            variables: null,
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error!.ShouldContain("8192 token limit");
    }

    [Fact]
    public void The_analysis_refuses_the_same_source_the_render_refuses()
    {
        TemplateSourceAnalysis analysis = Engine(maxSizeChars: 131_072).Analyze(
            Repeat("{{a.b.c.d.e.f.g.h.i.j}}", 131_072),
            "body");

        // Publication and validation parse without the memoization and without a
        // deadline of any kind, so the ceiling has to sit on this path too.
        analysis.ParseSucceeded.ShouldBeFalse();
        analysis.ParseError!.ShouldContain("8192 token limit");
        analysis.UsedVariables.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_deep_member_chain_is_refused_by_the_expression_ceiling()
    {
        var builder = new StringBuilder("{{ a");
        for (var link = 0; link < 400; link++)
        {
            builder.Append(".b");
        }

        Result<string> result = await Engine().RenderAsync(
            builder.Append(" }}").ToString(),
            variables: null,
            CancellationToken.None);

        // Not a cost limit: measured against this engine, a chain of 1500 links
        // ends the process with a stack overflow while rendering, because every
        // link is one more stack frame and the engine's own depth limit does not
        // count a postfix chain. The refusal has to land before the parse builds
        // the tree, because a stack overflow cannot be caught afterwards.
        result.IsFailure.ShouldBeTrue();
        result.Error!.ShouldContain("512 token limit");
    }

    [Fact]
    public async Task Refusing_an_oversized_source_names_the_size_limit_without_scanning_it()
    {
        // A recusa por comprimento e a primeira barreira e nao examina a fonte:
        // ela decide por um inteiro. A prova e deterministica e nao usa
        // relogio, porque a mensagem so pode vir do ramo que decidiu. Se a
        // barreira sumisse, a fonte seguiria para a varredura e a recusa
        // passaria a nomear o limite de tokens.
        ScribanTemplateEngine engine = Engine(maxSizeChars: 131_072);
        var source = Repeat("{{a.b.c.d.e.f.g.h.i.j}}", 262_144);

        Result<string> result = await engine.RenderAsync(
            source, variables: null, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldNotBeNull().ShouldContain("262144");
        result.Error.ShouldNotBeNull().ShouldContain("131072");
    }

    [Fact]
    public async Task Refusing_a_dense_source_stops_at_the_token_ceiling_and_not_at_the_end()
    {
        // Dentro do teto de comprimento, quem limita a recusa e o teto de
        // tokens. A propriedade e que a varredura para nele, e nao no fim da
        // fonte. Densidade de tokens fixa e comprimento variavel discriminam
        // as duas: parando no teto o tempo e constante, varrendo ate o fim ele
        // acompanha o comprimento. A razao cancela a velocidade do hospedeiro,
        // que um limiar absoluto mediria no lugar do mecanismo.
        const string unit = "{{a.b.c.d.e.f.g.h.i.j}}";
        ScribanTemplateEngine engine = Engine(
            maxSizeChars: 8_000_000, maxTemplateTokens: 8192);
        var modest = Repeat(unit, 200_000);
        var enormous = Repeat(unit, 3_200_000);

        // Primeira chamada descartada: paga compilacao tardia e primeiro toque
        // de memoria, que nao pertencem ao mecanismo sob teste.
        await engine.RenderAsync(modest, variables: null, CancellationToken.None);

        var modestWatch = Stopwatch.StartNew();
        Result<string> refusedModest = await engine.RenderAsync(
            modest, variables: null, CancellationToken.None);
        modestWatch.Stop();

        var enormousWatch = Stopwatch.StartNew();
        Result<string> refusedEnormous = await engine.RenderAsync(
            enormous, variables: null, CancellationToken.None);
        enormousWatch.Stop();

        refusedModest.IsFailure.ShouldBeTrue();
        refusedEnormous.IsFailure.ShouldBeTrue();
        refusedModest.Error.ShouldNotBeNull().ShouldContain("8192");
        refusedEnormous.Error.ShouldNotBeNull().ShouldContain("8192");

        // A fonte cresce dezesseis vezes. A folga de quatro absorve ruido de
        // agendamento sem admitir uma varredura proporcional.
        var growth = enormousWatch.Elapsed.TotalMilliseconds
            / Math.Max(modestWatch.Elapsed.TotalMilliseconds, 0.01);
        growth.ShouldBeLessThan(4d);
    }

    [Fact]
    public async Task A_source_already_parsed_is_not_measured_again()
    {
        var cache = new ScribanParseCache();
        var source = "Olá {{ nome }}, o seu pedido chegou e já está a caminho do endereço.";
        JsonElement variables = Variables("""{ "nome": "Ana" }""");

        // The memoization holds published sources only, so this is the published
        // path: a draft is parsed and dropped on every call and never becomes
        // resident at all.
        ScribanTemplateEngine generous = Engine(parseCache: cache);
        Result<string> admitted = await generous.RenderAsync(
            generous.BeginForm(), source, variables, CancellationToken.None);

        ScribanTemplateEngine strict = Engine(maxTemplateTokens: 4, maxCodeBlockTokens: 4, parseCache: cache);
        Result<string> resident = await strict.RenderAsync(
            strict.BeginForm(), source, variables, CancellationToken.None);
        Result<string> arriving = await strict.RenderAsync(
            strict.BeginForm(),
            source.Replace("pedido", "envio", StringComparison.Ordinal),
            variables,
            CancellationToken.None);

        // The measurement is charged to the call that parses and never to the
        // one that looks up: charging every render for it would put the whole
        // cost of the guard on the path the memoization exists to keep cheap.
        // Tightening the ceilings therefore reaches the resident set on the next
        // start, which is when a singleton bound at startup reads them anyway.
        admitted.IsSuccess.ShouldBeTrue(admitted.Error);
        resident.IsSuccess.ShouldBeTrue(resident.Error);
        arriving.IsFailure.ShouldBeTrue();
        arriving.Error!.ShouldContain("4 token limit");
    }

    [Fact]
    public async Task A_long_message_of_plain_text_still_renders()
    {
        var source = Repeat("Olá, o seu pedido saiu para entrega hoje pela manhã. ", 131_000)
            + "{{ nome }}";

        Result<string> result = await Engine(maxSizeChars: 131_072).RenderAsync(
            source,
            Variables("""{ "nome": "Ana" }"""),
            CancellationToken.None);

        // The shape the ceiling must never touch: the longest thing an author
        // writes is text, and text is the cheapest thing the parser reads.
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldEndWith("Ana");
    }
}
