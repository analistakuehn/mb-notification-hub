using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Governed identity of a rendering layout: the header/footer wrapper a
/// template version pins for reproducible rendering. Content lives in
/// versions; a template only ever renders inside a published layout version.
/// </summary>
public sealed class Layout
{
    public const int MaxTextLength = 200;

    private readonly string _key;

    private Layout(LayoutKey key, LayoutMetadata metadata)
    {
        _key = key.Value;
        OwnerTeam = metadata.OwnerTeam;
        DefaultLocale = metadata.DefaultLocale;
        Status = LayoutStatus.Active;
    }

    // EF Core materialization: fields are populated from the store.
    private Layout()
    {
        _key = null!;
        OwnerTeam = null!;
    }

    public LayoutKey Key => LayoutKey.Trusted(_key);

    public string OwnerTeam { get; }

    /// <summary>Locale the layout fallback chain lands on when no exact or base-language content exists.</summary>
    public Locale? DefaultLocale { get; }

    public LayoutStatus Status { get; private set; }

    public static Result<Layout> Create(LayoutKey key, LayoutMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(metadata);

        var ownerTeam = metadata.OwnerTeam?.Trim() ?? string.Empty;
        if (ownerTeam.Length == 0 || ownerTeam.Length > MaxTextLength)
        {
            return Result.ValidationError<Layout>(DomainError.Format(
                ErrorCodes.InvalidRequest,
                $"Field 'ownerTeam' is required and must have at most {MaxTextLength} characters."));
        }

        return Result.Success(new Layout(key, metadata with { OwnerTeam = ownerTeam }));
    }

    /// <summary>
    /// Marks the layout as deprecated: published versions stay reproducible,
    /// but the layout should not receive new references.
    /// </summary>
    public Result Deprecate()
    {
        if (Status != LayoutStatus.Active)
        {
            return InvalidTransition(LayoutStatuses.Deprecated);
        }

        Status = LayoutStatus.Deprecated;
        return Result.Success();
    }

    /// <summary>
    /// Marks the layout as disabled: the terminal state of the identity.
    /// Nothing publishes and there is no way back via the API.
    /// </summary>
    public Result Disable()
    {
        if (Status == LayoutStatus.Disabled)
        {
            return InvalidTransition(LayoutStatuses.Disabled);
        }

        Status = LayoutStatus.Disabled;
        return Result.Success();
    }

    /// <summary>
    /// Publication guard: a deprecated or disabled layout accepts no new
    /// published versions, including republications through rollback.
    /// </summary>
    public Result EnsureAcceptsPublication()
        => Status == LayoutStatus.Active
            ? Result.Success()
            : Result.BusinessRuleViolation(DomainError.StateTransition(
                Status.Canonical(),
                LayoutStatuses.AllowedTransitions(Status),
                $"Layout '{_key}' is '{Status.Canonical()}' and does not accept publications."));

    private Result InvalidTransition(string target)
        => Result.BusinessRuleViolation(DomainError.StateTransition(
            Status.Canonical(),
            LayoutStatuses.AllowedTransitions(Status),
            $"Cannot change layout '{_key}' to '{target}': current status is '{Status.Canonical()}'."));
}

/// <summary>Metadata captured when a layout identity is created.</summary>
public sealed record LayoutMetadata
{
    public required string OwnerTeam { get; init; }

    public Locale? DefaultLocale { get; init; }
}
