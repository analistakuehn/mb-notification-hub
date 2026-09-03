namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

/// <summary>
/// Reasons this module records when someone takes a dependency. They describe
/// why a hold was taken and are never read to decide whether it is live: what
/// makes a dependency live is the absence of a release, so a reason nobody
/// listed here still protects the object.
/// </summary>
internal static class AttachmentDependencyReasons
{
    internal const string ClaimConfirmed = "claim-confirmed";
    internal const string AttemptSending = "attempt-sending";
    internal const string AttemptUnknown = "attempt-unknown";
}

/// <summary>
/// One dependent's hold over one attachment. The row carries identifiers only,
/// never content, and it is the module's own record: the custody context never
/// asks another context whether its work is still running, it is told and it
/// remembers.
/// </summary>
internal sealed class AttachmentDependency
{
    internal const int MaxReasonLength = 40;
    internal const int MaxHolderLength = 100;

    // EF Core materialization and query projection only; writes go through an
    // upsert and an update that both key on the live row.
    private AttachmentDependency()
    {
        Reason = null!;
        Holder = null!;
    }

    internal Guid Id { get; private set; }

    internal Guid AttachmentId { get; private set; }

    /// <summary>Why the hold was taken, as declared by the dependent.</summary>
    internal string Reason { get; private set; }

    /// <summary>Opaque identity of the work that depends on the attachment.</summary>
    internal string Holder { get; private set; }

    internal DateTimeOffset AcquiredAt { get; private set; }

    /// <summary>Null while the dependency is live. Nothing else ends it.</summary>
    internal DateTimeOffset? ReleasedAt { get; private set; }

    internal long Version { get; private set; }
}
