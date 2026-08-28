using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.ClassPolicies;

internal static partial class DiffClassPolicyVersions
{
    internal sealed record DefinitionDiff(
        IReadOnlyList<string> AddedFields,
        IReadOnlyList<string> RemovedFields,
        IReadOnlyList<string> ChangedFields);

    internal sealed record Response(
        string Application,
        string Class,
        int Version,
        int AgainstVersion,
        DefinitionDiff Definition)
    {
        internal static Response From(
            string application,
            NotificationClass notificationClass,
            int version,
            int againstVersion,
            SchemaFieldDiff definition)
            => new(
                application,
                notificationClass.Canonical(),
                version,
                againstVersion,
                new DefinitionDiff(
                    definition.AddedFields,
                    definition.RemovedFields,
                    definition.ChangedFields));
    }
}
