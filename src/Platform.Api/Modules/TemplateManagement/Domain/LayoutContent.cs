using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Content of one (channel, locale) pair inside a layout version: the Scriban
/// wrapper the rendered template body lands in, plus an optional wrapper for
/// the plain-text variant. Owned by <see cref="LayoutVersion"/>; every edit
/// refreshes <see cref="BodyHash"/>.
/// </summary>
public sealed class LayoutContent
{
    internal LayoutContent(Channel channel, Locale locale, string body, string? bodyText)
    {
        Channel = channel;
        Locale = locale;
        Body = body;
        BodyText = bodyText;
        BodyHash = CanonicalHash.OfFields(body, bodyText);
    }

    // EF Core materialization: fields are populated from the store.
    private LayoutContent()
    {
        Channel = null!;
        Locale = null!;
        Body = null!;
        BodyHash = null!;
    }

    public Channel Channel { get; }

    public Locale Locale { get; }

    public string Body { get; private set; }

    public string? BodyText { get; private set; }

    public string BodyHash { get; private set; }

    internal void Update(string body, string? bodyText)
    {
        Body = body;
        BodyText = bodyText;
        BodyHash = CanonicalHash.OfFields(body, bodyText);
    }
}
