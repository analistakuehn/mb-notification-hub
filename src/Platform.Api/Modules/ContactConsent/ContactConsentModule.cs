using FluentValidation;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Composition;
using NotificationHub.Api.Modules.ContactConsent.Features.Mutations;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Authorization;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Privacy;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.RateLimiting;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Reads;
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

        services.AddScoped<DeclareContactPoints.Handler>();
        services.AddScoped<DeclareConsents.Handler>();
        services.AddScoped<RegisterDevice.Handler>();
        services.TryAddScoped<IValidator<DeclareContactPoints.Command>, DeclareContactPoints.Validator>();
        services.TryAddScoped<IValidator<DeclareConsents.Command>, DeclareConsents.Validator>();
        services.TryAddScoped<IValidator<RegisterDevice.Command>, RegisterDevice.Validator>();
    }

    public static void MapEndpoints(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder recipients = app.MapGroup("/v1/recipients");
        DeclareContactPoints.MapEndpoint(recipients);
        DeclareConsents.MapEndpoint(recipients);
        RegisterDevice.MapEndpoint(recipients);
    }
}
