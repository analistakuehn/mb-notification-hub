namespace NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

/// <summary>
/// Why a template or a layout left circulation. The value is a code and not a
/// sentence, because the periodic evidence read groups the trail by this field
/// and the archived report copies the group name verbatim: free text turns
/// every operator phrasing into a category of its own, and a category named
/// after a single incident is a category nobody can count.
///
/// It is published rather than internal for a mechanical reason: the reader
/// that does the grouping sits outside this module and may only depend on this
/// surface, so a vocabulary declared in the domain would be unreachable
/// exactly where it is needed.
///
/// The list is a floor and not a cage. <see cref="Other"/> exists so an
/// operator who finds no entry can still stop the traffic, and the note that
/// entry requires is what keeps the escape hatch readable.
/// </summary>
public static class LifecycleReasons
{
    /// <summary>A newer version took over the traffic this one carried.</summary>
    public const string SupersededByNewVersion = "superseded-by-new-version";

    /// <summary>The brand or the visual language moved on and the artifact did not follow.</summary>
    public const string VisualIdentityChange = "visual-identity-change";

    /// <summary>The journey behind the artifact ended and nothing replaces it.</summary>
    public const string Retired = "retired";

    /// <summary>What the artifact renders is wrong, and the reader is the one paying for it.</summary>
    public const string ContentIncorrect = "content-incorrect";

    /// <summary>The content was tampered with, or carries something it must not carry.</summary>
    public const string ContentCompromised = "content-compromised";

    /// <summary>None of the entries fits. The note that comes with it is what says what happened.</summary>
    public const string Other = "other";

    /// <summary>Every member, in the order an operator reads them in a refusal.</summary>
    public static IReadOnlyList<string> CanonicalValues { get; } =
    [
        SupersededByNewVersion,
        VisualIdentityChange,
        Retired,
        ContentIncorrect,
        ContentCompromised,
        Other,
    ];

    /// <summary>
    /// Whether a value belongs to the vocabulary. A predicate and never a
    /// materializer that throws: the same field is read back from rows written
    /// before this vocabulary existed, and a reader that throws on an unknown
    /// code turns one dirty row into an unavailable report.
    /// </summary>
    public static bool IsCanonical(string? reason)
        => reason is SupersededByNewVersion
            or VisualIdentityChange
            or Retired
            or ContentIncorrect
            or ContentCompromised
            or Other;
}
