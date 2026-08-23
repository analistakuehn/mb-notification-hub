using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Content of one (channel, locale) pair inside a template version. Owned by
/// <see cref="TemplateVersion"/>; every edit refreshes <see cref="BodyHash"/>.
/// </summary>
public sealed class TemplateContent
{
    internal TemplateContent(Channel channel, Locale locale, string? subject, string body, string? bodyText)
    {
        Channel = channel;
        Locale = locale;
        Subject = subject;
        Body = body;
        BodyText = bodyText;
        BodyHash = CanonicalHash.OfFields(subject, body, bodyText);
    }

    // EF Core materialization: fields are populated from the store.
    private TemplateContent()
    {
        Channel = null!;
        Locale = null!;
        Body = null!;
        BodyHash = null!;
    }

    public Channel Channel { get; }

    public Locale Locale { get; }

    public string? Subject { get; private set; }

    public string Body { get; private set; }

    public string? BodyText { get; private set; }

    public string BodyHash { get; private set; }

    internal void Update(string? subject, string body, string? bodyText)
    {
        Subject = subject;
        Body = body;
        BodyText = bodyText;
        BodyHash = CanonicalHash.OfFields(subject, body, bodyText);
    }
}
