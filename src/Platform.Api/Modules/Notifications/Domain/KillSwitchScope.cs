namespace NotificationHub.Api.Modules.Notifications.Domain;

/// <summary>Emergency-stop dimensions enforced by the notification lifecycle.</summary>
public enum KillSwitchScope
{
    Producer = 0,
    Application = 1,
    Channel = 2,
}

public static class KillSwitchScopes
{
    public const string Producer = "producer";
    public const string Application = "application";
    public const string Channel = "channel";

    public static string Canonical(this KillSwitchScope scope)
        => scope switch
        {
            KillSwitchScope.Producer => Producer,
            KillSwitchScope.Application => Application,
            KillSwitchScope.Channel => Channel,
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Escopo de kill switch desconhecido."),
        };

    public static bool TryParse(string? value, out KillSwitchScope scope)
    {
        scope = value switch
        {
            Producer => KillSwitchScope.Producer,
            Application => KillSwitchScope.Application,
            Channel => KillSwitchScope.Channel,
            _ => default,
        };
        return value is Producer or Application or Channel;
    }
}

internal readonly record struct KillSwitchAddress(KillSwitchScope Scope, string Key);

internal static class KillSwitchKeys
{
    internal const int MaxLength = 200;

    internal static bool TryNormalize(KillSwitchScope scope, string? value, out string key)
    {
        key = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.Length > MaxLength || normalized.Any(char.IsControl))
        {
            return false;
        }

        key = scope == KillSwitchScope.Channel ? normalized.ToLowerInvariant() : normalized;
        return true;
    }
}
