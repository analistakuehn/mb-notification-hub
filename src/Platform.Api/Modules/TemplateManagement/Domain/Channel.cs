using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>Delivery channel a template content entry targets. Canonical, closed set.</summary>
public sealed class Channel
{
    public static readonly Channel Email = new("email");
    public static readonly Channel Sms = new("sms");
    public static readonly Channel Push = new("push");
    public static readonly Channel WhatsApp = new("whatsapp");

    private Channel(string value) => Value = value;

    public string Value { get; }

    public static IReadOnlyList<Channel> All { get; } = [Email, Sms, Push, WhatsApp];

    public static Result<Channel> Create(string? value)
    {
        Channel? match = Find(value);
        return match is null
            ? Result.ValidationError<Channel>(DomainError.Format(
                ErrorCodes.InvalidRequest,
                $"Unknown channel '{value}'. Supported channels: {string.Join(", ", All.Select(channel => channel.Value))}."))
            : Result.Success(match);
    }

    /// <summary>Rehydrates a channel that already passed validation (persistence, canonical data).</summary>
    internal static Channel Trusted(string value)
        => Find(value) ?? throw new InvalidOperationException($"Unknown persisted channel '{value}'.");

    public override string ToString() => Value;

    private static Channel? Find(string? value)
        => All.FirstOrDefault(channel => string.Equals(channel.Value, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}
