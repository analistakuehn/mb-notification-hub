namespace NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

internal static partial class DisableLayoutLogger
{
    [LoggerMessage(EventId = 3080, Level = LogLevel.Information, Message = "Layout {LayoutKey} desabilitado; nenhuma publicação passa a ser aceita.")]
    internal static partial void LayoutDisabled(this ILogger logger, string layoutKey);
}
