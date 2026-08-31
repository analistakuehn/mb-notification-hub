using System.Text.RegularExpressions;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Governed identity of a notification template: metadata that anchors class,
/// ownership, legal basis, the default content locale, and the link-domain
/// allowlist. Content lives in versions; only a published version is ever
/// rendered.
/// <para>
/// Which variables carry sensitive data is deliberately not here. That
/// declaration decides what the render masks and what the trail stores in
/// clear, so it belongs to the unit an approval covers: it lives on the
/// version, inside the content hash a second person approves.
/// </para>
/// </summary>
public sealed partial class Template
{
    public const int MaxApplicationLength = ApplicationName.MaxLength;
    public const int MaxTextLength = 200;
    public const int MaxLinkDomains = 50;
    public const int MaxLinkDomainLength = 255;

    private readonly string _key;
    private readonly List<string> _linkDomainsAllowed = [];

    private Template(TemplateKey key, TemplateMetadata metadata)
    {
        _key = key.Value;
        Application = metadata.Application;
        Class = metadata.Class;
        OwnerTeam = metadata.OwnerTeam;
        Purpose = metadata.Purpose;
        LegalBasis = metadata.LegalBasis;
        DefaultLocale = metadata.DefaultLocale;
        _linkDomainsAllowed.AddRange(metadata.LinkDomainsAllowed);
        Status = TemplateStatus.Active;
    }

    // EF Core materialization: fields are populated from the store.
    private Template()
    {
        _key = null!;
        Application = null!;
        OwnerTeam = null!;
        Purpose = null!;
        LegalBasis = null!;
    }

    public TemplateKey Key => TemplateKey.Trusted(_key);

    public string Application { get; }

    public NotificationClass Class { get; }

    public string OwnerTeam { get; }

    public string Purpose { get; }

    public string LegalBasis { get; }

    /// <summary>Locale the fallback chain lands on when no exact or base-language content exists.</summary>
    public Locale? DefaultLocale { get; }

    /// <summary>Domains links and URL variables may point at. Empty means the template allows no links.</summary>
    public IReadOnlyList<string> LinkDomainsAllowed => _linkDomainsAllowed;

    public TemplateStatus Status { get; private set; }

    public static Result<Template> Create(TemplateKey key, TemplateMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(metadata);

        Result<string> application = ApplicationName.Create(metadata.Application);
        if (application.IsFailure)
        {
            return new Result<Template>(false, null, application.ErrorKind, application.Error);
        }

        Result<string> ownerTeam = RequiredText(metadata.OwnerTeam, "ownerTeam");
        if (ownerTeam.IsFailure)
        {
            return new Result<Template>(false, null, ownerTeam.ErrorKind, ownerTeam.Error);
        }

        Result<string> purpose = NormalizePurpose(metadata.Purpose);
        if (purpose.IsFailure)
        {
            return new Result<Template>(false, null, purpose.ErrorKind, purpose.Error);
        }

        Result<string> legalBasis = RequiredText(metadata.LegalBasis, "legalBasis");
        if (legalBasis.IsFailure)
        {
            return new Result<Template>(false, null, legalBasis.ErrorKind, legalBasis.Error);
        }

        Result<List<string>> linkDomains = NormalizeLinkDomains(metadata.LinkDomainsAllowed);
        if (linkDomains.IsFailure)
        {
            return new Result<Template>(false, null, linkDomains.ErrorKind, linkDomains.Error);
        }

        var normalized = new TemplateMetadata
        {
            Application = application.Value!,
            Class = metadata.Class,
            OwnerTeam = ownerTeam.Value!,
            Purpose = purpose.Value!,
            LegalBasis = legalBasis.Value!,
            DefaultLocale = metadata.DefaultLocale,
            LinkDomainsAllowed = linkDomains.Value!,
        };
        return Result.Success(new Template(key, normalized));
    }

    /// <summary>
    /// Marks the template as deprecated: existing versions stay reproducible,
    /// but new notification requests are rejected by the runtime.
    /// </summary>
    public Result Deprecate()
    {
        if (Status != TemplateStatus.Active)
        {
            return InvalidTransition(TemplateStatuses.Deprecated);
        }

        Status = TemplateStatus.Deprecated;
        return Result.Success();
    }

    /// <summary>
    /// Marks the template as disabled: the terminal state of the identity.
    /// Nothing renders, nothing publishes, and there is no way back via the API.
    /// </summary>
    public Result Disable()
    {
        if (Status == TemplateStatus.Disabled)
        {
            return InvalidTransition(TemplateStatuses.Disabled);
        }

        Status = TemplateStatus.Disabled;
        return Result.Success();
    }

    /// <summary>
    /// Publication guard: a deprecated or disabled template accepts no new
    /// published versions, including republications through rollback.
    /// </summary>
    public Result EnsureAcceptsPublication()
        => Status == TemplateStatus.Active
            ? Result.Success()
            : Result.BusinessRuleViolation(DomainError.StateTransition(
                Status.Canonical(),
                TemplateStatuses.AllowedTransitions(Status),
                $"Template '{_key}' is '{Status.Canonical()}' and does not accept publications."));

    /// <summary>True when the host equals an allowed domain or is one of its subdomains.</summary>
    public bool IsLinkDomainAllowed(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var candidate = host.Trim().ToLowerInvariant();
        return _linkDomainsAllowed.Any(domain =>
            string.Equals(candidate, domain, StringComparison.Ordinal)
            || candidate.EndsWith("." + domain, StringComparison.Ordinal));
    }

    private Result InvalidTransition(string target)
        => Result.BusinessRuleViolation(DomainError.StateTransition(
            Status.Canonical(),
            TemplateStatuses.AllowedTransitions(Status),
            $"Cannot change template '{_key}' to '{target}': current status is '{Status.Canonical()}'."));

    private static Result<string> RequiredText(string? value, string fieldName)
    {
        var candidate = value?.Trim() ?? string.Empty;
        return candidate.Length == 0 || candidate.Length > MaxTextLength
            ? Result.ValidationError<string>(DomainError.Format(
                ErrorCodes.InvalidRequest,
                $"Field '{fieldName}' is required and must have at most {MaxTextLength} characters."))
            : Result.Success(candidate);
    }

    /// <summary>
    /// Canonizes the purpose on the way in: lower case, then the same required
    /// text guard as the neighbouring fields, so the length limit applies to
    /// the form that is actually stored.
    /// <para>
    /// Only this field is folded, and only here. The purpose is the one
    /// governance value machines read: an ordinal equality decides whether the
    /// publication catalog bans links in an SMS, whether the render refuses to
    /// ship one, whether a recipient read may fall back to the last known
    /// snapshot, and whether a code takes the authentication queue instead of
    /// the ordinary one. The go-live gate asks the same question in SQL, where
    /// folding case would cost the index. This is the only write door of the
    /// column, so a value canonized here can never be read past later.
    /// </para>
    /// <para>
    /// The owner team and the legal basis share the required text guard and
    /// are read by people, never compared for equality, so lowering their case
    /// would rewrite two values nobody asked to change.
    /// </para>
    /// </summary>
    private static Result<string> NormalizePurpose(string? value)
        => RequiredText(value?.ToLowerInvariant(), "purpose");

    private static Result<List<string>> NormalizeLinkDomains(IReadOnlyList<string> domains)
    {
        if (domains.Count > MaxLinkDomains)
        {
            return Result.ValidationError<List<string>>(DomainError.Format(
                ErrorCodes.InvalidRequest,
                $"At most {MaxLinkDomains} link domains are allowed."));
        }

        List<string> normalized = [];
        foreach (var domain in domains)
        {
            var candidate = domain?.Trim().ToLowerInvariant() ?? string.Empty;
            if (candidate.Length == 0
                || candidate.Length > MaxLinkDomainLength
                || !LinkDomainPattern().IsMatch(candidate))
            {
                return Result.ValidationError<List<string>>(DomainError.Format(
                    ErrorCodes.InvalidRequest,
                    "Each allowed link domain must be a bare host name such as 'example.com', "
                    + "without scheme, path or port."));
            }

            if (!normalized.Contains(candidate, StringComparer.Ordinal))
            {
                normalized.Add(candidate);
            }
        }

        return Result.Success(normalized);
    }

    [GeneratedRegex(@"^[a-z0-9]+(?:[.-][a-z0-9]+)*\.[a-z]{2,}$")]
    private static partial Regex LinkDomainPattern();
}

/// <summary>Metadata captured when a template identity is created.</summary>
public sealed record TemplateMetadata
{
    public required string Application { get; init; }

    public required NotificationClass Class { get; init; }

    public required string OwnerTeam { get; init; }

    public required string Purpose { get; init; }

    public required string LegalBasis { get; init; }

    public Locale? DefaultLocale { get; init; }

    public IReadOnlyList<string> LinkDomainsAllowed { get; init; } = [];
}
