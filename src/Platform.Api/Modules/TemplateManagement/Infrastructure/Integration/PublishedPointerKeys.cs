namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;

/// <summary>
/// The single place the memoized keys of the published read surface are built.
/// The reader and the transition that invalidates it call the same builder on
/// purpose: a prefix changed on one side only would switch the invalidation
/// off without breaking the compilation, and the pointer window would silently
/// go back to being the whole story.
/// </summary>
/// <remarks>
/// Every argument is the canonical form the domain produced, which is what
/// makes a key reconstructible from a committed row: the identity factories
/// trim and refuse, they never transform, so the persisted value is byte for
/// byte the one the reader looked up.
/// </remarks>
internal static class PublishedPointerKeys
{
    /// <summary>The published decision metadata of one template identity.</summary>
    internal static string Template(string application, string templateKey)
        => $"template:{application}:{templateKey}";

    /// <summary>
    /// The published context the render and the variables validation share for
    /// one template identity.
    /// </summary>
    internal static string RenderContext(string application, string templateKey)
        => $"render-context:{application}:{templateKey}";

    /// <summary>The published policy of one class of one application.</summary>
    internal static string ClassPolicy(string application, string notificationClass)
        => $"policy:{application}:{notificationClass}";

    /// <summary>
    /// The status and the default locale of a layout identity, which is the
    /// mutable half of a layout and the only half a lifecycle transition
    /// moves.
    /// </summary>
    internal static string LayoutIdentity(string layoutKey)
        => $"layout-identity:{layoutKey}";

    /// <summary>
    /// A pinned layout version, immutable by the governance contract and for
    /// that reason never invalidated: a template version fixes a layout
    /// version number, and publishing or rolling back another version of that
    /// layout leaves the fixed number answering the same bytes.
    /// </summary>
    internal static string LayoutVersion(string layoutKey, int version)
        => $"layout-version:{layoutKey}:{version}";
}
