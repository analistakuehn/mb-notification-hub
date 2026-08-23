using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Unit of publication of a layout. Only a draft accepts edits; a published
/// version is immutable forever. The aggregate records the draft author and every
/// editor because publication must be performed by someone who touched neither.
/// <see cref="ContentHash"/> covers the canonical content of the whole version and
/// is refreshed on every edit; <see cref="EntityTag"/> backs optimistic concurrency
/// through HTTP entity tags.
/// </summary>
public sealed class LayoutVersion
{
    public const int MaxBodyLength = 512_000;

    private readonly string _layoutKey;
    private readonly List<LayoutContent> _contents = [];
    private readonly List<string> _editors = [];

    private LayoutVersion(LayoutKey layoutKey, int version, string createdBy, DateTimeOffset createdAt)
    {
        _layoutKey = layoutKey.Value;
        Version = version;
        Status = LayoutVersionStatus.Draft;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
        ContentHash = CanonicalHash.OfLayoutVersion(_contents);
        EntityTag = NewEntityTag();
    }

    // EF Core materialization: fields are populated from the store.
    private LayoutVersion()
    {
        _layoutKey = null!;
        CreatedBy = null!;
        ContentHash = null!;
        EntityTag = null!;
    }

    public LayoutKey LayoutKey => LayoutKey.Trusted(_layoutKey);

    public int Version { get; }

    public LayoutVersionStatus Status { get; private set; }

    public string ContentHash { get; private set; }

    public string CreatedBy { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>Version this one was cloned from by a rollback, when applicable.</summary>
    public int? RolledBackFrom { get; private set; }

    public string EntityTag { get; private set; }

    public IReadOnlyList<LayoutContent> Contents => _contents;

    public IReadOnlyList<string> Editors => _editors;

    public static LayoutVersion CreateDraft(LayoutKey layoutKey, int version, string createdBy, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(layoutKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        return new LayoutVersion(layoutKey, version, createdBy, createdAt);
    }

    public static LayoutVersion CreateDraftFrom(LayoutVersion source, int version, string createdBy, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(source);
        LayoutVersion draft = CreateDraft(source.LayoutKey, version, createdBy, createdAt);
        foreach (LayoutContent content in source._contents)
        {
            draft._contents.Add(new LayoutContent(
                content.Channel,
                content.Locale,
                content.Body,
                content.BodyText));
        }

        draft.ContentHash = CanonicalHash.OfLayoutVersion(draft._contents);
        return draft;
    }

    /// <summary>
    /// Rebuilds a version from previously validated state. Persistence-shaped
    /// entry point for mapping layers and test arrangement; it bypasses lifecycle
    /// guards on purpose and must never receive user input.
    /// </summary>
    internal static LayoutVersion Rehydrate(LayoutVersionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var version = new LayoutVersion(
            LayoutKey.Trusted(state.LayoutKey),
            state.Version,
            state.CreatedBy,
            state.CreatedAt)
        {
            Status = LayoutVersionStatuses.Trusted(state.Status),
            PublishedAt = state.PublishedAt,
            RolledBackFrom = state.RolledBackFrom,
        };
        version._editors.AddRange(state.Editors);
        foreach (LayoutContentState content in state.Contents)
        {
            version._contents.Add(new LayoutContent(
                Channel.Trusted(content.Channel),
                Locale.Trusted(content.Locale),
                content.Body,
                content.BodyText));
        }

        // A caller-supplied hash mirrors the persisted column so integrity
        // verification can compare the stored value against the content.
        version.ContentHash = state.ContentHash ?? CanonicalHash.OfLayoutVersion(version._contents);
        return version;
    }

    /// <summary>
    /// Publishes this draft. The publisher must not have created or edited it:
    /// approval only means something when a second person grants it.
    /// </summary>
    public Result Publish(string publisherId, DateTimeOffset publishedAt)
    {
        Result eligibility = CanBePublishedBy(publisherId);
        if (eligibility.IsFailure)
        {
            return eligibility;
        }

        Status = LayoutVersionStatus.Published;
        PublishedAt = publishedAt;
        EntityTag = NewEntityTag();
        return Result.Success();
    }

    /// <summary>
    /// Publication guards without the state change, so orchestration can fail
    /// fast before running the validation catalog.
    /// </summary>
    public Result CanBePublishedBy(string publisherId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherId);

        if (Status != LayoutVersionStatus.Draft)
        {
            return Result.BusinessRuleViolation(DomainError.StateTransition(
                Status.Canonical(),
                LayoutVersionStatuses.AllowedTransitions(Status),
                $"Cannot publish version {Version}: it is '{Status.Canonical()}' and only a draft can be published."));
        }

        return WasAuthoredOrEditedBy(publisherId)
            ? FourEyesFailure("publication")
            : Result.Success();
    }

    /// <summary>Moves the previously published version aside when a newer one takes over.</summary>
    public Result Supersede()
    {
        if (Status != LayoutVersionStatus.Published)
        {
            return Result.BusinessRuleViolation(DomainError.StateTransition(
                Status.Canonical(),
                LayoutVersionStatuses.AllowedTransitions(Status),
                $"Cannot supersede version {Version}: it is '{Status.Canonical()}', not published."));
        }

        Status = LayoutVersionStatus.Superseded;
        EntityTag = NewEntityTag();
        return Result.Success();
    }

    /// <summary>
    /// Creates the next version as an exact clone of a previously published one
    /// and publishes it in the same act. Four eyes applies to the source: the
    /// caller must not have created or edited the content being republished.
    /// The republished content was already approved by a second person when it
    /// was originally published.
    /// </summary>
    public static Result<LayoutVersion> CreateRollback(
        LayoutVersion source,
        int version,
        string actor,
        DateTimeOffset publishedAt)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        if (source.Status == LayoutVersionStatus.Draft)
        {
            return Result.BusinessRuleViolation<LayoutVersion>(DomainError.StateTransition(
                source.Status.Canonical(),
                LayoutVersionStatuses.AllowedTransitions(source.Status),
                $"Version {source.Version} was never published and cannot be a rollback target."));
        }

        if (source.WasAuthoredOrEditedBy(actor))
        {
            Result forbidden = FourEyesFailure("rollback");
            return new Result<LayoutVersion>(false, null, forbidden.ErrorKind, forbidden.Error);
        }

        LayoutVersion clone = CreateDraftFrom(source, version, actor, publishedAt);
        clone.RolledBackFrom = source.Version;
        clone.Status = LayoutVersionStatus.Published;
        clone.PublishedAt = publishedAt;
        return Result.Success(clone);
    }

    /// <summary>
    /// Recomputes the canonical hash from the loaded content and compares it to
    /// the stored one. A mismatch means the persisted content no longer matches
    /// what its hash vouches for, and nothing may be approved on top of it.
    /// </summary>
    public Result VerifyContentHash()
        => string.Equals(
            ContentHash,
            CanonicalHash.OfLayoutVersion(_contents),
            StringComparison.Ordinal)
            ? Result.Success()
            : Result.BusinessRuleViolation(DomainError.Format(
                ErrorCodes.ContentHashMismatch,
                $"The stored content of version {Version} no longer matches its content hash."));

    public Result SetContent(LayoutContentEdit edit, string editor)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentException.ThrowIfNullOrWhiteSpace(editor);

        Result guard = EnsureDraft($"content for ({edit.Channel.Value}, {edit.Locale.Value})");
        if (guard.IsFailure)
        {
            return guard;
        }

        if (string.IsNullOrWhiteSpace(edit.Body) || edit.Body.Length > MaxBodyLength)
        {
            return ValidationFailure($"Content body is required and must have at most {MaxBodyLength} characters.");
        }

        if (edit.BodyText is { Length: > MaxBodyLength })
        {
            return ValidationFailure($"Content text body must have at most {MaxBodyLength} characters.");
        }

        LayoutContent? existing = _contents.FirstOrDefault(content =>
            content.Channel == edit.Channel && content.Locale == edit.Locale);
        if (existing is null)
        {
            _contents.Add(new LayoutContent(edit.Channel, edit.Locale, edit.Body, edit.BodyText));
        }
        else
        {
            existing.Update(edit.Body, edit.BodyText);
        }

        RegisterEdit(editor);
        return Result.Success();
    }

    private bool WasAuthoredOrEditedBy(string actorId)
        => string.Equals(actorId, CreatedBy, StringComparison.Ordinal)
            || _editors.Contains(actorId, StringComparer.Ordinal);

    private static Result FourEyesFailure(string operation)
        => new(false, ResultErrorKind.Forbidden, DomainError.Format(
            ErrorCodes.FourEyesViolation,
            $"This principal created or edited the version content; {operation} requires a different person."));

    private Result EnsureDraft(string editTarget)
        => Status == LayoutVersionStatus.Draft
            ? Result.Success()
            : Result.BusinessRuleViolation(DomainError.StateTransition(
                Status.Canonical(),
                LayoutVersionStatuses.AllowedTransitions(Status),
                $"Cannot edit {editTarget}: version {Version} is '{Status.Canonical()}' and only a draft accepts edits."));

    private void RegisterEdit(string editor)
    {
        if (!_editors.Contains(editor, StringComparer.Ordinal))
        {
            _editors.Add(editor);
        }

        ContentHash = CanonicalHash.OfLayoutVersion(_contents);
        EntityTag = NewEntityTag();
    }

    private static Result ValidationFailure(string detail)
        => new(false, ResultErrorKind.Validation, DomainError.Format(ErrorCodes.InvalidRequest, detail));

    private static string NewEntityTag() => Guid.NewGuid().ToString("N");
}

/// <summary>One (channel, locale) content edit applied to a layout draft.</summary>
public sealed record LayoutContentEdit(Channel Channel, Locale Locale, string Body, string? BodyText);

/// <summary>Previously validated state used by <see cref="LayoutVersion.Rehydrate"/>.</summary>
internal sealed record LayoutVersionState
{
    public required string LayoutKey { get; init; }

    public required int Version { get; init; }

    public required string Status { get; init; }

    public required string CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public int? RolledBackFrom { get; init; }

    /// <summary>Persisted hash; when absent it is recomputed from the content.</summary>
    public string? ContentHash { get; init; }

    public IReadOnlyList<string> Editors { get; init; } = [];

    public IReadOnlyList<LayoutContentState> Contents { get; init; } = [];
}

/// <summary>Previously validated content state used by <see cref="LayoutVersion.Rehydrate"/>.</summary>
internal sealed record LayoutContentState(
    string Channel,
    string Locale,
    string Body,
    string? BodyText);
