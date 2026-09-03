namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

public sealed class AttachmentObjectStoreOptions
{
    public const string SectionName = "Modules:AttachmentManagement:Storage:S3";

    public string? Bucket { get; init; }

    public string? ServiceUrl { get; init; }

    public string? Region { get; init; }

    public string? AccessKey { get; init; }

    public string? SecretKey { get; init; }

    public bool ForcePathStyle { get; init; }

    internal bool IsUsable
        => !string.IsNullOrWhiteSpace(Bucket)
            && (string.IsNullOrWhiteSpace(AccessKey) == string.IsNullOrWhiteSpace(SecretKey));
}
