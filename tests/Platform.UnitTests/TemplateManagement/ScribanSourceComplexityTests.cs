using System.Text;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// The admission check that stands in for the deadline the parse cannot have.
/// It is driven by what was measured on this engine: cost follows the token
/// count, and postfix depth is what overflows the stack after the parse.
/// </summary>
public sealed class ScribanSourceComplexityTests
{
    /// <summary>The ceilings the module ships, so the cases read as deployed.</summary>
    private static readonly TemplatingOptions Shipped = new();

    private static SourceComplexityLimit Exceeded(string source, int? tokens = null, int? blockTokens = null)
        => ScribanSourceComplexity.Exceeded(
            source,
            tokens ?? Shipped.MaxTemplateTokens,
            blockTokens ?? Shipped.MaxCodeBlockTokens);

    private static string Repeat(string unit, int chars)
    {
        var builder = new StringBuilder(chars + unit.Length);
        while (builder.Length < chars)
        {
            builder.Append(unit);
        }

        return builder.ToString(0, chars);
    }

    private static string MemberChain(int links, string prefix = "")
    {
        var builder = new StringBuilder(prefix.Length + (links * 2) + 8);
        builder.Append("{{ ").Append(prefix).Append('a');
        for (var link = 0; link < links; link++)
        {
            builder.Append(".b");
        }

        return builder.Append(" }}").ToString();
    }

    [Fact]
    public void Prose_at_the_size_limit_is_admitted()
    {
        var source = Repeat("Olá, o seu pedido saiu para entrega hoje pela manhã. ", Shipped.MaxTemplateSizeChars);

        // The cheapest shape there is, and the one an author actually writes:
        // literal text lexes into a token per run, so the size ceiling is the
        // only one it can reach.
        Exceeded(source).ShouldBe(SourceComplexityLimit.None);
        source.Length.ShouldBe(Shipped.MaxTemplateSizeChars);
    }

    [Fact]
    public void A_rich_message_at_the_size_limit_is_admitted()
    {
        var filler = Repeat("<td style=\"padding:12px\">bloco promocional da campanha</td>", 600);
        var builder = new StringBuilder(Shipped.MaxTemplateSizeChars + 256);
        for (var row = 0; builder.Length < Shipped.MaxTemplateSizeChars; row++)
        {
            builder.Append("<tr>").Append(filler).Append("<td>{{ user.name }} {{ order.id }}</td></tr>");
            if (row % 10 == 0)
            {
                builder.Append("<ul>{{ for item in order.items }}<li>{{ item.label }}</li>{{ end }}</ul>");
            }
        }

        // The far end of what an author writes on purpose: 128 KB of marketing
        // HTML with a couple hundred interpolations and loops. It measures 2781
        // tokens against a ceiling of 8192, and the ceiling exists to leave this
        // untouched while refusing what costs two orders of magnitude more.
        Exceeded(builder.ToString(0, Shipped.MaxTemplateSizeChars)).ShouldBe(SourceComplexityLimit.None);
    }

    [Fact]
    public void The_sources_a_notification_renders_are_admitted()
    {
        string[] sources =
        [
            "Pedido {{ order.id }} atualizado",
            "<p>Olá {{ user.name }}, o pedido {{ order.id }} tem {{ order.items.size }} itens.</p>"
            + "<ul>{{ for item in order.items }}<li>{{ item.label }}: {{ item.qty }}</li>{{ end }}</ul>",
            "<html><header>MB</header>{{ content }}<footer>rodapé</footer></html>",
        ];

        foreach (var source in sources)
        {
            Exceeded(source).ShouldBe(SourceComplexityLimit.None, source);
        }
    }

    [Fact]
    public void A_source_dense_in_member_access_crosses_the_whole_source_ceiling()
    {
        // Measured at this size, this shape costs about 80 ms of parse against
        // 0.6 ms for the prose above, and no deadline covers either.
        var source = Repeat("{{a.b.c.d.e.f.g.h.i.j}}", Shipped.MaxTemplateSizeChars);

        Exceeded(source).ShouldBe(SourceComplexityLimit.Tokens);
    }

    [Fact]
    public void Many_small_blocks_add_up_to_the_whole_source_ceiling()
    {
        // No single block is remarkable here; the source is. The whole-source
        // ceiling is what sees it, because parse cost is paid per token wherever
        // the tokens sit.
        var source = Repeat("{{a}}", Shipped.MaxTemplateSizeChars);

        Exceeded(source).ShouldBe(SourceComplexityLimit.Tokens);
    }

    [Fact]
    public void A_member_chain_is_refused_far_below_the_depth_that_kills_the_process()
    {
        // The parse survives any depth: the engine reads a postfix chain in a
        // loop, so its own expression-depth limit never counts it. What does not
        // survive is everything that walks the tree afterwards, one stack frame
        // per link. Measured against this engine, 1500 links, a source of 3007
        // characters, ends the process with a stack overflow during the render,
        // and 16000 links do it during the analysis. A stack overflow cannot be
        // caught, so the refusal has to happen before the tree exists.
        Exceeded(MemberChain(400)).ShouldBe(SourceComplexityLimit.CodeBlockTokens);
        MemberChain(400).Length.ShouldBeLessThan(1000);
    }

    [Fact]
    public void An_index_chain_is_refused_by_the_same_ceiling()
    {
        var builder = new StringBuilder("{{ a");
        for (var link = 0; link < 400; link++)
        {
            builder.Append("[0]");
        }

        Exceeded(builder.Append(" }}").ToString()).ShouldBe(SourceComplexityLimit.CodeBlockTokens);
    }

    [Fact]
    public void A_closing_brace_inside_a_string_does_not_end_the_block()
    {
        // The reason the scan runs the engine's own lexer instead of counting
        // what sits between the braces: a character scanner reads the `}}` in
        // the string as the end of the block and measures two code characters,
        // then takes the 800-token chain behind it for literal text. The engine
        // parses this source without a single error.
        Exceeded(MemberChain(400, "\"}}\" + ")).ShouldBe(SourceComplexityLimit.CodeBlockTokens);
    }

    [Fact]
    public void The_block_ceiling_belongs_to_one_block_and_not_to_the_source()
    {
        // A hundred and sixty blocks of five tokens each, with text between
        // them. What the ceiling bounds is the depth of one expression, so it
        // starts over at every block and ignores the text: a counter that ran
        // across the source would refuse the longest legitimate message first,
        // and refuse it for a depth no expression in it has.
        var source = Repeat("texto corrido {{ user.address.city }} e segue o texto. ", 9000);

        Exceeded(source, tokens: 100_000, blockTokens: 8).ShouldBe(SourceComplexityLimit.None);
        Exceeded(source, tokens: 100_000, blockTokens: 4).ShouldBe(SourceComplexityLimit.CodeBlockTokens);
    }

    [Fact]
    public void The_ceilings_are_the_ones_the_caller_passes()
    {
        var source = Repeat("Olá {{ name }}, o seu pedido chegou. ", 4000);

        Exceeded(source).ShouldBe(SourceComplexityLimit.None);
        Exceeded(source, tokens: 64).ShouldBe(SourceComplexityLimit.Tokens);
        Exceeded(MemberChain(400), blockTokens: 4096).ShouldBe(SourceComplexityLimit.None);
    }
}
