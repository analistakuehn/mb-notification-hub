using System.Text;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

// Analyzes the deepest source the templating ceiling accepts, in a process of
// its own. The shape comes from the argument: 'member' builds 'a.b.b.b...' and
// 'index' builds 'a[0][0][0]...'. The engine parses both in a loop instead of
// a recursion, so it accepts a chain as deep as the character ceiling allows
// and returns a syntax tree just as deep. Whatever walks that tree has to walk
// it without recursing, and this process is how a test finds that out and
// lives to report it.
if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: NotificationHub.StackProbe <member|index>");
    return ExitCodes.BadUsage;
}

var options = new TemplatingOptions();
var link = args[0] switch
{
    "member" => ".b",
    "index" => "[0]",
    _ => null,
};

if (link is null)
{
    Console.Error.WriteLine($"Unknown shape '{args[0]}'. Use 'member' or 'index'.");
    return ExitCodes.BadUsage;
}

var source = Chain(link, options.MaxTemplateSizeChars);
var depth = (source.Length - 5) / link.Length;
using var parseCache = new ScribanParseCache();
var engine = new ScribanTemplateEngine(Options.Create(options), parseCache);

TemplateSourceAnalysis analysis = engine.Analyze(source, "body");
Console.WriteLine(
    $"shape={args[0]} depth={depth} chars={source.Length} parsed={analysis.ParseSucceeded} "
    + $"variables=[{string.Join(",", analysis.UsedVariables)}]");

if (!analysis.ParseSucceeded)
{
    Console.Error.WriteLine($"The source did not parse: {analysis.ParseError}");
    return ExitCodes.SourceRejected;
}

// The whole chain hangs off one root variable, so anything else means the walk
// reached the end but read the tree differently on the way.
if (!analysis.UsedVariables.SequenceEqual(["a"]))
{
    Console.Error.WriteLine("The analysis reported a variable set the chain does not have.");
    return ExitCodes.UnexpectedVariables;
}

return ExitCodes.Success;

// Builds 'a' followed by as many links as fit under the ceiling. The head and
// the tail together take five characters of the budget.
static string Chain(string link, int maxChars)
{
    const string head = "{{a";
    const string tail = "}}";
    var depth = (maxChars - head.Length - tail.Length) / link.Length;
    var builder = new StringBuilder(head, maxChars);
    for (var index = 0; index < depth; index++)
    {
        builder.Append(link);
    }

    return builder.Append(tail).ToString();
}

/// <summary>What the probe reports to the process that started it.</summary>
internal static class ExitCodes
{
    internal const int Success = 0;
    internal const int BadUsage = 64;
    internal const int SourceRejected = 65;
    internal const int UnexpectedVariables = 66;
}
