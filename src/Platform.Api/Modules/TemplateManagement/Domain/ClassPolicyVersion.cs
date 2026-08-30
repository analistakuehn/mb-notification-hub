using System.Text.Json;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Unit of publication of the class policy governed per (application, class).
/// Only a draft accepts edits; a published version is immutable forever and is
/// what the Policy stage loads at runtime. The aggregate records the draft
/// author and every editor because publication must be performed by someone who
/// touched neither. <see cref="ContentHash"/> covers the canonical form of the
/// definition and is refreshed on every edit; <see cref="EntityTag"/> backs
/// optimistic concurrency through HTTP entity tags.
/// </summary>
public sealed class ClassPolicyVersion
{
    public const int MaxDefinitionLength = 64_000;

    private readonly List<string> _editors = [];

    private ClassPolicyVersion(
        string application,
        NotificationClass notificationClass,
        int version,
        string createdBy,
        DateTimeOffset createdAt)
    {
        Application = application;
        Class = notificationClass;
        Version = version;
        Status = ClassPolicyVersionStatus.Draft;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
        DefinitionJson = null!;
        ContentHash = null!;
        EntityTag = null!;
    }

    // EF Core materialization: fields are populated from the store.
    private ClassPolicyVersion()
    {
        Application = null!;
        CreatedBy = null!;
        DefinitionJson = null!;
        ContentHash = null!;
        EntityTag = null!;
    }

    public string Application { get; }

    public NotificationClass Class { get; }

    public int Version { get; }

    public ClassPolicyVersionStatus Status { get; private set; }

    /// <summary>Vocabulary version declared by the stored definition.</summary>
    public int SchemaVersion { get; private set; }

    /// <summary>The definition document exactly as the author submitted it.</summary>
    public string DefinitionJson { get; private set; }

    public string ContentHash { get; private set; }

    public string CreatedBy { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public string EntityTag { get; private set; }

    public IReadOnlyList<string> Editors => _editors;

    public static Result<ClassPolicyVersion> CreateDraft(ClassPolicyDraftInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Application);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.CreatedBy);
        ArgumentOutOfRangeException.ThrowIfLessThan(input.Version, 1);

        var draft = new ClassPolicyVersion(
            input.Application,
            input.Class,
            input.Version,
            input.CreatedBy,
            input.CreatedAt);
        Result applied = draft.ApplyDefinition(input.DefinitionJson);
        return applied.IsFailure
            ? new Result<ClassPolicyVersion>(false, null, applied.ErrorKind, applied.Error)
            : Result.Success(draft);
    }

    /// <summary>
    /// Rebuilds a version from previously validated state. Persistence-shaped
    /// entry point for mapping layers and test arrangement; it bypasses
    /// lifecycle guards on purpose and must never receive user input.
    /// </summary>
    internal static ClassPolicyVersion Rehydrate(ClassPolicyVersionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var version = new ClassPolicyVersion(
            state.Application,
            state.Class,
            state.Version,
            state.CreatedBy,
            state.CreatedAt)
        {
            Status = ClassPolicyVersionStatuses.Trusted(state.Status),
            SchemaVersion = state.SchemaVersion,
            DefinitionJson = state.DefinitionJson,
            PublishedAt = state.PublishedAt,
            EntityTag = NewEntityTag(),
        };
        version._editors.AddRange(state.Editors);

        // A caller-supplied hash mirrors the persisted column so integrity
        // verification can compare the stored value against the definition.
        if (state.ContentHash is not null)
        {
            version.ContentHash = state.ContentHash;
            return version;
        }

        // Deriving the hash reads the definition, and this entry point takes
        // previously validated state only, so a definition that does not
        // transcode here is a caller that broke that contract rather than a
        // document with a property. That is the unexpected system failure an
        // exception exists for, and handing this one a refusal to return would
        // invite a caller to route user input through the one door that skips
        // the guards.
        if (CanonicalJson.TryNormalize(state.DefinitionJson).Text is not { } canonical)
        {
            throw new InvalidOperationException(
                "Rehydrate received a policy definition that does not transcode. "
                + "This entry point takes previously validated state and never user input.");
        }

        version.ContentHash = HashOf(canonical);
        return version;
    }

    public Result SetDefinition(string definitionJson, string editor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editor);

        if (Status != ClassPolicyVersionStatus.Draft)
        {
            return Result.BusinessRuleViolation(DomainError.StateTransition(
                Status.Canonical(),
                ClassPolicyVersionStatuses.AllowedTransitions(Status),
                $"Cannot edit the definition: version {Version} is '{Status.Canonical()}' "
                + "and only a draft accepts edits."));
        }

        Result applied = ApplyDefinition(definitionJson);
        if (applied.IsFailure)
        {
            return applied;
        }

        if (!_editors.Contains(editor, StringComparer.Ordinal))
        {
            _editors.Add(editor);
        }

        return Result.Success();
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

        Status = ClassPolicyVersionStatus.Published;
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

        if (Status != ClassPolicyVersionStatus.Draft)
        {
            return Result.BusinessRuleViolation(DomainError.StateTransition(
                Status.Canonical(),
                ClassPolicyVersionStatuses.AllowedTransitions(Status),
                $"Cannot publish version {Version}: it is '{Status.Canonical()}' and only a draft can be published."));
        }

        var involved = string.Equals(publisherId, CreatedBy, StringComparison.Ordinal)
            || _editors.Contains(publisherId, StringComparer.Ordinal);
        return involved
            ? new Result(false, ResultErrorKind.Forbidden, DomainError.Format(
                ErrorCodes.FourEyesViolation,
                "This principal created or edited the policy draft; publication requires a different person."))
            : Result.Success();
    }

    /// <summary>Moves the previously published version aside when a newer one takes over.</summary>
    public Result Supersede()
    {
        if (Status != ClassPolicyVersionStatus.Published)
        {
            return Result.BusinessRuleViolation(DomainError.StateTransition(
                Status.Canonical(),
                ClassPolicyVersionStatuses.AllowedTransitions(Status),
                $"Cannot supersede version {Version}: it is '{Status.Canonical()}', not published."));
        }

        Status = ClassPolicyVersionStatus.Superseded;
        EntityTag = NewEntityTag();
        return Result.Success();
    }

    /// <summary>
    /// Recomputes the canonical hash from the stored definition and compares it
    /// to the persisted one. A mismatch means the definition no longer matches
    /// what its hash vouches for, and nothing may be approved on top of it.
    /// </summary>
    public Result VerifyContentHash()
    {
        // A row that cannot be read did not diverge from its hash: nothing can
        // recompute one over it. Reporting a mismatch would accuse the stored
        // bytes of a change nobody made, and send the reader looking for one.
        if (CanonicalJson.TryNormalize(DefinitionJson).Text is not { } canonical)
        {
            return Result.BusinessRuleViolation(DomainError.Format(
                ErrorCodes.StoredContentUnreadable,
                $"The stored definition of version {Version} cannot be read: "
                + "an escape in it names no character."));
        }

        return string.Equals(ContentHash, HashOf(canonical), StringComparison.Ordinal)
            ? Result.Success()
            : Result.BusinessRuleViolation(DomainError.Format(
                ErrorCodes.ContentHashMismatch,
                $"The stored definition of version {Version} no longer matches its content hash."));
    }

    private Result ApplyDefinition(string definitionJson)
    {
        if (string.IsNullOrWhiteSpace(definitionJson) || definitionJson.Length > MaxDefinitionLength)
        {
            return ValidationFailure(
                $"The definition is required and must have at most {MaxDefinitionLength} characters.");
        }

        // One traversal answers all three: whether it is JSON, whether anything
        // can read it, and whether it is an object. It runs before a single
        // property is looked up, because looking one up unescapes candidate
        // keys to compare them and an escape that names no character takes the
        // lookup down with it.
        CanonicalJsonForm form = CanonicalJson.TryNormalize(definitionJson);
        if (form is not { Verdict: CanonicalJsonVerdict.Canonical, Text: { } canonical })
        {
            return ValidationFailure(form.Verdict switch
            {
                CanonicalJsonVerdict.Malformed => "The definition must be well-formed JSON.",
                CanonicalJsonVerdict.Unreadable =>
                    "The definition must be JSON text that can be read: an escape in it names no character.",
                _ => "The definition must be a JSON object declaring an integer 'schemaVersion'.",
            });
        }

        // The parse cannot fail here: the traversal above already read the same
        // text, so no catch stands between this walk and a defect in it.
        using var document = JsonDocument.Parse(definitionJson);
        if (!document.RootElement.TryGetProperty("schemaVersion", out JsonElement declared)
            || declared.ValueKind != JsonValueKind.Number
            || !declared.TryGetInt32(out var schemaVersion))
        {
            return ValidationFailure("The definition must be a JSON object declaring an integer 'schemaVersion'.");
        }

        DefinitionJson = definitionJson;
        SchemaVersion = schemaVersion;
        ContentHash = HashOf(canonical);
        EntityTag = NewEntityTag();
        return Result.Success();
    }

    /// <summary>
    /// The hash covers the canonical JSON form, so formatting and key order
    /// never change what the approval vouches for. The caller produces that
    /// form, because producing it is the step that can refuse the document and
    /// a hash that could fail would put the refusal in the one place with no
    /// way to report it.
    /// </summary>
    private static string HashOf(string canonicalDefinition)
        => CanonicalHash.OfFields(canonicalDefinition);

    private static Result ValidationFailure(string detail)
        => new(false, ResultErrorKind.Validation, DomainError.Format(ErrorCodes.InvalidRequest, detail));

    private static string NewEntityTag() => Guid.NewGuid().ToString("N");
}

/// <summary>Previously validated state used by <see cref="ClassPolicyVersion.Rehydrate"/>.</summary>
internal sealed record ClassPolicyVersionState
{
    public required string Application { get; init; }

    public required NotificationClass Class { get; init; }

    public required int Version { get; init; }

    public required string Status { get; init; }

    public required int SchemaVersion { get; init; }

    public required string DefinitionJson { get; init; }

    public required string CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Persisted hash; when absent it is recomputed from the definition.</summary>
    public string? ContentHash { get; init; }

    public IReadOnlyList<string> Editors { get; init; } = [];
}

/// <summary>Everything a new class policy draft captures at creation.</summary>
public sealed record ClassPolicyDraftInput
{
    public required string Application { get; init; }

    public required NotificationClass Class { get; init; }

    public required int Version { get; init; }

    public required string DefinitionJson { get; init; }

    public required string CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
