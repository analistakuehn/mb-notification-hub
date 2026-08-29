namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Retention;

internal static partial class LifecycleNoteEraserLogger
{
    [LoggerMessage(EventId = 4030, Level = LogLevel.Information, Message = "Nota de ciclo de vida {NoteRef} apagada; a trilha registra o apagamento sob a mesma referência.")]
    internal static partial void LifecycleNoteErased(this ILogger logger, Guid noteRef);

    [LoggerMessage(EventId = 4031, Level = LogLevel.Information, Message = "Nenhuma nota de ciclo de vida sob a referência {NoteRef}; nada a apagar e nada a registrar.")]
    internal static partial void LifecycleNoteAlreadyAbsent(this ILogger logger, Guid noteRef);
}
