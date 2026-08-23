using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class DiffClassPolicyVersions
{
    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapGet("/versions/{version:int}/diff", HandleHttpAsync)
            .RequireAuthorization(AuthorizationSetup.AuthorPolicyName)
            .RequireRateLimiting(RateLimitingSetup.PolicyName)
            .WithRequestLogging();

    private static async Task<IResult> HandleHttpAsync(
        string application,
        string @class,
        int version,
        int? against,
        Handler handler,
        CancellationToken cancellationToken)
    {
        if (against is not int againstVersion || againstVersion < 1)
        {
            return ApiResults.Problem(ResultErrorKind.Validation, DomainError.Format(
                ErrorCodes.InvalidRequest,
                "The 'against' query parameter is required and must be a positive version number."));
        }

        Result<Response> result = await handler.HandleAsync(
            new Query(application, @class, version, againstVersion),
            cancellationToken);
        return result.IsFailure ? ApiResults.Problem(result) : Results.Ok(result.Value);
    }
}
