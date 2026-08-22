namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class CreateTemplateVersion
{
    internal sealed partial class Handler
    {
        [LoggerMessage(EventId = 2010, Level = LogLevel.Information, Message = "Template {TemplateKey} draft version {Version} opened")]
        private partial void DraftOpened(string templateKey, int version);

        [LoggerMessage(EventId = 2011, Level = LogLevel.Information, Message = "Template {TemplateKey} draft version {Version} opened as a clone of version {FromVersion}")]
        private partial void DraftCloned(string templateKey, int version, int fromVersion);
    }
}
