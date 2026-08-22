namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class CreateTemplate
{
    internal sealed partial class Handler
    {
        [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "Template {TemplateKey} created for application {Application} with class {Class}")]
        private partial void TemplateCreated(string templateKey, string application, string @class);
    }
}
