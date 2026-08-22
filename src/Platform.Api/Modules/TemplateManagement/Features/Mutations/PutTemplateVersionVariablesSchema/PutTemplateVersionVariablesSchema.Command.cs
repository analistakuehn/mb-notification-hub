using Microsoft.AspNetCore.Mvc;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PutTemplateVersionVariablesSchema
{
    internal sealed record RouteInputs(
        string Key,
        int Version,
        [property: FromHeader(Name = "If-Match")] string? IfMatch);

    /// <summary>The HTTP body is the JSON Schema document itself, carried as raw JSON.</summary>
    internal sealed record Command(RouteInputs Route, string SchemaJson, string Actor);
}
