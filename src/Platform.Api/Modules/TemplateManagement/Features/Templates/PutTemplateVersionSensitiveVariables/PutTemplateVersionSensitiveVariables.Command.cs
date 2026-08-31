using Microsoft.AspNetCore.Mvc;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class PutTemplateVersionSensitiveVariables
{
    /// <summary>
    /// HTTP body: the whole declaration of which variables carry sensitive
    /// data. The list replaces the previous one, so an absent or empty list
    /// declares that this version carries none.
    /// </summary>
    internal sealed record Request(IReadOnlyList<string>? SensitiveVariables);

    internal sealed record RouteInputs(
        string Key,
        int Version,
        [property: FromHeader(Name = "If-Match")] string? IfMatch);

    internal sealed record Command(RouteInputs Route, Request Declaration, string Actor);
}
