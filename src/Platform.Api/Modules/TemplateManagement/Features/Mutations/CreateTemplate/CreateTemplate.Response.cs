using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class CreateTemplate
{
    internal sealed record Response
    {
        public required string Key { get; init; }

        public required string Application { get; init; }

        public required string Class { get; init; }

        public required string OwnerTeam { get; init; }

        public required string Purpose { get; init; }

        public required string LegalBasis { get; init; }

        public required string Status { get; init; }

        public string? DefaultLocale { get; init; }

        public required IReadOnlyList<string> LinkDomainsAllowed { get; init; }

        public required IReadOnlyList<string> SensitiveVariables { get; init; }

        internal static Response From(Template template) => new()
        {
            Key = template.Key.Value,
            Application = template.Application,
            Class = template.Class.Canonical(),
            OwnerTeam = template.OwnerTeam,
            Purpose = template.Purpose,
            LegalBasis = template.LegalBasis,
            Status = template.Status.Canonical(),
            DefaultLocale = template.DefaultLocale?.Value,
            LinkDomainsAllowed = template.LinkDomainsAllowed,
            SensitiveVariables = template.SensitiveVariables,
        };
    }
}
