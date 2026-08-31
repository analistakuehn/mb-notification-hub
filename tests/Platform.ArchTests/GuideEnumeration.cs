using System.Text;

namespace NotificationHub.ArchTests;

/// <summary>
/// The bullet of a guide section that carries an enumeration a rule holds
/// code against. Two rules read one this way, and they had the same walk
/// written out twice before it moved here.
/// </summary>
/// <remarks>
/// Only the walk is shared. Each rule keeps its own heading, its own anchoring
/// clause, its own idea of what a span in that bullet has to look like, and
/// its own guard against an empty side, so the semantics of a rule stay at the
/// rule. A walk that stopped finding anything would empty both rules at once,
/// and both would say so, because that guard never moved.
/// </remarks>
internal static class GuideEnumeration
{
    /// <summary>
    /// The enumerating bullet of a section, joined into one line because the
    /// enumeration wraps. Bullets are read from the heading to the next one at
    /// the same level, so prose added to the section later cannot be mistaken
    /// for the enumeration, and the bullet is located by the anchoring clause
    /// instead of by a line range or by a position in the list.
    /// </summary>
    internal static string? Bullet(string path, string sectionHeader, string anchor)
    {
        var lines = File.ReadAllLines(path);
        var header = Array.FindIndex(lines, line => line.Trim() == sectionHeader);
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
                if (Anchored(bullet, anchor))
                {
                    return bullet.ToString();
                }

                bullet.Clear();
            }

            bullet.Append(trimmed).Append(' ');
        }

        return Anchored(bullet, anchor) ? bullet.ToString() : null;
    }

    private static bool Anchored(StringBuilder bullet, string anchor)
        => bullet.ToString().Contains(anchor, StringComparison.Ordinal);
}
