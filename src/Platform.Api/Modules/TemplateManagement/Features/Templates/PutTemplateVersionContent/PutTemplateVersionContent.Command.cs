using Microsoft.AspNetCore.Mvc;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class PutTemplateVersionContent
{
    /// <summary>HTTP body: the content fields for one (channel, locale) pair.</summary>
    internal sealed record Request(string? Subject, string Body, string? BodyText);

    internal sealed record RouteInputs(
        string Key,
        int Version,
        string Channel,
        string Locale,
        [property: FromHeader(Name = "If-Match")] string? IfMatch);

    internal sealed record Command(RouteInputs Route, Request Content, string Actor);
}
