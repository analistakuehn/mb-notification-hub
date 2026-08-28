using System.Security.Claims;
using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

internal static partial class RollbackLayout
{
    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapPost("/{key}/rollback", HandleHttpAsync)
            .RequireAuthorization(AuthorizationSetup.PublisherPolicyName)
            .RequireRateLimiting(RateLimitingSetup.PolicyName)
            .WithValidation<Request>()
            .WithRequestLogging();

    private static async Task<IResult> HandleHttpAsync(
        string key,
        Request request,
        ClaimsPrincipal principal,
        Handler handler,
        CancellationToken cancellationToken)
    {
        Result<string> actor = CurrentActor.Identify(principal);
        if (actor.IsFailure)
        {
            return ApiResults.Problem(actor);
        }

        Result<Outcome> result = await handler.HandleAsync(
            new Command(key, request.ToVersion, actor.Value!),
            cancellationToken);
        if (result.IsFailure)
        {
            return ApiResults.Problem(result);
        }

        return result.Value switch
        {
            Outcome.RolledBack rolledBack => Results.Ok(rolledBack.Response),
            Outcome.Blocked blocked => ApiResults.Problem(
                blocked.Report,
                ErrorCodes.LayoutValidationFailed,
                $"The layout validation blocked the rollback to version {request.ToVersion}. "
                + "The full report travels in the 'checks' extension member."),
            _ => throw new InvalidOperationException("Unsupported rollback outcome."),
        };
    }
}
