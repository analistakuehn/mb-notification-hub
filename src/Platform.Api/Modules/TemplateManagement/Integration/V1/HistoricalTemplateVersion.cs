namespace NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

/// <summary>
/// The exact version a past notification rendered, whatever the catalog
/// publishes today. Reading the current published version to answer about an
/// old notification is not a partial answer, it is a wrong one: the legal basis,
/// the hashes and the pinned layout of the version that actually shipped are the
/// only ones an approval vouches for.
/// </summary>
/// <remarks>
/// <para>
/// The legal basis, the purpose, the owning team, the class and the sensitive
/// variables belong to the template identity, which this module creates once and
/// never edits; they are therefore the same values the version shipped under.
/// The content hash, the status, the pinned layout and the publication instant
/// belong to the version itself.
/// </para>
/// <para>
/// This is a reference type without value equality on purpose. The version is
/// identified by <see cref="Version"/> under its template together with
/// <see cref="ContentHash"/>, and that is what an approval vouches for.
/// Comparing these members would answer about the sensitive-variable list
/// instance a caller happened to build, which is a fact about that caller and
/// not about what shipped; it would also compare a lifecycle status that moves
/// after publication against a content hash that never does. Ask the hash.
/// </para>
/// </remarks>
public sealed class HistoricalTemplateVersion
{
    public required string Application { get; init; }

    public required string TemplateKey { get; init; }

    public required int Version { get; init; }

    /// <summary>Lifecycle of this version today: published or superseded.</summary>
    public required string VersionStatus { get; init; }

    /// <summary>Lifecycle of the template identity today: active, deprecated or disabled.</summary>
    public required string TemplateStatus { get; init; }

    public required string Class { get; init; }

    public required string OwnerTeam { get; init; }

    public required string Purpose { get; init; }

    public required string LegalBasis { get; init; }

    public required IReadOnlyList<string> SensitiveVariables { get; init; }

    /// <summary>Canonical content hash of this version, the value its approval vouches for.</summary>
    public required string ContentHash { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Version this one republished, when a rollback created it.</summary>
    public int? RolledBackFromVersion { get; init; }

    /// <summary>
    /// The pin exactly as the version declared it, key and number, present
    /// whenever the version pinned anything and whether or not <see cref="Layout"/>
    /// resolved. Its absence is the one way this answer states that the message
    /// went out framed by nothing.
    /// </summary>
    public HistoricalLayoutPin? LayoutPin { get; init; }

    /// <summary>
    /// The layout this version pinned, with its own canonical hash, present only
    /// when the pin still resolves to a published or superseded layout version.
    /// </summary>
    /// <remarks>
    /// Three different states leave this member absent, and <see cref="LayoutPin"/>
    /// is what tells them apart. No pin at all: both members are absent, and that
    /// is the only legitimate one, a version that framed its message with nothing.
    /// Pin present and this member absent: the version pinned a wrapper and the
    /// read could not vouch for it, either because the pinned version is no longer
    /// in the store or because it never left draft. Neither of those two is
    /// reachable through a legitimate path, since publishing required the pin to
    /// resolve to a published layout version, the layout lifecycle never walks
    /// back, and nothing deletes a layout version. The read records both at error
    /// level, which is where the two are told apart from each other.
    /// </remarks>
    public HistoricalLayoutVersion? Layout { get; init; }
}

/// <summary>
/// The layout reference a template version declared, copied off the version row
/// and resolved against nothing. It answers whether the message was framed at
/// all; <see cref="HistoricalLayoutVersion"/> is what answers for the content of
/// the frame.
/// </summary>
public sealed record HistoricalLayoutPin
{
    public required string LayoutKey { get; init; }

    /// <summary>Layout version number this template version pinned.</summary>
    public required int Version { get; init; }
}

/// <summary>The layout version a template version pinned, as it was published.</summary>
public sealed record HistoricalLayoutVersion
{
    public required string LayoutKey { get; init; }

    public required int Version { get; init; }

    /// <summary>Lifecycle of the pinned version today: published or superseded.</summary>
    public required string VersionStatus { get; init; }

    public required string ContentHash { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }
}
