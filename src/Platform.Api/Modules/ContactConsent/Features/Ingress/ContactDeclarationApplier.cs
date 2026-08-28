using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using NotificationHub.Api.Modules.ContactConsent.Features.Recipients;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.ContactConsent.Features.Ingress;

/// <summary>How one consumed declaration ended, in terms the transport can settle.</summary>
internal abstract record ContactIngestionResult
{
    private ContactIngestionResult()
    {
    }

    /// <summary>The declaration was applied, or matched the state already in force.</summary>
    internal sealed record Applied : ContactIngestionResult;

    /// <summary>The record was settled before; nothing was written a second time.</summary>
    internal sealed record Duplicate : ContactIngestionResult;

    /// <summary>The declaration can never be applied; the reason is published vocabulary.</summary>
    internal sealed record Refused(string Reason) : ContactIngestionResult;

    /// <summary>A concurrent write won the race; the same record applies cleanly later.</summary>
    internal sealed record Conflict : ContactIngestionResult;
}

/// <summary>
/// Turns one declaration event into the use case that owns it. The two event
/// types of this topic bind to the two commands the REST routes already
/// accept, run through the same validator the route's filter runs, and reach
/// the same handler: there is no second reconciliation anywhere, and no
/// business rule lives here.
///
/// The validator runs explicitly because the REST surface runs it in an
/// endpoint filter, which no consumer passes through. Skipping it would grow a
/// second dialect of shape validation, and the two would drift the first time
/// a rule changed on one side.
/// </summary>
internal sealed class ContactDeclarationApplier(
    DeclareContactPoints.Handler contactPointsHandler,
    DeclareConsents.Handler consentsHandler,
    IValidator<DeclareContactPoints.Command> contactPointsValidator,
    IValidator<DeclareConsents.Command> consentsValidator)
{
    /// <summary>Declaration of the complete contact-point set of one recipient.</summary>
    internal const string ContactPointsDeclaredType = "araia.contact.contact_points_declared.v1";

    /// <summary>Declaration of the desired consent state per purpose and channel.</summary>
    internal const string ConsentsDeclaredType = "araia.contact.consents_declared.v1";

    public Task<ContactIngestionResult> ApplyAsync(
        string eventType,
        string recipientId,
        JsonElement data,
        ContactWriteContext writeContext,
        CancellationToken cancellationToken)
        => eventType switch
        {
            ContactPointsDeclaredType =>
                ApplyContactPointsAsync(recipientId, data, writeContext, cancellationToken),
            ConsentsDeclaredType =>
                ApplyConsentsAsync(recipientId, data, writeContext, cancellationToken),
            _ => Task.FromResult<ContactIngestionResult>(
                new ContactIngestionResult.Refused(ContactIngestionRejectionReasons.EventTypeUnsupported)),
        };

    private async Task<ContactIngestionResult> ApplyContactPointsAsync(
        string recipientId,
        JsonElement data,
        ContactWriteContext writeContext,
        CancellationToken cancellationToken)
    {
        if (ContactEventBinder.BindContactPoints(data) is not { } command)
        {
            return new ContactIngestionResult.Refused(ContactIngestionRejectionReasons.PayloadInvalid);
        }

        ValidationResult validation = await contactPointsValidator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return new ContactIngestionResult.Refused(ContactIngestionRejectionReasons.PayloadInvalid);
        }

        Result<DeclareContactPoints.Outcome> result = await contactPointsHandler.HandleAsync(
            recipientId, command, writeContext, cancellationToken);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"A declaração de pontos de contato falhou de forma inesperada: {result.Error}");
        }

        return result.Value switch
        {
            DeclareContactPoints.Outcome.Declared => new ContactIngestionResult.Applied(),
            DeclareContactPoints.Outcome.Duplicate => new ContactIngestionResult.Duplicate(),
            DeclareContactPoints.Outcome.ConcurrencyConflict => new ContactIngestionResult.Conflict(),
            _ => throw new InvalidOperationException(
                $"Desfecho de declaração não suportado: {result.Value!.GetType().Name}."),
        };
    }

    private async Task<ContactIngestionResult> ApplyConsentsAsync(
        string recipientId,
        JsonElement data,
        ContactWriteContext writeContext,
        CancellationToken cancellationToken)
    {
        if (ContactEventBinder.BindConsents(data) is not { } command)
        {
            return new ContactIngestionResult.Refused(ContactIngestionRejectionReasons.PayloadInvalid);
        }

        ValidationResult validation = await consentsValidator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return new ContactIngestionResult.Refused(ContactIngestionRejectionReasons.PayloadInvalid);
        }

        Result<DeclareConsents.Outcome> result = await consentsHandler.HandleAsync(
            recipientId, command, writeContext, cancellationToken);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"A declaração de consentimentos falhou de forma inesperada: {result.Error}");
        }

        return result.Value switch
        {
            DeclareConsents.Outcome.Declared => new ContactIngestionResult.Applied(),
            DeclareConsents.Outcome.Duplicate => new ContactIngestionResult.Duplicate(),
            DeclareConsents.Outcome.ConcurrencyConflict => new ContactIngestionResult.Conflict(),
            DeclareConsents.Outcome.RecipientUnknown =>
                new ContactIngestionResult.Refused(ContactIngestionRejectionReasons.RecipientUnknown),
            DeclareConsents.Outcome.NoContactPointForChannel =>
                new ContactIngestionResult.Refused(
                    ContactIngestionRejectionReasons.NoContactPointForChannel),
            _ => throw new InvalidOperationException(
                $"Desfecho de declaração não suportado: {result.Value!.GetType().Name}."),
        };
    }
}
