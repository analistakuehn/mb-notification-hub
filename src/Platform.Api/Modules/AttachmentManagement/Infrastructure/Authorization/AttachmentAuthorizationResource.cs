namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Authorization;

internal abstract record AttachmentAuthorizationResource(CancellationToken CancellationToken)
{
    internal sealed record Application(
        string Name,
        CancellationToken CancellationToken)
        : AttachmentAuthorizationResource(CancellationToken);

    internal sealed record Reference(
        string Value,
        CancellationToken CancellationToken)
        : AttachmentAuthorizationResource(CancellationToken);
}
