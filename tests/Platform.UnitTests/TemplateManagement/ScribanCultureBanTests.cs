using System.Globalization;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.SharedKernel;
using Scriban;
using Scriban.Runtime;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// The culture a rendered message is formatted under is the system's and never
/// the template author's. The engine's own default is the invariant culture, so
/// the only door out of it is the culture argument of a formatting member, and
/// these are the cases that hold that door shut.
/// </summary>
/// <remarks>
/// The ban refuses rather than ignores, and that is the whole design: a render
/// that dropped the argument and carried on would answer an author who asked
/// for one thing with another, in text that no later step compares against
/// anything.
/// </remarks>
public sealed class ScribanCultureBanTests
{
    /// <summary>One source per culture slot the ban covers, and the member each one names.</summary>
    public static TheoryData<string, string> CultureSlots => new()
    {
        { """{{ date.parse "2020-03-04" "%Y-%m-%d" "pt-BR" }}""", "date.parse" },
        { """{{ date.parse_to_string "2020-03-04" "%d %B %Y" "pt-BR" }}""", "date.parse_to_string" },
        {
            """{{ date.parse_to_string "2020-03-04" "%d %B %Y" null "%Y-%m-%d" "pt-BR" }}""",
            "date.parse_to_string"
        },
        { """{{ (date.parse "2020-03-04") | date.to_string "%d %B %Y" "pt-BR" }}""", "date.to_string" },
        { """{{ 1234567.5 | math.format "N1" "pt-BR" }}""", "math.format" },
        { """{{ 1234567.5 | object.format "N1" "pt-BR" }}""", "object.format" },
    };

    /// <summary>Every way the pinned engine lets an author reach one banned member.</summary>
    private static readonly string[] CallFormSources =
    [
        """{{ 1234567.5 | math.format "N1" "pt-BR" }}""",
        """{{ math.format 1234567.5 "N1" "pt-BR" }}""",
        """{{ math.format(1234567.5, "N1", "pt-BR") }}""",
        """{{ 1234567.5 | math.format format: "N1" culture: "pt-BR" }}""",
        """{{ math.format value: 1234567.5 format: "N1" culture: "pt-BR" }}""",
        """{{ cultura = "pt-BR"; 1234567.5 | math.format "N1" cultura }}""",
        """{{ 1234567.5 | math["format"] "N1" "pt-BR" }}""",
        """{{ grupo = math; 1234567.5 | grupo.format "N1" "pt-BR" }}""",
    ];

    /// <summary>The same members called the way the ban leaves open.</summary>
    private static readonly string[] CultureFreeSources =
    [
        """{{ 1234567.5 | math.format "N1" }}""",
        """{{ 1234567.5 | object.format "N1" }}""",
        """{{ date.parse "2020-03-04" "%Y-%m-%d" }}""",
        """{{ date.parse_to_string "2020-03-04" "%d %B %Y" }}""",
        """{{ (date.parse "2020-03-04") | date.to_string "%d %B %Y" }}""",
        """{{ 1234567.5 | math.format "N1" null }}""",
    ];

    /// <summary>The banned call, buried under each construct the engine offers.</summary>
    private static readonly string[] NestedSources =
    [
        """{{ for i in 1..3 }}{{ i | math.format "N1" "pt-BR" }}{{ end }}""",
        """{{ func f(x); ret x | math.format "N1" "pt-BR"; end; f 1 }}""",
        """{{ capture c }}{{ 1 | math.format "N1" "pt-BR" }}{{ end }}{{ c }}""",
        """{{ if (1 | math.format "N1" "pt-BR") == "x" }}a{{ end }}""",
    ];

    public static TheoryData<string> CallForms => new(CallFormSources);

    public static TheoryData<string> CultureFreeCalls => new(CultureFreeSources);

    public static TheoryData<string> NestedRefusals => new(NestedSources);

    private static ScribanTemplateEngine Engine()
        => new(Options.Create(new TemplatingOptions()), new ScribanParseCache());

    [Theory]
    [MemberData(nameof(CultureSlots))]
    public async Task Each_culture_slot_is_refused_on_its_own(string source, string member)
    {
        TemplateRenderOutcome outcome = await Engine().RenderOutcomeAsync(
            source, variables: null, CancellationToken.None);

        // The mode comes first: under a ban that stopped refusing, the render
        // would succeed and the assertion that fired would be about a message
        // that is not there, which reads as a wording change and not as an open
        // door.
        outcome.Refusal.ShouldBe(TemplateRefusal.CultureArgument);
        outcome.Result.IsFailure.ShouldBeTrue();
        outcome.Result.Error.ShouldNotBeNull();
        outcome.Result.Error.ShouldContain(member);
    }

    [Theory]
    [MemberData(nameof(CallForms))]
    public async Task Every_way_of_writing_the_call_is_refused(string source)
    {
        TemplateRenderOutcome outcome = await Engine().RenderOutcomeAsync(
            source, variables: null, CancellationToken.None);

        outcome.Refusal.ShouldBe(TemplateRefusal.CultureArgument);
        outcome.Result.IsFailure.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(NestedRefusals))]
    public async Task A_banned_call_nested_in_a_construct_keeps_its_own_mode(string source)
    {
        // The engine wraps what a builtin throws, and a construct in between is
        // where a second wrapping would appear. It would not fail the render,
        // it would relabel the refusal as the mode that names no limit, and the
        // operator reading it would be told nothing.
        TemplateRenderOutcome outcome = await Engine().RenderOutcomeAsync(
            source, variables: null, CancellationToken.None);

        outcome.Refusal.ShouldBe(TemplateRefusal.CultureArgument);
    }

    [Fact]
    public async Task An_empty_culture_is_still_a_culture_the_author_chose()
    {
        // It selects the invariant culture, which is what the system would have
        // used anyway, so the text would be identical and the ban would look
        // pointless here. It is refused all the same, because what is banned is
        // an author deciding the culture, not a particular culture winning: an
        // author who is allowed to write one culture argument has been told the
        // argument works.
        TemplateRenderOutcome outcome = await Engine().RenderOutcomeAsync(
            """{{ 1234567.5 | math.format "N1" "" }}""",
            variables: null,
            CancellationToken.None);

        outcome.Refusal.ShouldBe(TemplateRefusal.CultureArgument);
    }

    [Theory]
    [MemberData(nameof(CultureFreeCalls))]
    public async Task A_call_that_names_no_culture_renders_exactly_what_the_engine_alone_renders(string source)
    {
        // The oracle is the same expression on the untouched engine, not a
        // literal: a literal would pin this host's ICU into the assertion and
        // fail on the other one for a reason that has nothing to do with the
        // ban. What is being asserted is that the wrapper hands every argument
        // through in the order it received them.
        Result<string> rendered = await Engine().RenderAsync(
            source, variables: null, CancellationToken.None);

        var unbanned = Template.Parse(source).Render(new TemplateContext());

        rendered.IsSuccess.ShouldBeTrue(rendered.Error);
        rendered.Value.ShouldBe(unbanned);
        rendered.Value.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_culture_free_calls_do_not_all_render_the_same_text()
    {
        // Premise for the case above: it compares two renders of one source, so
        // it would pass just as well if every source rendered to one constant.
        List<string> rendered = [];
        foreach (var source in CultureFreeSources)
        {
            Result<string> outcome = await Engine().RenderAsync(
                source, variables: null, CancellationToken.None);
            rendered.Add(outcome.Value!);
        }

        rendered.Distinct(StringComparer.Ordinal).Count().ShouldBeGreaterThan(1);
    }

    /// <summary>
    /// Reopening sentinel. The ban names five members and six slots because
    /// that is what the pinned engine has; a release that adds a sixth member,
    /// or a third slot to one of these, leaves that door open and every case
    /// above stays green, because each one names a member the ban already
    /// covers. This is the one that goes red.
    /// </summary>
    /// <remarks>
    /// The two sides are not the same source read twice: the left one is walked
    /// out of the engine's own builtin object through the parameter information
    /// it publishes, and the right one is the table the ban is written from.
    /// </remarks>
    [Fact]
    public void The_builtin_members_that_accept_a_culture_are_the_ones_the_ban_covers()
    {
        ScriptObject builtin = new TemplateContext().BuiltinObject;

        var measured = Slots(builtin, prefix: null)
            .Concat(builtin.GetMembers()
                .Where(name => builtin[name] is ScriptObject)
                .SelectMany(name => Slots((ScriptObject)builtin[name]!, name)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var banned = CultureBearingBuiltins.Members
            .SelectMany(member => member.Slots.Select(slot => $"{member.Path}#{slot.Index}:{slot.Name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        const string Reopen =
            "Reconfira a superfície do motor e estenda CultureBearingBuiltins para alcançar "
            + "o que apareceu. O que a tabela não nomear continua aceitando cultura do autor, "
            + "e a saída volta a depender da ICU do host sem sinal nenhum.";

        string.Equals(string.Join(",", measured), string.Join(",", banned), StringComparison.Ordinal)
            .ShouldBeTrue(
                $"os argumentos de cultura do motor agora são [{string.Join(",", measured)}] "
                + $"e o banimento cobre [{string.Join(",", banned)}]. {Reopen}");
    }

    /// <summary>
    /// Every argument of every function of one object that can carry a culture,
    /// named as <c>group.member#index:name</c>.
    /// </summary>
    private static IEnumerable<string> Slots(ScriptObject source, string? prefix)
    {
        foreach (var name in source.GetMembers())
        {
            if (source[name] is not IScriptCustomFunction function)
            {
                continue;
            }

            for (var index = 0; index < function.ParameterCount; index++)
            {
                ScriptParameterInfo parameter = function.GetParameterInfo(index);
                var carries = parameter.ParameterType == typeof(CultureInfo)
                    || (parameter.Name?.Contains("culture", StringComparison.OrdinalIgnoreCase) ?? false);

                if (carries)
                {
                    yield return $"{(prefix is null ? name : prefix + "." + name)}#{index}:{parameter.Name}";
                }
            }
        }
    }
}
