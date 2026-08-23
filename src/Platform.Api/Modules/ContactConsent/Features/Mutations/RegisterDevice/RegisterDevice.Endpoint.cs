using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Authorization;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Http;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.ContactConsent.Features.Mutations;

internal static partial class RegisterDevice
{
    private const int MaxRecipientIdLength = 100;

    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapPost("/{recipientId}/devices", HandleHttpAsync)
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

        // Device registration exists on this transport only: the token is
        // registered by the app, never declared by the registration system.
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
            Outcome.Registered registered => Results.Ok(registered.Response),
            Outcome.ConcurrencyConflict => ContactConsentProblems.ConcurrentUpdateConflict(),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }
}
