using System.Security.Claims;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;
using NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.KillSwitch;

internal static partial class KillSwitchAdministration
{
    internal sealed record Request(bool? Active);

    internal sealed record Response(
        string Scope,
        string Key,
        string State,
        long Version,
        DateTimeOffset? UpdatedAt,
        bool Changed);

    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapPut("/{scope}/{key}", HandleHttpAsync)
            .RequireAuthorization(NotificationsAuthorizationSetup.KillSwitchAdminPolicyName)
            .RequireRateLimiting(NotificationsRateLimitingSetup.KillSwitchAdminPolicyName);

    private static async Task<IResult> HandleHttpAsync(
        string scope,
        string key,
        Request request,
        ClaimsPrincipal principal,
        Handler handler,
        CancellationToken cancellationToken)
    {
        if (!KillSwitchScopes.TryParse(scope, out KillSwitchScope parsedScope)
            || !KillSwitchKeys.TryNormalize(parsedScope, key, out var normalizedKey))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "kill-switch-address-invalid",
                "O escopo ou a chave do kill switch é inválido.");
        }

        var actor = principal.FindFirstValue("oid") ?? principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Problem(
                StatusCodes.Status403Forbidden,
                "kill-switch-actor-unidentified",
                "O token não contém oid nem sub para identificar o ator.");
        }

        if (request.Active is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "kill-switch-active-required",
                "O corpo precisa informar active.");
        }

        Result<ChangeResult> result = await handler.HandleAsync(
            new ChangeCommand(parsedScope, normalizedKey, request.Active.Value, actor),
            cancellationToken);
        if (result.IsFailure)
        {
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError);
        }

        ChangeResult change = result.Value!;
        if (change.Conflict)
        {
            return Problem(
                StatusCodes.Status409Conflict,
                "kill-switch-concurrency-conflict",
                "O estado mudou durante a transição; leia o estado atual e tente novamente.");
        }

        return Results.Ok(new Response(
            parsedScope.Canonical(),
            normalizedKey,
            change.State,
            change.Version,
            change.UpdatedAt,
            change.Changed));
    }

    private static IResult Problem(int statusCode, string type, string detail)
        => Results.Problem(statusCode: statusCode, title: type, type: type, detail: detail);
}
