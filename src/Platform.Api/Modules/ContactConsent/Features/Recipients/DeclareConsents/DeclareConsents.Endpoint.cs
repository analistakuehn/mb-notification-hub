using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Authorization;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Http;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.ContactConsent.Features.Recipients;

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

        // No provenance on this transport: an HTTP request is not a record the
        // consumer may see twice, so nothing here is deduplicated by mark.
        Result<Outcome> result = await handler.HandleAsync(
            recipientId,
            command,
            new ContactWriteContext(actor.Value.ActorId, actor.Value.ActorType, Provenance: null),
            cancellationToken);
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
