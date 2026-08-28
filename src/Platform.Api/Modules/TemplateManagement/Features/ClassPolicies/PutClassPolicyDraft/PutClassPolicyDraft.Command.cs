using Microsoft.AspNetCore.Mvc;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.ClassPolicies;

internal static partial class PutClassPolicyDraft
{
    internal sealed record RouteInputs(
        string Application,
        string Class,
        [property: FromHeader(Name = "If-Match")] string? IfMatch);

    /// <summary>The HTTP body is the policy definition document itself, carried as raw JSON.</summary>
    internal sealed record Command(RouteInputs Route, string DefinitionJson, string Actor);
}
