namespace NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

/// <summary>
/// Published class policy definition together with the decision metadata a
/// notification must record: the policy version that ruled it and the content
/// hash its approval vouches for.
/// </summary>
public sealed record PublishedClassPolicy
{
    public required string Application { get; init; }

    /// <summary>Canonical class value: critical, transactional or operational.</summary>
    public required string Class { get; init; }

    /// <summary>Number of the published policy version.</summary>
    public required int Version { get; init; }

    /// <summary>Canonical content hash of the published definition.</summary>
    public required string ContentHash { get; init; }

    /// <summary>The stored definition read through the tolerant version-1 vocabulary.</summary>
    public required ClassPolicyDefinition Definition { get; init; }
}
