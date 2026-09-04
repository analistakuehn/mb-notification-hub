using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Composition;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Reconciliation;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Retention;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;

namespace NotificationHub.Api.Modules.AttachmentManagement;

/// <summary>
/// Composition of the <c>attachment-maintenance</c> worker role, owned by this
/// module: the round that carries out the repairs attachments are recorded as
/// owing.
/// </summary>
/// <remarks>
/// A role of its own, and not a passenger of the request-serving host, for two
/// reasons. The round removes durable bytes, which is not work that may run
/// once per replica of a host scaled by traffic; and the repairs it carries out
/// must happen whether or not anybody is uploading, because the rows it
/// repairs are precisely the ones whose next upload is being refused.
/// <para>
/// The composition is deliberately narrow. It takes the module's own store,
/// the custody it removes through, the inventory it discovers orphans through,
/// and the validation that owns the state machine a waiting verdict ends in.
/// It composes no endpoint, no authorization and no rate limiter: nothing here
/// answers a caller.
/// </para>
/// </remarks>
public sealed class AttachmentMaintenanceWorkerRole : IWorkerRoleModule
{
    public static string Role => "attachment-maintenance";

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddAttachmentManagementPersistence(configuration);
        services.AddAttachmentObjectStore(configuration);

        services.AddAttachmentValidation(configuration);
        services.AddAttachmentReconciliation(configuration);
        services.AddAttachmentRetention(configuration);

        // The operation that removes the bytes of one attachment and refuses
        // while anything depends on it. It is composed here because the sweep
        // of abandoned attachments is its first caller that is not a request,
        // and because this role is the only one allowed to reach it: a host
        // scaled by traffic would run the sweep once per replica.
        services.TryAddScoped<AttachmentDisposal>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService<AttachmentReconciliationService>();
        services.AddHostedService<AttachmentAbandonmentService>();
    }
}
