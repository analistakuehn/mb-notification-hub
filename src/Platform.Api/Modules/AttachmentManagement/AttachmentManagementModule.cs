using FluentValidation;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Composition;
using NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;
using NotificationHub.Api.Modules.AttachmentManagement.Features.Operations;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capacity;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.RateLimiting;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Reads;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Reconciliation;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Release;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Retention;
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
        services.AddAttachmentReconciliation(configuration);
        services.AddAttachmentRetention(configuration);
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

        // The way to the bytes of an accepted attachment. It creates the
        // context it reads with, so it serves a caller that lives as long as
        // the process: a provider adapter is a singleton, and a scoped
        // dependency there would pin the first scope that resolved it.
        services.TryAddSingleton<IAcceptedAttachmentContent, RecordedAcceptedAttachmentContent>();

        // The witness of what a send actually put on the wire, composed beside
        // the way to the bytes and never apart from it: a host that can open
        // the content and cannot settle the comparison would deliver a set and
        // record nothing about the bytes it delivered.
        services.TryAddSingleton<IAttachmentSubmissionWitness, RecordedAttachmentSubmissionWitness>();

        // The evidence read, scoped like the release check beside it because it
        // reads the module's own context. It is composed for the disclosure
        // surface and for nothing else: a caller that only needs to know
        // whether a set may go out asks the release check, which answers a
        // verdict, while this one hands over the proof of the bytes.
        services.TryAddScoped<IAttachmentEvidence, RecordedAttachmentEvidence>();
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
