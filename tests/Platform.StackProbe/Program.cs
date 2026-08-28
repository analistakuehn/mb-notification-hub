using System.Text;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

// Runs the two ends of the postfix-chain question in a process of its own. The
// shape comes from the first argument: 'member' builds 'a.b.b.b...' and 'index'
// builds 'a[0][0][0]...'. The engine parses both in a loop instead of a
// recursion, so it never counts them against its own statement depth limit and
// returns a syntax tree as deep as the source affords.
//
// The second argument picks the end. 'refusal' takes the deepest chain the
// character ceiling still accepts and checks that the complexity ceiling turns
// it away before anything builds a tree from it. 'walk' takes the deepest chain
// the complexity ceiling admits and analyzes it, which is the deepest walk the
// module can be asked for. Either end would end the test host with no assertion
// if the containment broke, which is why they run here and a test reads the
// exit code.
if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: NotificationHub.StackProbe <member|index> <refusal|walk>");
    return ExitCodes.BadUsage;
}

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

var options = new TemplatingOptions();
using var parseCache = new ScribanParseCache();
var engine = new ScribanTemplateEngine(Options.Create(options), parseCache);

return args[1] switch
{
    "refusal" => Refusal(),
    "walk" => Walk(),
    _ => Unknown(),
};

int Unknown()
{
    Console.Error.WriteLine($"Unknown check '{args[1]}'. Use 'refusal' or 'walk'.");
    return ExitCodes.BadUsage;
}

// The chain that fills the character ceiling is the one that used to reach the
// walk and take the process down with it. It has to be refused, and refused by
// the ceiling on the tokens of a single block, which is the one that measures
// how deep a single expression goes.
int Refusal()
{
    var links = Depth(link, options.MaxTemplateSizeChars);
    var source = Chain(link, links);
    SourceComplexityLimit limit = ScribanSourceComplexity.Exceeded(
        source,
        options.MaxTemplateTokens,
        options.MaxCodeBlockTokens);

    Console.WriteLine(
        $"shape={args[0]} check=refusal links={links} chars={source.Length} limit={limit}");

    if (limit != SourceComplexityLimit.CodeBlockTokens)
    {
        Console.Error.WriteLine(
            $"The deepest source under the character ceiling was not refused by the block ceiling: {limit}.");
        return ExitCodes.DeepSourceNotRefused;
    }

    TemplateSourceAnalysis analysis = engine.Analyze(source, "body");
    if (analysis.ParseSucceeded)
    {
        Console.Error.WriteLine("The analysis parsed a source the complexity ceiling refuses.");
        return ExitCodes.DeepSourceAnalyzed;
    }

    Console.WriteLine($"  refused={analysis.ParseError}");
    return ExitCodes.Success;
}

// The deepest chain the ceilings still admit, which is the deepest tree the walk
// can be handed. The links are searched for rather than written down, so the
// check follows the ceiling wherever configuration moves it instead of pinning a
// number that goes stale the moment it moves.
int Walk()
{
    var links = DeepestAdmitted(link);
    if (links <= 0)
    {
        Console.Error.WriteLine("No chain at all is admitted, so the walk was never exercised.");
        return ExitCodes.BoundaryNotFound;
    }

    if (Admitted(Chain(link, links + 1)))
    {
        Console.Error.WriteLine($"A chain of {links + 1} links is admitted too, so this is not the boundary.");
        return ExitCodes.BoundaryNotFound;
    }

    var source = Chain(link, links);
    TemplateSourceAnalysis analysis = engine.Analyze(source, "body");
    Console.WriteLine(
        $"shape={args[0]} check=walk links={links} chars={source.Length} "
        + $"parsed={analysis.ParseSucceeded} variables=[{string.Join(",", analysis.UsedVariables)}]");

    if (!analysis.ParseSucceeded)
    {
        Console.Error.WriteLine($"The deepest admitted source did not analyze: {analysis.ParseError}");
        return ExitCodes.AdmittedSourceRefused;
    }

    // The whole chain hangs off one root variable, so anything else means the
    // walk reached the end but read the tree differently on the way.
    if (!analysis.UsedVariables.SequenceEqual(["a"]))
    {
        Console.Error.WriteLine("The analysis reported a variable set the chain does not have.");
        return ExitCodes.UnexpectedVariables;
    }

    return ExitCodes.Success;
}

// Largest link count the ceilings let through, by bisection over a range whose
// top is the character ceiling itself.
int DeepestAdmitted(string unit)
{
    var low = 0;
    var high = Depth(unit, options.MaxTemplateSizeChars);
    while (low < high)
    {
        var middle = low + ((high - low + 1) / 2);
        if (Admitted(Chain(unit, middle)))
        {
            low = middle;
        }
        else
        {
            high = middle - 1;
        }
    }

    return low;
}

bool Admitted(string source)
    => source.Length <= options.MaxTemplateSizeChars
        && ScribanSourceComplexity.Exceeded(source, options.MaxTemplateTokens, options.MaxCodeBlockTokens)
            == SourceComplexityLimit.None;

// Builds 'a' followed by the links asked for. The head and the tail together
// take five characters.
static string Chain(string unit, int links)
{
    var builder = new StringBuilder("{{a", 32 + (links * unit.Length));
    for (var index = 0; index < links; index++)
    {
        builder.Append(unit);
    }

    return builder.Append("}}").ToString();
}

// Links of that unit that fit in the characters given, head and tail included.
static int Depth(string unit, int maxChars) => (maxChars - 5) / unit.Length;

/// <summary>What the probe reports to the process that started it.</summary>
internal static class ExitCodes
{
    internal const int Success = 0;
    internal const int BadUsage = 64;
    internal const int DeepSourceNotRefused = 65;
    internal const int DeepSourceAnalyzed = 66;
    internal const int AdmittedSourceRefused = 67;
    internal const int UnexpectedVariables = 68;
    internal const int BoundaryNotFound = 69;
}
