namespace NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

internal static partial class CreateLayoutLogger
{
    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "Layout {LayoutKey} criado para o time {OwnerTeam}.")]
    internal static partial void LayoutCreated(this ILogger logger, string layoutKey, string ownerTeam);
}
