using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Authorization;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Http;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.ContactConsent.Features.Mutations;

internal static partial class DeclareConsents
{
    private const int MaxRecipientIdLength = 100;

    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapPut("/{recipientId}/consents", HandleHttpAsync)
            .RequireAuthorization(ContactConsentAuthorizationSetup.WritePolicyName)
            .RequireRateLimiting(ContactConsentRateLimitingSetup.PolicyName)
            .WithValidation<Command>()
            .WithRequestLogging();

    private static async Task<IResult> HandleHttpAsync(
        string recipientId,
        Command command,
        HttpContext httpContext,
        Handler handler,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recipientId) || recipientId.Length > MaxRecipientIdLength)
        {
            return ContactConsentProblems.RecipientIdInvalid();
        }

        (string ActorId, string ActorType)? actor = ContactWriterIdentity.Identify(httpContext.User);
        if (actor is null)
        {
            return ContactConsentProblems.WriterIdentityRequired();
        }

        Result<Outcome> result = await handler.HandleAsync(
            recipientId, command, actor.Value.ActorId, actor.Value.ActorType, cancellationToken);
        if (result.IsFailure)
        {
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError);
        }

        return result.Value switch
        {
            Outcome.Declared declared => Results.Ok(declared.Response),
            Outcome.RecipientUnknown => ContactConsentProblems.RecipientNotFound(recipientId),
            Outcome.NoContactPointForChannel missing =>
                ContactConsentProblems.NoContactPointForChannel(missing.Channel),
            Outcome.ConcurrencyConflict => ContactConsentProblems.ConcurrentUpdateConflict(),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }
}
