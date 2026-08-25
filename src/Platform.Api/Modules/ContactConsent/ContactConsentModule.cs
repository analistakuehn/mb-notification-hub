using FluentValidation;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Composition;
using NotificationHub.Api.Modules.ContactConsent.Features.Mutations;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Authorization;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Privacy;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.RateLimiting;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Reads;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Suppression;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;

namespace NotificationHub.Api.Modules.ContactConsent;

public sealed class ContactConsentModule : IModule, IEndpointModule
{
    public static void ConfigureServices(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddContactConsentPersistence(configuration);
        services.AddContactConsentAuthorization();
        services.AddContactConsentRateLimiting();
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<ContactValueProtector>();
        services.AddScoped<ContactConsentWriter>();
        services.AddScoped<IRecipientDirectory, RecipientDirectory>();

        // Reconstruction surface: lifecycle stamps and the consent ledger for
        // an evidence composer, never for the support query surface.
        services.AddScoped<IContactHistory, ContactHistoryReader>();

        // Write side of the suppression ledger. The API host composes it for
        // the reversal route; the delivery-feedback path composes the same
        // implementation in its worker role, through the composition root.
        services.AddScoped<ISuppressionLedger, SuppressionLedger>();

        services.AddScoped<DeclareContactPoints.Handler>();
        services.AddScoped<DeclareConsents.Handler>();
        services.AddScoped<RegisterDevice.Handler>();
        services.AddScoped<RemoveSuppression.Handler>();
        services.TryAddScoped<IValidator<DeclareContactPoints.Command>, DeclareContactPoints.Validator>();
        services.TryAddScoped<IValidator<DeclareConsents.Command>, DeclareConsents.Validator>();
        services.TryAddScoped<IValidator<RegisterDevice.Command>, RegisterDevice.Validator>();
        services.TryAddScoped<IValidator<RemoveSuppression.Command>, RemoveSuppression.Validator>();
    }

    public static void MapEndpoints(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder recipients = app.MapGroup("/v1/recipients");
        DeclareContactPoints.MapEndpoint(recipients);
        DeclareConsents.MapEndpoint(recipients);
        RegisterDevice.MapEndpoint(recipients);
        RemoveSuppression.MapEndpoint(recipients);
    }
}
