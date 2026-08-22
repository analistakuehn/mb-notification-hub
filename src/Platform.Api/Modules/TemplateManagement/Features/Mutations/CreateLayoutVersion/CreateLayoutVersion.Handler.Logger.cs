namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class CreateLayoutVersionLogger
{
    [LoggerMessage(EventId = 3010, Level = LogLevel.Information, Message = "Rascunho {Version} do layout {LayoutKey} aberto.")]
    internal static partial void LayoutDraftOpened(this ILogger logger, string layoutKey, int version);

    [LoggerMessage(EventId = 3011, Level = LogLevel.Information, Message = "Rascunho {Version} do layout {LayoutKey} aberto como clone da versão {FromVersion}.")]
    internal static partial void LayoutDraftCloned(this ILogger logger, string layoutKey, int version, int fromVersion);
}
