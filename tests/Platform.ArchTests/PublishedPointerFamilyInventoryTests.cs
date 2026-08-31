using System.Reflection;
using System.Text;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;

namespace NotificationHub.ArchTests;

/// <summary>
/// The families of memoized key the published reads build, held against the
/// one document an agent reads before touching that boundary. A family is the
/// prefix every key of one kind of memoized answer carries, and adding one is
/// a decision about staleness: whether the answer may be a pointer at all,
/// and which lifecycle transition owes its invalidation. A family that
/// arrives without a line in the guide is that decision taken by nobody.
/// </summary>
/// <remarks>
/// <para>
/// The built side is taken from the builder itself, by invoking each member
/// and reading the prefix off the key it returns. The member name is not the
/// prefix and cannot stand in for it: the policy family is built by a member
/// called <c>ClassPolicy</c> and emits <c>policy:</c>, so a rule that read
/// names would compare one vocabulary against another and agree with itself
/// on nothing.
/// </para>
/// <para>
/// A builder that grows an argument this rule cannot invent, or one that
/// starts refusing the sentinel it is handed, takes the rule down with a
/// verdict of its own. That is the same principle as the guard against an
/// empty side: a rule that cannot read its input has to say so, because the
/// alternative is a green report that measured nothing.
/// </para>
/// </remarks>
public sealed class PublishedPointerFamilyInventoryTests
{
    private static readonly string[] GuideSegments =
        ["src", "Platform.Api", "Modules", "TemplateManagement", "AGENTS.md"];

    /// <summary>Heading of the section that carries the enumeration.</summary>
    private const string SectionHeader = "## Published read contracts";

    /// <summary>
    /// Opening clause of the one bullet inside that section that enumerates
    /// the families. The bullet is located by this clause instead of by a line
    /// range or by a position in the list, so editing the prose around it
    /// cannot move the rule, and rewriting the clause fails these tests loudly
    /// instead of quietly matching nothing.
    /// </summary>
    private const string EnumerationAnchor = "families of the published reads are:";

    private static readonly Type Builder = typeof(PublishedPointerKeys);

    [Fact]
    public void Every_key_family_the_builder_emits_is_named_in_the_guide()
    {
        HashSet<string> built = BuiltFamilies();
        HashSet<string> documented = DocumentedFamilies();

        AssertBothSidesWereFound(built, documented);

        var undocumented = built.Except(documented).Order(StringComparer.Ordinal).ToArray();
        undocumented.ShouldBeEmpty(
            "The published reads memoize under these key families and the guide does not name "
            + "them, so nobody decided how stale their answers may be or which transition drops "
            + "them: " + string.Join(", ", undocumented));
    }

    [Fact]
    public void Every_key_family_the_guide_names_is_still_emitted_by_the_builder()
    {
        HashSet<string> built = BuiltFamilies();
        HashSet<string> documented = DocumentedFamilies();

        AssertBothSidesWereFound(built, documented);

        var unbuilt = documented.Except(built).Order(StringComparer.Ordinal).ToArray();
        unbuilt.ShouldBeEmpty(
            "The guide names these key families and nothing builds them any more, so it describes "
            + "memoization that is not there: " + string.Join(", ", unbuilt));
    }

    /// <summary>
    /// Both sides have to be found before comparing them means anything. An
    /// empty set on either side makes one of the comparisons above pass for
    /// the wrong reason, and the cheapest way to reach one is a rename: of the
    /// section heading, of the anchoring clause, or of the builder itself.
    /// </summary>
    private static void AssertBothSidesWereFound(
        HashSet<string> built,
        HashSet<string> documented)
    {
        built.Count.ShouldBeGreaterThan(
            1,
            $"No key family was built off '{Builder.Name}'. The reflection is looking at the wrong "
            + "type or the builders moved, and until that is fixed this rule proves nothing.");

        documented.Count.ShouldBeGreaterThan(
            1,
            $"The enumeration was not found in the guide. Either the '{SectionHeader}' heading or "
            + $"the clause '{EnumerationAnchor}' was renamed, and this rule cannot read a section "
            + "it cannot locate.");
    }

    /// <summary>
    /// The families the builder emits, read off the keys it produces for
    /// sentinel arguments and cut at the first separator, because everything
    /// after it is identity and not family.
    /// </summary>
    private static HashSet<string> BuiltFamilies()
    {
        var families = new HashSet<string>(StringComparer.Ordinal);
        foreach (MethodInfo builder in Builder.GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            var key = Build(builder);
            var separator = key.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                throw new InvalidOperationException(
                    $"The key '{builder.Name}' builds carries no family separator, so this rule "
                    + "cannot tell where its family ends and its identity begins.");
            }

            families.Add(key[..(separator + 1)]);
        }

        return families;
    }

    /// <summary>
    /// One key, built with arguments that carry no separator of their own. A
    /// builder that refuses them, or that takes an argument this rule cannot
    /// invent, ends the run with a verdict that says the rule could not read,
    /// and never with one that says it passed.
    /// </summary>
    private static string Build(MethodInfo builder)
    {
        var arguments = new object[builder.GetParameters().Length];
        ParameterInfo[] parameters = builder.GetParameters();
        for (var index = 0; index < parameters.Length; index++)
        {
            Type parameter = parameters[index].ParameterType;
            arguments[index] = parameter == typeof(string) ? "sentinel"
                : parameter == typeof(int) ? 1
                : throw new InvalidOperationException(
                    $"The builder '{builder.Name}' takes a '{parameter.Name}', and this rule has "
                    + "no sentinel for it, so it cannot build the key it is supposed to read.");
        }

        try
        {
            return (string)builder.Invoke(null, arguments)!;
        }
        catch (TargetInvocationException exception)
        {
            throw new InvalidOperationException(
                $"The builder '{builder.Name}' refused the sentinel arguments this rule hands it, "
                + "so the family it emits could not be read and this run proves nothing about it.",
                exception);
        }
    }

    /// <summary>
    /// The families the guide enumerates, taken from the code spans of the
    /// anchored bullet. A span counts when it is shaped like a family prefix,
    /// which is what every entry of the enumeration is and what no other span
    /// in that bullet is.
    /// </summary>
    private static HashSet<string> DocumentedFamilies()
    {
        var families = new HashSet<string>(StringComparer.Ordinal);
        if (EnumerationBullet() is not string bullet)
        {
            return families;
        }

        var spans = bullet.Split('`');
        for (var index = 1; index < spans.Length; index += 2)
        {
            var span = spans[index];
            if (LooksLikeFamilyPrefix(span))
            {
                families.Add(span);
            }
        }

        return families;
    }

    /// <summary>
    /// The enumerating bullet of the section, joined into one line because the
    /// enumeration wraps. Bullets are read from the heading to the next one at
    /// the same level, so prose added to the section later cannot be mistaken
    /// for the enumeration.
    /// </summary>
    private static string? EnumerationBullet()
    {
        var lines = File.ReadAllLines(GuidePath());
        var header = Array.FindIndex(lines, line => line.Trim() == SectionHeader);
        if (header < 0)
        {
            return null;
        }

        var bullet = new StringBuilder();
        for (var index = header + 1; index < lines.Length; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                break;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                if (Anchored(bullet))
                {
                    return bullet.ToString();
                }

                bullet.Clear();
            }

            bullet.Append(trimmed).Append(' ');
        }

        return Anchored(bullet) ? bullet.ToString() : null;
    }

    private static bool Anchored(StringBuilder bullet)
        => bullet.ToString().Contains(EnumerationAnchor, StringComparison.Ordinal);

    private static bool LooksLikeFamilyPrefix(string span)
        => span.Length > 1
            && span[^1] == ':'
            && span[..^1].All(character => char.IsAsciiLetterLower(character) || character == '-');

    private static string GuidePath()
        => Path.Combine([FindSolutionRoot(), .. GuideSegments]);

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
