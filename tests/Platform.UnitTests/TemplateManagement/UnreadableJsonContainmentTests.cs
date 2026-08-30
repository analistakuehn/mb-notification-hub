using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// Born green. These do not describe the refusal; they fence the shape of it,
/// so the properties it was built on cannot drift away without a red.
/// </summary>
public sealed class UnreadableJsonContainmentTests
{
    private const string LoneSurrogateEscape = @"\ud800";
    private const string PairedSurrogateEscape = @"\ud83d\ude00";
    private const string EscapedATilde = @"\u00e3";

    /// <summary>
    /// Documents the two mechanisms have to agree about. Both spellings are
    /// present on purpose: a corpus of readable documents alone would let the
    /// two agree by never disagreeing about anything.
    /// </summary>
    private static readonly string[] Corpus =
    [
        """{"a":1}""",
        """{"a":1e2,"b":[1,2,{"c":null}]}""",
        $$"""{"v":"{{PairedSurrogateEscape}}"}""",
        $$"""{"cidade":"S{{EscapedATilde}}o Paulo"}""",
        """{"v":"😀"}""",
        "\"texto\"",
        "[1,2,3]",
        $$"""{"v":"{{LoneSurrogateEscape}}"}""",
        $$"""{"{{LoneSurrogateEscape}}":"ok"}""",
        $$"""{"items":["ok","{{LoneSurrogateEscape}}"]}""",
        $$$"""{"order":{"note":"{{{LoneSurrogateEscape}}}"}}""",
    ];

    [Fact]
    public void The_canonical_form_and_the_measure_answer_readability_alike()
    {
        // The rule now has two mentions: one produces the canonical form the
        // hash covers, the other guards the doors and the walks. They read the
        // same runtime rule and must never come apart, because a document one
        // admits and the other refuses is a door that accepts what the next
        // step cannot hash.
        List<string> disagreements = [];
        foreach (var json in Corpus)
        {
            using var document = JsonDocument.Parse(json);
            var measured = CompactJsonSize.Measure(document.RootElement).IsReadable;
            var canonical = CanonicalJson.TryNormalize(json).Text is not null;
            if (measured != canonical)
            {
                disagreements.Add(json);
            }
        }

        disagreements.ShouldBeEmpty();
    }

    [Fact]
    public void The_corpus_actually_contains_documents_of_both_kinds()
    {
        // Without this the agreement above is satisfied by a corpus nothing
        // refuses, which would pass over an implementation that never refuses.
        var readable = Corpus.Count(json => CanonicalJson.TryNormalize(json).Text is not null);

        readable.ShouldBeGreaterThan(0);
        readable.ShouldBeLessThan(Corpus.Length);
    }

    [Fact]
    public void A_defect_inside_the_traversal_is_never_reported_as_an_unreadable_document()
    {
        // A string already carrying invalid UTF-16 in memory is not a document
        // with a property: no stored column and no request body can produce
        // one, only a caller inside this process that built it. The traversal
        // raises a different exception type for it, and the catch has to let
        // that one through: a catch wide enough to swallow it would report a
        // broken caller as an unreadable document, which is how a measure stops
        // being able to fail.
        var brokenInMemory = "{\"a\":\"" + (char)0xD800 + "\"}";

        ArgumentException thrown = Should.Throw<ArgumentException>(
            () => CanonicalJson.TryNormalize(brokenInMemory));

        thrown.ShouldNotBeOfType<JsonException>();
    }

    [Fact]
    public void Rehydration_stays_out_of_the_running_system()
    {
        // It is the one entry point that skips every guard, and it answers a
        // document it cannot read by throwing. That is right only while nothing
        // in production calls it: the moment something does, a stored row
        // reaches the one door with no refusal to return.
        var callers = SourceFiles(ProductionRoot)
            .Where(path => File.ReadAllText(path).Contains(".Rehydrate(", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .ToList();

        callers.ShouldBeEmpty();
    }

    /// <summary>
    /// Where this module is allowed to parse a JSON document. Each of these
    /// either establishes readability before it walks anything, or is reached
    /// only behind a door that already did.
    /// </summary>
    private static readonly string[] ParsingFiles =
    [
        "CanonicalJson.cs",
        "ClassPolicyValidation.cs",
        "ClassPolicyVersion.cs",
        "JsonProjections.cs",
        "VariablesSchema.cs",
        "VersionDiff.cs",
    ];

    [Fact]
    public void The_module_parses_json_only_where_readability_is_already_settled()
    {
        // A new parse site is a new walk over names and string values, and the
        // guard is a property of the walk and not of the type it produces. This
        // is a tripwire, not a proof: it fails on a site nobody weighed, which
        // is when the question is cheapest to answer.
        var parsing = SourceFiles(ModuleRoot)
            .Where(path => File.ReadAllText(path).Contains("JsonDocument.Parse", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        parsing.ShouldBe(ParsingFiles);
    }

    private const string ProductionRoot = "src";

    private const string ModuleRoot = "src/Platform.Api/Modules/TemplateManagement";

    private static IEnumerable<string> SourceFiles(string relativeRoot)
        => Directory
            .EnumerateFiles(
                Path.Combine(FindSolutionRoot(), relativeRoot.Replace('/', Path.DirectorySeparatorChar)),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the solution root.");
    }
}
