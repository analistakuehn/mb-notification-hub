using FluentValidation;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Composition;
using NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;
using NotificationHub.Api.Modules.AttachmentManagement.Features.Operations;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capacity;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.RateLimiting;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Release;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Revocation;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

namespace NotificationHub.Api.Modules.AttachmentManagement;

public sealed class AttachmentManagementModule : IModule, IEndpointModule
{
    public static void ConfigureServices(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAttachmentManagementPersistence(configuration);
        services.AddAttachmentManagementAuthorization();
        services.AddAttachmentManagementRateLimiting();
        services.AddValidatorsFromAssembly(
            typeof(AttachmentManagementModule).Assembly,
            includeInternalTypes: true);
        services.AddAttachmentObjectStore(configuration);
        services.AddAttachmentCapacity(configuration);
        services.AddAttachmentValidation(configuration);
        services.TryAddSingleton(TimeProvider.System);

        // The claim is stateless and joins whatever transaction it is handed,
        // exactly like the audit trail this module borrows the shape from, so
        // one instance serves every caller.
        services.TryAddSingleton<IAttachmentClaim, TransactionalAttachmentClaim>();

        // The two halves of what a caller asks immediately before a call it
        // cannot take back. The capacity measurement holds no state and reads
        // only the configured limits; the release check reads the durable
        // record and takes this module's own context, so it lives as long as
        // that context does.
        services.TryAddSingleton<IAttachmentEnvelopeCheck, AcceptedSetEnvelopeCheck>();
        services.TryAddScoped<IAttachmentReleaseCheck, RecordedAttachmentReleaseCheck>();
        services.AddScoped<AttachmentDependencyRegistry>();
        services.AddScoped<AttachmentDisposal>();
        services.AddScoped<AttachmentRevocationOperation>();
        services.AddScoped<RegisterAttachment.Handler>();
        services.AddScoped<UploadAttachment.Handler>();
        services.AddScoped<GetAttachment.Handler>();
        services.AddScoped<ValidateAttachment.Handler>();
        services.AddScoped<RevokeAttachment.Handler>();
        services.AddScoped<GetAttachmentLifecycle.Handler>();
    }

    public static void MapEndpoints(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder attachments = app.MapGroup("/v1/attachments");
        RegisterAttachment.MapEndpoint(attachments);
        UploadAttachment.MapEndpoint(attachments);
        GetAttachment.MapEndpoint(attachments);
        ValidateAttachment.MapEndpoint(attachments);
        RevokeAttachment.MapEndpoint(attachments);

        // A group of its own, because it is gated by a different policy and
        // answers a different reader. Hanging it under the producer's addresses
        // would put a reading only operations may perform inside the tree a
        // producer already holds a grant over.
        RouteGroupBuilder operations = app.MapGroup("/v1/attachment-operations");
        GetAttachmentLifecycle.MapEndpoint(operations);
    }
}
