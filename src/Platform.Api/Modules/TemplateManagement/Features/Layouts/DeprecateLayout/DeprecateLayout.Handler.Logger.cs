namespace NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

internal static partial class DeprecateLayoutLogger
{
    [LoggerMessage(EventId = 3070, Level = LogLevel.Information, Message = "Layout {LayoutKey} depreciado; novas referências devem apontar para outro layout.")]
    internal static partial void LayoutDeprecated(this ILogger logger, string layoutKey);
}
