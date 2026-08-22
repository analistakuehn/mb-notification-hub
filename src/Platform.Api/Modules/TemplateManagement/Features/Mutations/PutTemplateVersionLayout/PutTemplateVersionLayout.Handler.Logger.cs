namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PutTemplateVersionLayoutLogger
{
    [LoggerMessage(EventId = 2100, Level = LogLevel.Information, Message = "Rascunho {Version} do template {TemplateKey} fixou o layout {LayoutKey} na versão {LayoutVersion}.")]
    internal static partial void LayoutReferencePinned(this ILogger logger, string templateKey, int version, string layoutKey, int layoutVersion);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "Rascunho {Version} do template {TemplateKey} removeu a referência de layout.")]
    internal static partial void LayoutReferenceCleared(this ILogger logger, string templateKey, int version);
}
