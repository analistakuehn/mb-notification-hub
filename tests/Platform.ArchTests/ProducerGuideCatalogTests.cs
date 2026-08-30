using NotificationHub.Api.Modules.Notifications.Integration.V1;

namespace NotificationHub.ArchTests;

/// <summary>
/// The rejection catalog is a published vocabulary, and a producer reads it in
/// the integration guide rather than in this source tree. A member that exists
/// in code and nowhere in the guide reaches a producer as a word it cannot look
/// up, which is the same failure as an uncatalogued reason: the diagnosis is
/// gone by the time anyone needs it.
/// </summary>
public sealed class ProducerGuideCatalogTests
{
    /// <summary>
    /// Header of the one table in the guide that documents the catalog. The
    /// table is located by its header instead of by a line range, so editing
    /// the prose around it cannot move the rule, and renaming the header fails
    /// this test loudly instead of quietly matching nothing.
    /// </summary>
    /// <summary>
    /// The published integration guide, the document a producer reads. It is
    /// named here rather than discovered, because the rule claims that this
    /// one document carries the catalog.
    /// </summary>
    private const string ProducerGuideFileName = "guia-integracao-produtor.md";

    private const string ReasonTableHeader = "| Motivo | O que significa | O que o produtor faz |";

    [Fact]
    public void Every_rejection_reason_has_a_row_in_the_producer_guide()
    {
        HashSet<string> documented = DocumentedReasons();

        // The table has to be found before its contents mean anything: an empty
        // set would make the assertion below fail for the wrong reason, and a
        // set built from a different table would pass for the wrong one.
        documented.Count.ShouldBeGreaterThan(1);

        var missing = NotificationRejectionReasons.All
            .Where(reason => !documented.Contains(reason))
            .Order(StringComparer.Ordinal)
            .ToArray();

        missing.ShouldBeEmpty();
    }

    /// <summary>
    /// The reasons the guide table names, taken from the first cell of every
    /// row under the header until the table ends. Only a cell written as a
    /// single code span counts, which is the shape every catalog row uses and
    /// no prose row does.
    /// </summary>
    private static HashSet<string> DocumentedReasons()
    {
        var lines = File.ReadAllLines(GuidePath());
        var header = Array.FindIndex(lines, line => line.Trim() == ReasonTableHeader);
        if (header < 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var reasons = new HashSet<string>(StringComparer.Ordinal);
        for (var index = header + 1; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (!line.StartsWith('|'))
            {
                break;
            }

            var cells = line.Split('|', StringSplitOptions.TrimEntries);
            if (cells.Length < 2)
            {
                continue;
            }

            var first = cells[1];
            if (first.Length > 2 && first.StartsWith('`') && first.EndsWith('`'))
            {
                reasons.Add(first[1..^1]);
            }
        }

        return reasons;
    }

    private static string GuidePath()
        => Path.Combine(FindSolutionRoot(), "docs", ProducerGuideFileName);

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
