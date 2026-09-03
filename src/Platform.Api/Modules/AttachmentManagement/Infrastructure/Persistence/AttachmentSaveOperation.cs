namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

internal interface IAttachmentSaveOperation
{
    Task SaveChangesAsync(
        AttachmentManagementDbContext dbContext,
        CancellationToken cancellationToken);
}

internal sealed class AttachmentSaveOperation : IAttachmentSaveOperation
{
    public Task SaveChangesAsync(
        AttachmentManagementDbContext dbContext,
        CancellationToken cancellationToken)
        => dbContext.SaveChangesAsync(cancellationToken);
}
