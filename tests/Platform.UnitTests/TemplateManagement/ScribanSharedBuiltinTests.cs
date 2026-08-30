using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.SharedKernel;
using Scriban;
using Scriban.Runtime;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// State the sandbox may not carry from one render to the next. The builtin
/// surface is a single object for the whole process: it is built once and
/// pushed to the bottom of the global stack of every context, and the reset
/// that runs between two renders preserves it by design. A member written
/// there therefore outlives the render that wrote it, outlives its caller and
/// outlives the engine instance, so the next render of any other application
/// reads it. Every case here was reproduced against the pinned engine before
/// the surface was closed to writes.
/// </summary>
public sealed class ScribanSharedBuiltinTests
{
    /// <summary>
    /// Planted by one render and looked for in the next one. It is unique on
    /// purpose: a read that answers it read the plant and nothing else.
    /// </summary>
    private const string PlantedValue = "VALOR-DO-DESTINATARIO-9137";

    /// <summary>Member name no builtin group carries.</summary>
    private const string PlantedMember = "probe_9137";

    /// <summary>Date pattern that differs from the engine default in every field order.</summary>
    private const string MutatedDateFormat = "%Y/%m/%d";

    /// <summary>
    /// The groups the seal walks, measured against the pinned engine. The
    /// sentinel below is what keeps this list honest across an upgrade.
    /// </summary>
    private static readonly string[] BuiltinGroups =
        ["array", "date", "html", "math", "object", "regex", "string", "timespan"];

    /// <summary>
    /// Every builtin member that carries data instead of behavior, at both
    /// levels of the surface. Functions were already refused before the seal,
    /// because the engine marks them read-only itself; these five were not.
    /// </summary>
    private static readonly string[] BuiltinDataMembers =
        ["blank", "date.default_format", "date.format", "empty", "timespan.zero"];

    public static TheoryData<string> Groups => new(BuiltinGroups);

    private static ScribanTemplateEngine Engine()
        => new(Options.Create(new TemplatingOptions
        {
            LoopLimit = 1000,
            RecursionLimit = 16,
            MaxTemplateSizeChars = 10_000,
            RenderTimeoutMilliseconds = 5000,
            MaxOutputChars = 1_000_000,
        }), new ScribanParseCache());

    private static JsonElement Variables(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Theory]
    [MemberData(nameof(Groups))]
    public async Task A_member_planted_on_a_builtin_group_never_reaches_another_render(string group)
    {
        // Premise, so that a green below is the write being refused and not the
        // expression failing to be a write: the same shape against an object
        // this render owns is executed and readable in the same render.
        Result<string> premise = await Engine().RenderAsync(
            "{{ alvo." + PlantedMember + " = \"" + PlantedValue + "\" }}{{ alvo." + PlantedMember + " }}",
            Variables("""{ "alvo": {} }"""),
            CancellationToken.None);

        premise.IsSuccess.ShouldBeTrue();
        premise.Value.ShouldBe(PlantedValue);

        Result<string> plant = await Engine().RenderAsync(
            "{{ " + group + "." + PlantedMember + " = \"" + PlantedValue + "\" }}",
            variables: null,
            CancellationToken.None);

        // A second engine instance, with its own options and its own parse
        // memoization. What the two share is the process, which is exactly the
        // claim: before the seal this read answered the value planted above.
        Result<string> read = await Engine().RenderAsync(
            "{{ " + group + "." + PlantedMember + " }}",
            variables: null,
            CancellationToken.None);

        read.IsSuccess.ShouldBeTrue();
        read.Value!.ShouldNotContain(PlantedValue);

        // And the write is refused where it happens, rather than merely being
        // invisible to the reader that follows it.
        plant.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Overwriting_the_default_date_format_never_reaches_another_render()
    {
        const string ImplicitDate = "{{ date.parse '2026-08-30' }}";

        Result<string> before = await Engine().RenderAsync(
            ImplicitDate, variables: null, CancellationToken.None);

        Result<string> spelled = await Engine().RenderAsync(
            "{{ date.parse '2026-08-30' | date.to_string '" + MutatedDateFormat + "' }}",
            variables: null,
            CancellationToken.None);

        // Premise, so that the equality at the end is a format that did not
        // move and not two renders agreeing by accident: the two patterns
        // produce different text for the same instant.
        before.IsSuccess.ShouldBeTrue();
        before.Value.ShouldNotBeNullOrEmpty();
        spelled.IsSuccess.ShouldBeTrue();
        spelled.Value.ShouldNotBe(before.Value);

        // Premise, so that the refusal below is the seal and not a statement
        // the engine never executes: assigning that member on an object this
        // render owns is executed and readable.
        Result<string> premise = await Engine().RenderAsync(
            "{{ alvo.format = '" + MutatedDateFormat + "' }}{{ alvo.format }}",
            Variables("""{ "alvo": {} }"""),
            CancellationToken.None);

        premise.IsSuccess.ShouldBeTrue();
        premise.Value.ShouldBe(MutatedDateFormat);

        Result<string> overwrite = await Engine().RenderAsync(
            "{{ date.format = '" + MutatedDateFormat + "' }}",
            variables: null,
            CancellationToken.None);

        Result<string> after = await Engine().RenderAsync(
            ImplicitDate, variables: null, CancellationToken.None);

        after.IsSuccess.ShouldBeTrue();
        after.Value.ShouldBe(before.Value);

        overwrite.IsFailure.ShouldBeTrue();
    }

    /// <summary>
    /// Reopening sentinel, and the reason it is not a restatement of the code:
    /// the seal walks the builtin surface exactly one level below the root,
    /// which is the whole surface the pinned engine has. A release that nests a
    /// third level, or that adds a ninth group, leaves the members it brings
    /// writable and shared, and every other test in this file stays green
    /// because each one names a member the seal already reaches. This is the
    /// one that goes red.
    /// </summary>
    [Fact]
    public void The_builtin_surface_the_seal_walks_is_the_one_it_was_measured_against()
    {
        ScriptObject builtin = new TemplateContext().BuiltinObject;

        var groups = builtin.GetMembers()
            .Where(name => builtin[name] is ScriptObject)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var nested = groups
            .SelectMany(group => Members(builtin, group)
                .Where(member => Member(builtin, group, member) is ScriptObject)
                .Select(member => group + "." + member))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var data = builtin.GetMembers()
            .Where(name => CarriesData(builtin[name]))
            .Concat(groups.SelectMany(group => Members(builtin, group)
                .Where(member => CarriesData(Member(builtin, group, member)))
                .Select(member => group + "." + member)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        const string Reopen =
            "Reconfira a superfície de builtins do motor e estenda o selo de "
            + "BuildSandboxBuiltin para alcançar o que apareceu. O selo percorre um "
            + "nível abaixo da raiz, então grupo novo ou nível novo nasce gravável e "
            + "compartilhado por todo o processo, e um template publicado de uma "
            + "aplicação passa a gravar ali um valor que o template de outra lê.";

        string.Equals(string.Join(",", groups), string.Join(",", BuiltinGroups), StringComparison.Ordinal)
            .ShouldBeTrue($"os grupos de builtin agora são [{string.Join(",", groups)}]. {Reopen}");

        (nested.Length == 0)
            .ShouldBeTrue($"o builtin agora aninha um terceiro nível em [{string.Join(",", nested)}]. {Reopen}");

        string.Equals(string.Join(",", data), string.Join(",", BuiltinDataMembers), StringComparison.Ordinal)
            .ShouldBeTrue($"os membros de dados do builtin agora são [{string.Join(",", data)}]. {Reopen}");
    }

    private static IEnumerable<string> Members(ScriptObject builtin, string group)
        => ((ScriptObject)builtin[group]!).GetMembers();

    private static object? Member(ScriptObject builtin, string group, string member)
        => ((ScriptObject)builtin[group]!)[member];

    /// <summary>
    /// Whether a builtin member holds a value rather than behavior. A function
    /// was already refused before the seal, because the engine marks its own
    /// functions read-only; a value was not.
    /// </summary>
    private static bool CarriesData(object? member)
        => member is not ScriptObject and not IScriptCustomFunction;
}
