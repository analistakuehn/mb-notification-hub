using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

/// <summary>
/// The canonical hash of one rendered form, published so a consumer can verify
/// a stored render without reimplementing the rule. This module owns the rule
/// because it owns the render: a second implementation would drift and turn a
/// verification failure into an argument about whose hash is right.
/// </summary>
public static class RenderedContentHash
{
    /// <summary>
    /// Hashes exactly the three fields a <see cref="RenderedForm"/> ships, in
    /// the same canonical form <see cref="IPublishedTemplateRenderer"/> used
    /// when it produced the value stored beside the content.
    /// </summary>
    public static string OfForm(string? subject, string body, string? bodyText)
        => CanonicalHash.OfFields(subject, body, bodyText);
}
