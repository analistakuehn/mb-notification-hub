namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PutTemplateVersionContent
{
    internal sealed partial class Handler
    {
        [LoggerMessage(EventId = 2020, Level = LogLevel.Information, Message = "Template {TemplateKey} draft version {Version} content updated for ({Channel}, {Locale})")]
        private partial void ContentUpdated(string templateKey, int version, string channel, string locale);
    }
}
