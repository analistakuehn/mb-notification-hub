namespace NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

/// <summary>
/// Reasons the published catalog reports for a template identity that no
/// longer accepts new notification requests. The values belong to the
/// canonical rejection-reason catalog shared with the notification events, so
/// a consumer forwards them without translation.
/// </summary>
public static class TemplateRejectionReasons
{
    public const string Deprecated = "template-deprecated";
    public const string Disabled = "template-disabled";
}

/// <summary>
/// Reasons the published render refuses to frame a message with the layout a
/// version pins. The values belong to the canonical rejection-reason catalog
/// shared with the notification events, so a consumer forwards them without
/// translation.
/// </summary>
public static class LayoutRejectionReasons
{
    /// <summary>
    /// The layout identity is disabled, so nothing frames a message with it
    /// any more. Every template that pins it is refused, whatever its class:
    /// rendering the body without the wrapper would ship content whose
    /// canonical hash matches nothing that was ever approved, and a layout is
    /// disabled precisely when the wrapper itself is what must stop going out.
    /// </summary>
    public const string Disabled = "layout-disabled";
}

/// <summary>
/// Reasons the published render refuses content it produced correctly. The
/// values belong to the canonical rejection-reason catalog shared with the
/// notification events, so a consumer forwards them without translation.
/// </summary>
public static class RenderedContentRejectionReasons
{
    /// <summary>
    /// The render is larger than the channel carries. The word names no
    /// channel on purpose: the unit differs per channel, and a channel that
    /// gains a ceiling later has to reuse this member instead of adding one to
    /// a closed catalog.
    /// </summary>
    public const string TooLarge = "rendered-content-too-large";
}

/// <summary>
/// The purpose vocabulary a sibling module has to recognize. The vocabulary
/// itself stays open, because it is governance data an author writes; only the
/// one word that changes how a notification is treated is published here.
/// </summary>
public static class TemplatePurposes
{
    /// <summary>
    /// The canonical purpose of a template that carries authentication
    /// material. Lower case is the stored form: the aggregate canonizes on the
    /// way in, and every reader compares against this exact value.
    /// </summary>
    public const string Authentication = "authentication";

    /// <summary>
    /// Whether a purpose puts a template in an authentication flow. It is one
    /// question asked in six places: the publication catalog that bans a link
    /// in an SMS, the render that refuses to ship one, the acceptance that
    /// materializes the flow on the notification, the two recipient reads that
    /// may fall back to the last known snapshot, and the routing that names the
    /// authentication queue. They ask it here so the next change of criterion
    /// reaches all six.
    /// <para>
    /// The comparison is ordinal and must stay ordinal. The value is canonized
    /// at the aggregate's single write door, so nothing else can exist; the
    /// go-live gate asks the same question in SQL, where folding case would
    /// give up the index and create a second answer to what is canonical; and
    /// a stored value in another case has to stay visible in the catalog and in
    /// the compliance evidence rather than be silently read past.
    /// </para>
    /// </summary>
    public static bool IsAuthentication(string? purpose)
        => string.Equals(purpose, Authentication, StringComparison.Ordinal);
}

/// <summary>
/// Decision metadata of the published version of a template: everything a
/// sibling module needs to validate, route and audit a notification request
/// without touching this module's internals.
/// </summary>
/// <remarks>
/// This is a reference type without value equality on purpose. What says two
/// of these describe the same published version is <see cref="ContentHash"/>,
/// computed over the stored content rather than over the decision metadata
/// projected here, so two versions whose members match one by one can still
/// ship different content. Comparing instances would also answer about
/// whichever list instance a producer happened to hand over for the two
/// collection members, which is a fact about that producer and not about the
/// version. Ask the hash.
/// </remarks>
public sealed class PublishedTemplate
{
    public required string Application { get; init; }

    public required string TemplateKey { get; init; }

    /// <summary>Canonical class value: critical, transactional or operational.</summary>
    public required string Class { get; init; }

    public required string OwnerTeam { get; init; }

    public required string Purpose { get; init; }

    public required string LegalBasis { get; init; }

    /// <summary>Variable names whose values only ever leave this module masked on the stored render form.</summary>
    public required IReadOnlyList<string> SensitiveVariables { get; init; }

    /// <summary>Channels the published version ships content for.</summary>
    public required IReadOnlyList<Channel> ChannelsWithContent { get; init; }

    /// <summary>Locale the content fallback chain lands on, when the template declares one.</summary>
    public string? DefaultLocale { get; init; }

    /// <summary>Number of the published version.</summary>
    public required int Version { get; init; }

    /// <summary>Canonical content hash of the published version, the value its approval vouches for.</summary>
    public required string ContentHash { get; init; }
}

/// <summary>
/// Outcome of a published-template lookup: either the decision metadata of the
/// published version, or the catalog reason the consumer must reject new
/// requests with. The rejection is data, not an error, because rejecting is
/// the consumer's own legitimate flow.
/// </summary>
public abstract record PublishedTemplateLookup
{
    private PublishedTemplateLookup()
    {
    }

    /// <summary>The template is active and has a published version.</summary>
    public sealed record Published(PublishedTemplate Template) : PublishedTemplateLookup;

    /// <summary>
    /// The template rejects new requests; <see cref="Reason"/> carries the
    /// catalog value from <see cref="TemplateRejectionReasons"/>.
    /// </summary>
    public sealed record Rejected(string Reason) : PublishedTemplateLookup;
}
