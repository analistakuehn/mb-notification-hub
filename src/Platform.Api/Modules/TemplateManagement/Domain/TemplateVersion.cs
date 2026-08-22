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
        ContentHash = CanonicalHash.OfVersion(null, _contents);
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

    public string ContentHash { get; private set; }

    public string CreatedBy { get; }

    public DateTimeOffset CreatedAt { get; }

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
        var draft = CreateDraft(source.TemplateKey, version, createdBy, createdAt);
        draft.VariablesSchemaJson = source.VariablesSchemaJson;
        foreach (TemplateContent content in source._contents)
        {
            draft._contents.Add(new TemplateContent(
                content.Channel,
                content.Locale,
                content.Subject,
                content.Body,
                content.BodyText));
        }

        draft.ContentHash = CanonicalHash.OfVersion(draft.VariablesSchemaJson, draft._contents);
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

        version.ContentHash = CanonicalHash.OfVersion(version.VariablesSchemaJson, version._contents);
        return version;
    }

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

        ContentHash = CanonicalHash.OfVersion(VariablesSchemaJson, _contents);
        EntityTag = NewEntityTag();
    }

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

    public required string CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

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
