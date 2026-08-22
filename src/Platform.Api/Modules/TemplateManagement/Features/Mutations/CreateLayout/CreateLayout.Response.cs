using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class CreateLayout
{
    internal sealed record Response
    {
        public required string Key { get; init; }

        public required string OwnerTeam { get; init; }

        public required string Status { get; init; }

        public string? DefaultLocale { get; init; }

        internal static Response From(Layout layout) => new()
        {
            Key = layout.Key.Value,
            OwnerTeam = layout.OwnerTeam,
            Status = layout.Status.Canonical(),
            DefaultLocale = layout.DefaultLocale?.Value,
        };
    }
}
