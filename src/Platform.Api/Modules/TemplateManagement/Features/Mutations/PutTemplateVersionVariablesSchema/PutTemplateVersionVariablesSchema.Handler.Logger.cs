namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PutTemplateVersionVariablesSchema
{
    internal sealed partial class Handler
    {
        [LoggerMessage(EventId = 2030, Level = LogLevel.Information, Message = "Template {TemplateKey} draft version {Version} variables schema replaced")]
        private partial void VariablesSchemaReplaced(string templateKey, int version);
    }
}
