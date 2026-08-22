namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PutLayoutVersionContentLogger
{
    [LoggerMessage(EventId = 3020, Level = LogLevel.Information, Message = "Conteúdo do rascunho {Version} do layout {LayoutKey} atualizado para ({Channel}, {Locale}).")]
    internal static partial void LayoutContentUpdated(this ILogger logger, string layoutKey, int version, string channel, string locale);
}
