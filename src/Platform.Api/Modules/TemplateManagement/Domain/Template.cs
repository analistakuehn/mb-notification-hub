using System.Text.RegularExpressions;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Governed identity of a notification template: metadata that anchors class,
/// ownership, legal basis, the default content locale, the link-domain
/// allowlist, and which variables carry sensitive data. Content lives in
/// versions; only a published version is ever rendered.
/// </summary>
public sealed partial class Template
{
    public const int MaxApplicationLength = 100;
    public const int MaxTextLength = 200;
    public const int MaxLinkDomains = 50;
    public const int MaxLinkDomainLength = 255;
    public const int MaxSensitiveVariables = 100;
    public const int MaxVariableNameLength = 100;

    private readonly string _key;
    private readonly List<string> _linkDomainsAllowed = [];
    private readonly List<string> _sensitiveVariables = [];

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
        _sensitiveVariables.AddRange(metadata.SensitiveVariables);
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

    /// <summary>Variable names whose values carry sensitive data and must never sit in a URL position.</summary>
    public IReadOnlyList<string> SensitiveVariables => _sensitiveVariables;

    public TemplateStatus Status { get; private set; }

    public static Result<Template> Create(TemplateKey key, TemplateMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(metadata);

        string application = metadata.Application?.Trim() ?? string.Empty;
        if (application.Length == 0
            || application.Length > MaxApplicationLength
            || !ApplicationPattern().IsMatch(application))
        {
            return Result.ValidationError<Template>(DomainError.Format(
                ErrorCodes.InvalidRequest,
                $"Application must be 1-{MaxApplicationLength} lowercase alphanumeric characters "
                + "in segments separated by '-'."));
        }

        Result<string> ownerTeam = RequiredText(metadata.OwnerTeam, "ownerTeam");
        if (ownerTeam.IsFailure)
        {
            return new Result<Template>(false, null, ownerTeam.ErrorKind, ownerTeam.Error);
        }

        Result<string> purpose = RequiredText(metadata.Purpose, "purpose");
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

        Result<List<string>> sensitiveVariables = NormalizeSensitiveVariables(metadata.SensitiveVariables);
        if (sensitiveVariables.IsFailure)
        {
            return new Result<Template>(false, null, sensitiveVariables.ErrorKind, sensitiveVariables.Error);
        }

        var normalized = new TemplateMetadata
        {
            Application = application,
            Class = metadata.Class,
            OwnerTeam = ownerTeam.Value!,
            Purpose = purpose.Value!,
            LegalBasis = legalBasis.Value!,
            DefaultLocale = metadata.DefaultLocale,
            LinkDomainsAllowed = linkDomains.Value!,
            SensitiveVariables = sensitiveVariables.Value!,
        };
        return Result.Success(new Template(key, normalized));
    }

    /// <summary>True when the host equals an allowed domain or is one of its subdomains.</summary>
    public bool IsLinkDomainAllowed(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        string candidate = host.Trim().ToLowerInvariant();
        return _linkDomainsAllowed.Any(domain =>
            string.Equals(candidate, domain, StringComparison.Ordinal)
            || candidate.EndsWith("." + domain, StringComparison.Ordinal));
    }

    private static Result<string> RequiredText(string? value, string fieldName)
    {
        string candidate = value?.Trim() ?? string.Empty;
        return candidate.Length == 0 || candidate.Length > MaxTextLength
            ? Result.ValidationError<string>(DomainError.Format(
                ErrorCodes.InvalidRequest,
                $"Field '{fieldName}' is required and must have at most {MaxTextLength} characters."))
            : Result.Success(candidate);
    }

    private static Result<List<string>> NormalizeLinkDomains(IReadOnlyList<string> domains)
    {
        if (domains.Count > MaxLinkDomains)
        {
            return Result.ValidationError<List<string>>(DomainError.Format(
                ErrorCodes.InvalidRequest,
                $"At most {MaxLinkDomains} link domains are allowed."));
        }

        List<string> normalized = [];
        foreach (string domain in domains)
        {
            string candidate = domain?.Trim().ToLowerInvariant() ?? string.Empty;
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

    private static Result<List<string>> NormalizeSensitiveVariables(IReadOnlyList<string> variables)
    {
        if (variables.Count > MaxSensitiveVariables)
        {
            return Result.ValidationError<List<string>>(DomainError.Format(
                ErrorCodes.InvalidRequest,
                $"At most {MaxSensitiveVariables} sensitive variables are allowed."));
        }

        List<string> normalized = [];
        foreach (string variable in variables)
        {
            string candidate = variable?.Trim() ?? string.Empty;
            if (candidate.Length == 0
                || candidate.Length > MaxVariableNameLength
                || !VariableNamePattern().IsMatch(candidate))
            {
                return Result.ValidationError<List<string>>(DomainError.Format(
                    ErrorCodes.InvalidRequest,
                    "Each sensitive variable must be a template variable name: letters, digits and "
                    + "underscores, not starting with a digit."));
            }

            if (!normalized.Contains(candidate, StringComparer.Ordinal))
            {
                normalized.Add(candidate);
            }
        }

        return Result.Success(normalized);
    }

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex ApplicationPattern();

    [GeneratedRegex(@"^[a-z0-9]+(?:[.-][a-z0-9]+)*\.[a-z]{2,}$")]
    private static partial Regex LinkDomainPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex VariableNamePattern();
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

    public IReadOnlyList<string> SensitiveVariables { get; init; } = [];
}
