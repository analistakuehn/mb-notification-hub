using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

internal static partial class DiffLayoutVersions
{
    internal sealed record ContentCoordinate(string Channel, string Locale);

    internal sealed record ChangedContent(string Channel, string Locale, IReadOnlyList<string> Fields);

    internal sealed record ContentsDiff(
        IReadOnlyList<ContentCoordinate> Added,
        IReadOnlyList<ContentCoordinate> Removed,
        IReadOnlyList<ChangedContent> Changed);

    internal sealed record Response(
        string LayoutKey,
        int Version,
        int AgainstVersion,
        ContentsDiff Contents)
    {
        internal static Response From(
            LayoutKey key,
            int version,
            int againstVersion,
            ContentSetDiff contents)
            => new(
                key.Value,
                version,
                againstVersion,
                new ContentsDiff(
                    contents.Added
                        .Select(unit => new ContentCoordinate(unit.Channel, unit.Locale))
                        .ToList(),
                    contents.Removed
                        .Select(unit => new ContentCoordinate(unit.Channel, unit.Locale))
                        .ToList(),
                    contents.Changed
                        .Select(change => new ChangedContent(change.Channel, change.Locale, change.ChangedFields))
                        .ToList()));
    }
}
