using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Authorization;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Http;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.ContactConsent.Features.Mutations;

internal static partial class RemoveSuppression
{
    private const int MaxRecipientIdLength = 100;

    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapPost("/{recipientId}/suppressions/{contactPointId:guid}/removal", HandleHttpAsync)
            .RequireAuthorization(ContactConsentAuthorizationSetup.SuppressionRemovalPolicyName)
            .RequireRateLimiting(ContactConsentRateLimitingSetup.SuppressionRemovalPolicyName)
            .WithValidation<Command>()
            .WithRequestLogging();

    private static async Task<IResult> HandleHttpAsync(
        string recipientId,
        Guid contactPointId,
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

        // No provenance: the reversal exists on this transport only. Nothing
        // declares it over the bus, precisely because a human has to answer
        // for it.
        Result<Outcome> result = await handler.HandleAsync(
            recipientId,
            contactPointId,
            command,
            new ContactWriteContext(actor.Value.ActorId, actor.Value.ActorType, Provenance: null),
            cancellationToken);
        if (result.IsFailure)
        {
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError);
        }

        return result.Value switch
        {
            Outcome.Removed removed => Results.Ok(removed.Response),
            Outcome.NotSuppressed => Results.Ok(new { contactPointId, removed = false }),
            Outcome.ContactPointNotFound => ContactConsentProblems.ContactPointNotFound(),
            Outcome.ConcurrencyConflict => ContactConsentProblems.ConcurrentUpdateConflict(),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }
}
