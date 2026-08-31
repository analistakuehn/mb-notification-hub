using System.Text.RegularExpressions;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Unit of publication of a template. Only a draft accepts edits; a published
/// version is immutable forever. The aggregate records the draft author and every
/// editor because publication must be performed by someone who touched neither.
/// <see cref="ContentHash"/> covers the canonical content of the whole version and
/// is refreshed on every edit; <see cref="EntityTag"/> backs optimistic concurrency
/// through HTTP entity tags.
/// <para>
/// The names that carry sensitive data are declared here rather than on the
/// identity, because the declaration decides what the render masks and what the
/// trail stores in clear. Living on the version puts it inside the hash the
/// approval covers, so a second person approves the exact list, and it can be
/// corrected by the same act that corrects everything else: a new draft,
/// edited, published by somebody who did not write it.
/// </para>
/// </summary>
public sealed partial class TemplateVersion
{
    /// <summary>
    /// Characters a content subject may carry. The number comes from the mail
    /// header line limit and from the column that stores it, and never from
    /// what a subject costs to parse, so it is a ceiling of its own and not a
    /// share of the source ceiling.
    /// <para>
    /// A subject is still source the engine analyzes, so the two are tied by
    /// one invariant, asserted by a test: a subject this constant admits has
    /// to fit inside the source ceiling, or a configuration between the two
    /// would accept a subject on the write and refuse it on the analysis.
    /// </para>
    /// </summary>
    public const int MaxSubjectLength = 998;

    /// <summary>
    /// Characters a variables schema may carry. It is deliberately outside the
    /// source ceiling: a schema describes the variables, is read by the schema
    /// validator, and never reaches the template engine, so nothing about the
    /// cost of parsing a template says anything about it.
    /// </summary>
    public const int MaxSchemaLength = 64_000;

    /// <summary>Names a version may declare as carrying sensitive data.</summary>
    public const int MaxSensitiveVariables = 100;

    public const int MaxVariableNameLength = 100;

    private readonly string _templateKey;
    private readonly List<TemplateContent> _contents = [];
    private readonly List<string> _editors = [];
    private readonly List<string> _sensitiveVariables = [];

    private TemplateVersion(TemplateKey templateKey, int version, string createdBy, DateTimeOffset createdAt)
    {
        _templateKey = templateKey.Value;
        Version = version;
        Status = TemplateVersionStatus.Draft;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
        ContentHash = CanonicalHash.OfVersion(null, null, null, _sensitiveVariables, _contents);
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

    /// <summary>Variable names whose values carry sensitive data and must never sit in a URL position.</summary>
    public IReadOnlyList<string> SensitiveVariables => _sensitiveVariables;

    public static TemplateVersion CreateDraft(TemplateKey templateKey, int version, string createdBy, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(templateKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        return new TemplateVersion(templateKey, version, createdBy, createdAt);
    }

    /// <summary>
    /// Clones a version into a new draft. Cloning hashes the clone, and hashing
    /// reads the source schema, so a source whose stored schema no longer
    /// transcodes is refused here with an answer: the clone would otherwise
    /// carry a hash nobody could recompute.
    /// </summary>
    public static Result<TemplateVersion> CreateDraftFrom(
        TemplateVersion source,
        int version,
        string createdBy,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.TryReadStoredSchema(out var canonicalSchema))
        {
            Result unreadable = StoredContentUnreadable(source.Version);
            return new Result<TemplateVersion>(false, null, unreadable.ErrorKind, unreadable.Error);
        }

        TemplateVersion draft = CreateDraft(source.TemplateKey, version, createdBy, createdAt);
        draft.VariablesSchemaJson = source.VariablesSchemaJson;
        draft.LayoutKey = source.LayoutKey;
        draft.LayoutVersion = source.LayoutVersion;
        draft._sensitiveVariables.AddRange(source._sensitiveVariables);
        foreach (TemplateContent content in source._contents)
        {
            draft._contents.Add(new TemplateContent(
                content.Channel,
                content.Locale,
                content.Subject,
                content.Body,
                content.BodyText));
        }

        draft.ContentHash = CanonicalHash.OfVersion(
            canonicalSchema,
            draft.LayoutKey,
            draft.LayoutVersion,
            draft._sensitiveVariables,
            draft._contents);
        return Result.Success(draft);
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
        version._sensitiveVariables.AddRange(state.SensitiveVariables);
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
        if (state.ContentHash is not null)
        {
            version.ContentHash = state.ContentHash;
            return version;
        }

        // Deriving the hash reads the schema, and this entry point takes
        // previously validated state only, so a schema that does not transcode
        // here is a caller that broke that contract rather than a document with
        // a property. That is the unexpected system failure an exception exists
        // for, and handing this one a refusal to return would invite a caller
        // to route user input through the one door that skips the guards.
        if (!version.TryReadStoredSchema(out var canonicalSchema))
        {
            throw new InvalidOperationException(
                "Rehydrate received a variables schema that does not transcode. "
                + "This entry point takes previously validated state and never user input.");
        }

        version.ContentHash = CanonicalHash.OfVersion(
            canonicalSchema,
            version.LayoutKey,
            version.LayoutVersion,
            version._sensitiveVariables,
            version._contents);
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

        Result<TemplateVersion> clone = CreateDraftFrom(source, version, actor, publishedAt);
        if (clone.IsFailure)
        {
            return clone;
        }

        TemplateVersion published = clone.Value!;
        published.RolledBackFrom = source.Version;
        published.Status = TemplateVersionStatus.Published;
        published.PublishedAt = publishedAt;
        return Result.Success(published);
    }

    /// <summary>
    /// Recomputes the canonical hash from the loaded content and compares it to
    /// the stored one. A mismatch means the persisted content no longer matches
    /// what its hash vouches for, and nothing may be approved on top of it.
    /// </summary>
    public Result VerifyContentHash()
    {
        // A row that cannot be read did not diverge from its hash: nothing can
        // recompute one over it. Reporting a mismatch would accuse the stored
        // bytes of a change nobody made, and send the reader looking for one.
        if (!TryReadStoredSchema(out var canonicalSchema))
        {
            return StoredContentUnreadable(Version);
        }

        return string.Equals(
            ContentHash,
            CanonicalHash.OfVersion(canonicalSchema, LayoutKey, LayoutVersion, _sensitiveVariables, _contents),
            StringComparison.Ordinal)
            ? Result.Success()
            : Result.BusinessRuleViolation(DomainError.Format(
                ErrorCodes.ContentHashMismatch,
                $"The stored content of version {Version} no longer matches its content hash."));
    }

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

        if (string.IsNullOrWhiteSpace(edit.Body) || edit.Body.Length > TemplateSourceSize.MaxChars)
        {
            return ValidationFailure($"Content body is required and must have at most {TemplateSourceSize.MaxChars} characters.");
        }

        if (edit.Subject is { Length: > MaxSubjectLength })
        {
            return ValidationFailure($"Content subject must have at most {MaxSubjectLength} characters.");
        }

        if (edit.BodyText is { Length: > TemplateSourceSize.MaxChars })
        {
            return ValidationFailure($"Content text body must have at most {TemplateSourceSize.MaxChars} characters.");
        }

        // Read before mutating: an edit applied and then unable to rehash would
        // leave the version holding a hash that vouches for content it no
        // longer has.
        if (!TryReadStoredSchema(out var canonicalSchema))
        {
            return StoredContentUnreadable(Version);
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

        RegisterEdit(editor, canonicalSchema);
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

        // One traversal answers all three: whether it is JSON, whether anything
        // can read it, and whether it is an object. The shape rule lives here
        // and not only at the transport, because a schema that is legal JSON
        // and not an object declares no variables at all, and a version that
        // declares none passes every undeclared-name check by saying nothing.
        CanonicalJsonForm form = CanonicalJson.TryNormalize(schemaJson);
        switch (form.Verdict)
        {
            case CanonicalJsonVerdict.Malformed:
                return ValidationFailure("The variables schema must be well-formed JSON.");
            case CanonicalJsonVerdict.Unreadable:
                return new Result(false, ResultErrorKind.Validation, DomainError.Format(
                    ErrorCodes.VariablesSchemaUnreadable,
                    "The variables schema must be JSON text that can be read: "
                    + "an escape in it names no character."));
            case CanonicalJsonVerdict.NotAnObject:
                return ValidationFailure("The variables schema must be a JSON object.");
            default:
                break;
        }

        VariablesSchemaJson = schemaJson;
        RegisterEdit(editor, form.Text);
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

        if (!TryReadStoredSchema(out var canonicalSchema))
        {
            return StoredContentUnreadable(Version);
        }

        LayoutKey = layoutKey?.Value;
        LayoutVersion = layoutVersion;
        RegisterEdit(editor, canonicalSchema);
        return Result.Success();
    }

    /// <summary>
    /// Declares which variables of this version carry sensitive data. It is an
    /// edit like any other: only a draft accepts it, the editor is recorded,
    /// and the hash is refreshed, so the declaration reaches publication under
    /// the same four eyes and the same approval as the content it protects.
    /// <para>
    /// Whether every declared name resolves through the variables schema is
    /// the job of the sensitive-variable validation check, not of this edit:
    /// an author may declare the names and write the schema in either order
    /// inside the same draft.
    /// </para>
    /// </summary>
    public Result SetSensitiveVariables(IReadOnlyList<string> variables, string editor)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentException.ThrowIfNullOrWhiteSpace(editor);

        Result guard = EnsureDraft("the sensitive variables");
        if (guard.IsFailure)
        {
            return guard;
        }

        Result<List<string>> normalized = NormalizeSensitiveVariables(variables);
        if (normalized.IsFailure)
        {
            return new Result(false, normalized.ErrorKind, normalized.Error);
        }

        // Read before mutating: an edit applied and then unable to rehash
        // would leave the version holding a hash that vouches for a
        // declaration it no longer has.
        if (!TryReadStoredSchema(out var canonicalSchema))
        {
            return StoredContentUnreadable(Version);
        }

        _sensitiveVariables.Clear();
        _sensitiveVariables.AddRange(normalized.Value!);
        RegisterEdit(editor, canonicalSchema);
        return Result.Success();
    }

    private static Result<List<string>> NormalizeSensitiveVariables(IReadOnlyList<string> variables)
    {
        if (variables.Count > MaxSensitiveVariables)
        {
            return Result.ValidationError<List<string>>(DomainError.Format(
                ErrorCodes.InvalidRequest,
                $"At most {MaxSensitiveVariables} sensitive variables are allowed."));
        }

        List<string> normalized = [];
        foreach (var variable in variables)
        {
            var candidate = variable?.Trim() ?? string.Empty;
            if (candidate.Length == 0
                || candidate.Length > MaxVariableNameLength
                || !VariableNamePattern().IsMatch(candidate))
            {
                return Result.ValidationError<List<string>>(DomainError.Format(
                    ErrorCodes.InvalidRequest,
                    "Each sensitive variable must be a template variable path: dot-separated "
                    + "segments of letters, digits and underscores, none starting with a digit."));
            }

            if (!normalized.Contains(candidate, StringComparer.Ordinal))
            {
                normalized.Add(candidate);
            }
        }

        return Result.Success(normalized);
    }

    // A sensitive name addresses either a variable at any depth (one segment)
    // or an absolute path from the payload root (several). Every segment stays
    // ASCII, where ordinal comparison and Unicode normalization coincide, so
    // the publication check and the mask can never read the same name apart.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex VariableNamePattern();

    private Result EnsureDraft(string editTarget)
        => Status == TemplateVersionStatus.Draft
            ? Result.Success()
            : Result.BusinessRuleViolation(DomainError.StateTransition(
                Status.Canonical(),
                TemplateVersionStatuses.AllowedTransitions(Status),
                $"Cannot edit {editTarget}: version {Version} is '{Status.Canonical()}' and only a draft accepts edits."));

    /// <summary>
    /// Records the editor and refreshes hash and entity tag. It takes the
    /// canonical schema the caller already read rather than reading it again:
    /// the read is the step that can refuse, every caller has done it before
    /// mutating, and doing it twice would canonicalize the same document twice
    /// per edit.
    /// </summary>
    private void RegisterEdit(string editor, string? canonicalSchema)
    {
        if (!_editors.Contains(editor, StringComparer.Ordinal))
        {
            _editors.Add(editor);
        }

        ContentHash = CanonicalHash.OfVersion(
            canonicalSchema, LayoutKey, LayoutVersion, _sensitiveVariables, _contents);
        EntityTag = NewEntityTag();
    }

    /// <summary>
    /// The canonical form of the stored variables schema, or false when the
    /// stored document can no longer be read at all. A schema that is legal
    /// JSON and not an object still reads: the shape rule belongs to the door
    /// that accepts a schema, and applying it here would turn a row written
    /// before that door existed into a version nobody can verify.
    /// </summary>
    private bool TryReadStoredSchema(out string? canonicalSchema)
    {
        canonicalSchema = null;
        if (VariablesSchemaJson is null)
        {
            return true;
        }

        CanonicalJsonForm form = CanonicalJson.TryNormalize(VariablesSchemaJson);
        canonicalSchema = form.Text;
        return form.Text is not null;
    }

    private static Result StoredContentUnreadable(int version)
        => Result.BusinessRuleViolation(DomainError.Format(
            ErrorCodes.StoredContentUnreadable,
            $"The stored variables schema of version {version} cannot be read: "
            + "an escape in it names no character."));

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

    public IReadOnlyList<string> SensitiveVariables { get; init; } = [];

    public IReadOnlyList<TemplateContentState> Contents { get; init; } = [];
}

/// <summary>Previously validated content state used by <see cref="TemplateVersion.Rehydrate"/>.</summary>
internal sealed record TemplateContentState(
    string Channel,
    string Locale,
    string? Subject,
    string Body,
    string? BodyText);
