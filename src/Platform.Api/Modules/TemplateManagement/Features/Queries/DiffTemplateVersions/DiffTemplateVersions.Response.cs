using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class DiffTemplateVersions
{
    internal sealed record ContentCoordinate(string Channel, string Locale);

    internal sealed record ChangedContent(string Channel, string Locale, IReadOnlyList<string> Fields);

    internal sealed record ContentsDiff(
        IReadOnlyList<ContentCoordinate> Added,
        IReadOnlyList<ContentCoordinate> Removed,
        IReadOnlyList<ChangedContent> Changed);

    internal sealed record VariablesSchemaDiff(
        IReadOnlyList<string> AddedFields,
        IReadOnlyList<string> RemovedFields,
        IReadOnlyList<string> ChangedFields);

    internal sealed record Response(
        string TemplateKey,
        int Version,
        int AgainstVersion,
        ContentsDiff Contents,
        VariablesSchemaDiff VariablesSchema)
    {
        internal static Response From(
            TemplateKey key,
            int version,
            int againstVersion,
            ContentSetDiff contents,
            SchemaFieldDiff schema)
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
                        .ToList()),
                new VariablesSchemaDiff(schema.AddedFields, schema.RemovedFields, schema.ChangedFields));
    }
}
