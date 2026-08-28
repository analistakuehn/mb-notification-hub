using Microsoft.AspNetCore.Mvc;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class PutTemplateVersionLayout
{
    /// <summary>
    /// HTTP body: the layout pin of the draft. Both fields set pin an exact
    /// layout version; both fields null clear the reference.
    /// </summary>
    internal sealed record Request(string? LayoutKey, int? LayoutVersion);

    internal sealed record RouteInputs(
        string Key,
        int Version,
        [property: FromHeader(Name = "If-Match")] string? IfMatch);

    internal sealed record Command(RouteInputs Route, Request Reference, string Actor);
}
