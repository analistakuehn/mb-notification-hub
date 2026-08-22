using System.Text.RegularExpressions;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Governed identity of a notification template: metadata that anchors class,
/// ownership and legal basis. Content lives in versions; only a published
/// version is ever rendered.
/// </summary>
public sealed partial class Template
{
    public const int MaxApplicationLength = 100;
    public const int MaxTextLength = 200;

    private readonly string _key;

    private Template(TemplateKey key, TemplateMetadata metadata)
    {
        _key = key.Value;
        Application = metadata.Application;
        Class = metadata.Class;
        OwnerTeam = metadata.OwnerTeam;
        Purpose = metadata.Purpose;
        LegalBasis = metadata.LegalBasis;
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

        var normalized = new TemplateMetadata(
            application,
            metadata.Class,
            ownerTeam.Value!,
            purpose.Value!,
            legalBasis.Value!);
        return Result.Success(new Template(key, normalized));
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

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex ApplicationPattern();
}

/// <summary>Metadata captured when a template identity is created.</summary>
public sealed record TemplateMetadata(
    string Application,
    NotificationClass Class,
    string OwnerTeam,
    string Purpose,
    string LegalBasis);
