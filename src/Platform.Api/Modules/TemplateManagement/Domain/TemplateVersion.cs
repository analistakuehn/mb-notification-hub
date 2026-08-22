using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Unit of publication of a template. Only a draft accepts edits; a published
/// version is immutable forever. The aggregate records the draft author and every
/// editor because publication must be performed by someone who touched neither.
/// <see cref="ContentHash"/> covers the canonical content of the whole version and
/// is refreshed on every edit; <see cref="EntityTag"/> backs optimistic concurrency
/// through HTTP entity tags.
/// </summary>
public sealed class TemplateVersion
{
    public const int MaxSubjectLength = 998;
    public const int MaxBodyLength = 512_000;
    public const int MaxSchemaLength = 64_000;

    private readonly string _templateKey;
    private readonly List<TemplateContent> _contents = [];
    private readonly List<string> _editors = [];

    private TemplateVersion(TemplateKey templateKey, int version, string createdBy, DateTimeOffset createdAt)
    {
        _templateKey = templateKey.Value;
        Version = version;
        Status = TemplateVersionStatus.Draft;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
        ContentHash = CanonicalHash.OfVersion(null, null, null, _contents);
        EntityTag = NewEntityTag();
    }

    // EF Core materialization: fields are populated from the store.
    private TemplateVersion()
    {
        _templateKey = null!;
        CreatedBy = null!;
        ContentHash = null!;
        EntityTag = null!;
    }

    public TemplateKey TemplateKey => TemplateKey.Trusted(_templateKey);

    public int Version { get; }

    public TemplateVersionStatus Status { get; private set; }

    public string? VariablesSchemaJson { get; private set; }

    /// <summary>Key of the layout this version pins for reproducible rendering, when any.</summary>
    public string? LayoutKey { get; private set; }

    /// <summary>Layout version pinned together with <see cref="LayoutKey"/>.</summary>
    public int? LayoutVersion { get; private set; }

    public string ContentHash { get; private set; }

    public string CreatedBy { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>Version this one was cloned from by a rollback, when applicable.</summary>
    public int? RolledBackFrom { get; private set; }

    public string EntityTag { get; private set; }

    public IReadOnlyList<TemplateContent> Contents => _contents;

    public IReadOnlyList<string> Editors => _editors;

    public static TemplateVersion CreateDraft(TemplateKey templateKey, int version, string createdBy, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(templateKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        return new TemplateVersion(templateKey, version, createdBy, createdAt);
    }

    public static TemplateVersion CreateDraftFrom(TemplateVersion source, int version, string createdBy, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(source);
        TemplateVersion draft = CreateDraft(source.TemplateKey, version, createdBy, createdAt);
        draft.VariablesSchemaJson = source.VariablesSchemaJson;
        draft.LayoutKey = source.LayoutKey;
        draft.LayoutVersion = source.LayoutVersion;
        foreach (TemplateContent content in source._contents)
        {
            draft._contents.Add(new TemplateContent(
                content.Channel,
                content.Locale,
                content.Subject,
                content.Body,
                content.BodyText));
        }

        draft.ContentHash = draft.ComputeContentHash();
        return draft;
    }

    /// <summary>
    /// Rebuilds a version from previously validated state. Persistence-shaped
    /// entry point for mapping layers and test arrangement; it bypasses lifecycle
    /// guards on purpose and must never receive user input.
    /// </summary>
    internal static TemplateVersion Rehydrate(TemplateVersionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var version = new TemplateVersion(
            TemplateKey.Trusted(state.TemplateKey),
            state.Version,
            state.CreatedBy,
            state.CreatedAt)
        {
            Status = TemplateVersionStatuses.Trusted(state.Status),
            VariablesSchemaJson = state.VariablesSchemaJson,
            LayoutKey = state.LayoutKey,
            LayoutVersion = state.LayoutVersion,
            PublishedAt = state.PublishedAt,
            RolledBackFrom = state.RolledBackFrom,
        };
        version._editors.AddRange(state.Editors);
        foreach (TemplateContentState content in state.Contents)
        {
            version._contents.Add(new TemplateContent(
                Channel.Trusted(content.Channel),
                Locale.Trusted(content.Locale),
                content.Subject,
                content.Body,
                content.BodyText));
        }

        // A caller-supplied hash mirrors the persisted column so integrity
        // verification can compare the stored value against the content.
        version.ContentHash = state.ContentHash ?? version.ComputeContentHash();
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

        Status = TemplateVersionStatus.Published;
        PublishedAt = publishedAt;
        EntityTag = NewEntityTag();
        return Result.Success();
    }

    /// <summary>
    /// Publication guards without the state change, so orchestration can fail
    /// fast before running the full validation catalog.
    /// </summary>
    public Result CanBePublishedBy(string publisherId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherId);

        if (Status != TemplateVersionStatus.Draft)
        {
            return Result.BusinessRuleViolation(DomainError.StateTransition(
                Status.Canonical(),
                TemplateVersionStatuses.AllowedTransitions(Status),
                $"Cannot publish version {Version}: it is '{Status.Canonical()}' and only a draft can be published."));
        }

        return WasAuthoredOrEditedBy(publisherId)
            ? FourEyesFailure("publication")
            : Result.Success();
    }

    /// <summary>Moves the previously published version aside when a newer one takes over.</summary>
    public Result Supersede()
    {
        if (Status != TemplateVersionStatus.Published)
        {
            return Result.BusinessRuleViolation(DomainError.StateTransition(
                Status.Canonical(),
                TemplateVersionStatuses.AllowedTransitions(Status),
                $"Cannot supersede version {Version}: it is '{Status.Canonical()}', not published."));
        }

        Status = TemplateVersionStatus.Superseded;
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
    public static Result<TemplateVersion> CreateRollback(
        TemplateVersion source,
        int version,
        string actor,
        DateTimeOffset publishedAt)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        if (source.Status == TemplateVersionStatus.Draft)
        {
            return Result.BusinessRuleViolation<TemplateVersion>(DomainError.StateTransition(
                source.Status.Canonical(),
                TemplateVersionStatuses.AllowedTransitions(source.Status),
                $"Version {source.Version} was never published and cannot be a rollback target."));
        }

        if (source.WasAuthoredOrEditedBy(actor))
        {
            Result forbidden = FourEyesFailure("rollback");
            return new Result<TemplateVersion>(false, null, forbidden.ErrorKind, forbidden.Error);
        }

        TemplateVersion clone = CreateDraftFrom(source, version, actor, publishedAt);
        clone.RolledBackFrom = source.Version;
        clone.Status = TemplateVersionStatus.Published;
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
            ComputeContentHash(),
            StringComparison.Ordinal)
            ? Result.Success()
            : Result.BusinessRuleViolation(DomainError.Format(
                ErrorCodes.ContentHashMismatch,
                $"The stored content of version {Version} no longer matches its content hash."));

    private bool WasAuthoredOrEditedBy(string actorId)
        => string.Equals(actorId, CreatedBy, StringComparison.Ordinal)
            || _editors.Contains(actorId, StringComparer.Ordinal);

    private static Result FourEyesFailure(string operation)
        => new(false, ResultErrorKind.Forbidden, DomainError.Format(
            ErrorCodes.FourEyesViolation,
            $"This principal created or edited the version content; {operation} requires a different person."));

    public Result SetContent(ContentEdit edit, string editor)
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

        if (edit.Subject is { Length: > MaxSubjectLength })
        {
            return ValidationFailure($"Content subject must have at most {MaxSubjectLength} characters.");
        }

        if (edit.BodyText is { Length: > MaxBodyLength })
        {
            return ValidationFailure($"Content text body must have at most {MaxBodyLength} characters.");
        }

        TemplateContent? existing = _contents.FirstOrDefault(content =>
            content.Channel == edit.Channel && content.Locale == edit.Locale);
        if (existing is null)
        {
            _contents.Add(new TemplateContent(edit.Channel, edit.Locale, edit.Subject, edit.Body, edit.BodyText));
        }
        else
        {
            existing.Update(edit.Subject, edit.Body, edit.BodyText);
        }

        RegisterEdit(editor);
        return Result.Success();
    }

    public Result SetVariablesSchema(string schemaJson, string editor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editor);

        Result guard = EnsureDraft("the variables schema");
        if (guard.IsFailure)
        {
            return guard;
        }

        if (string.IsNullOrWhiteSpace(schemaJson) || schemaJson.Length > MaxSchemaLength)
        {
            return ValidationFailure($"Variables schema is required and must have at most {MaxSchemaLength} characters.");
        }

        VariablesSchemaJson = schemaJson;
        RegisterEdit(editor);
        return Result.Success();
    }

    /// <summary>
    /// Pins (or clears) the layout this draft renders inside. The pin names an
    /// exact layout version so the render stays reproducible after publication;
    /// whether that version exists and is published is the job of the
    /// layout-reference validation check, not of this edit.
    /// </summary>
    public Result SetLayoutReference(LayoutKey? layoutKey, int? layoutVersion, string editor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editor);

        Result guard = EnsureDraft("the layout reference");
        if (guard.IsFailure)
        {
            return guard;
        }

        if (layoutKey is null != layoutVersion is null)
        {
            return ValidationFailure("A layout reference requires both layoutKey and layoutVersion, or neither.");
        }

        if (layoutVersion is < 1)
        {
            return ValidationFailure("layoutVersion must be a positive version number.");
        }

        LayoutKey = layoutKey?.Value;
        LayoutVersion = layoutVersion;
        RegisterEdit(editor);
        return Result.Success();
    }

    private Result EnsureDraft(string editTarget)
        => Status == TemplateVersionStatus.Draft
            ? Result.Success()
            : Result.BusinessRuleViolation(DomainError.StateTransition(
                Status.Canonical(),
                TemplateVersionStatuses.AllowedTransitions(Status),
                $"Cannot edit {editTarget}: version {Version} is '{Status.Canonical()}' and only a draft accepts edits."));

    private void RegisterEdit(string editor)
    {
        if (!_editors.Contains(editor, StringComparer.Ordinal))
        {
            _editors.Add(editor);
        }

        ContentHash = ComputeContentHash();
        EntityTag = NewEntityTag();
    }

    private string ComputeContentHash()
        => CanonicalHash.OfVersion(VariablesSchemaJson, LayoutKey, LayoutVersion, _contents);

    private static Result ValidationFailure(string detail)
        => new(false, ResultErrorKind.Validation, DomainError.Format(ErrorCodes.InvalidRequest, detail));

    private static string NewEntityTag() => Guid.NewGuid().ToString("N");
}

/// <summary>One (channel, locale) content edit applied to a draft.</summary>
public sealed record ContentEdit(Channel Channel, Locale Locale, string? Subject, string Body, string? BodyText);

/// <summary>Previously validated state used by <see cref="TemplateVersion.Rehydrate"/>.</summary>
internal sealed record TemplateVersionState
{
    public required string TemplateKey { get; init; }

    public required int Version { get; init; }

    public required string Status { get; init; }

    public string? VariablesSchemaJson { get; init; }

    public string? LayoutKey { get; init; }

    public int? LayoutVersion { get; init; }

    public required string CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public int? RolledBackFrom { get; init; }

    /// <summary>Persisted hash; when absent it is recomputed from the content.</summary>
    public string? ContentHash { get; init; }

    public IReadOnlyList<string> Editors { get; init; } = [];

    public IReadOnlyList<TemplateContentState> Contents { get; init; } = [];
}

/// <summary>Previously validated content state used by <see cref="TemplateVersion.Rehydrate"/>.</summary>
internal sealed record TemplateContentState(
    string Channel,
    string Locale,
    string? Subject,
    string Body,
    string? BodyText);
