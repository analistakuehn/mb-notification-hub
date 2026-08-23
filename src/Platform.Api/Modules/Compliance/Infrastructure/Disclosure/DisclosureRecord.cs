namespace NotificationHub.Api.Modules.Compliance.Infrastructure.Disclosure;

/// <summary>Canonical scope values a disclosure records.</summary>
internal static class DisclosureScopes
{
    /// <summary>The reconstruction of one notification: trail, state and prior accesses.</summary>
    internal const string NotificationEvidence = "notification-evidence";

    /// <summary>The stored content of one attempt, opened and served.</summary>
    internal const string AttemptContent = "attempt-content";
}

/// <summary>Canonical names of the content forms this surface can serve.</summary>
internal static class DisclosedContentForms
{
    internal const string Masked = "masked";
}

/// <summary>Who disclosed and through which route.</summary>
internal sealed record DisclosureActor(string ActorId, string Route);

/// <summary>Hashes of one attempt that left in an answer, never its content.</summary>
internal sealed record DisclosedAttemptHashes(int Sequence, string ContentHashMasked, string ContentHashFull);

/// <summary>Everything the trail records about one reconstruction that was served.</summary>
internal sealed record EvidenceDisclosure
{
    public required DisclosureActor Actor { get; init; }

    public required Guid NotificationId { get; init; }

    public required string Application { get; init; }

    public required string RecipientId { get; init; }

    public required IReadOnlyList<DisclosedAttemptHashes> Attempts { get; init; }

    public required int TrailLinkCount { get; init; }

    public required int PriorAccessCount { get; init; }
}

/// <summary>Everything the trail records about one content opening that was served.</summary>
internal sealed record ContentDisclosure
{
    public required DisclosureActor Actor { get; init; }

    public required Guid NotificationId { get; init; }

    public required string Application { get; init; }

    public required int Sequence { get; init; }

    /// <summary>Which form of the content left; only the masked one ever does.</summary>
    public required string DisclosedForm { get; init; }

    public required string ContentHashMasked { get; init; }

    public required string ContentHashFull { get; init; }

    /// <summary>Whether the recomputed masked hash matched the recorded one.</summary>
    public required bool ContentHashVerified { get; init; }
}
