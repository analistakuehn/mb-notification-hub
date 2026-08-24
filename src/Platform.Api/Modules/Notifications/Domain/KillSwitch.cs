namespace NotificationHub.Api.Modules.Notifications.Domain;

public static class KillSwitchStates
{
    public const string Active = "active";
    public const string Inactive = "inactive";
}

/// <summary>Authoritative emergency-stop state for one scope and key.</summary>
public sealed class KillSwitchState
{
    private KillSwitchState(
        KillSwitchScope scope,
        string key,
        string actor,
        DateTimeOffset updatedAt)
    {
        Scope = scope.Canonical();
        Key = key;
        State = KillSwitchStates.Active;
        Version = 1;
        Actor = actor;
        UpdatedAt = updatedAt;
    }

    // EF Core materialization: fields are populated from the store.
    private KillSwitchState()
    {
        Scope = null!;
        Key = null!;
        State = null!;
        Actor = null!;
    }

    public string Scope { get; }

    public string Key { get; }

    public string State { get; private set; }

    public long Version { get; private set; }

    public string Actor { get; private set; }

    public string? SecondActor { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsActive => string.Equals(State, KillSwitchStates.Active, StringComparison.Ordinal);

    internal static KillSwitchState Activate(
        KillSwitchScope scope,
        string key,
        string actor,
        DateTimeOffset updatedAt)
        => new(scope, key, actor, updatedAt);

    internal KillSwitchTransition? Change(
        bool active,
        string actor,
        string? secondActor,
        DateTimeOffset updatedAt)
    {
        var after = active ? KillSwitchStates.Active : KillSwitchStates.Inactive;
        if (string.Equals(State, after, StringComparison.Ordinal))
        {
            return null;
        }

        var before = State;
        State = after;
        Version++;
        Actor = actor;
        SecondActor = secondActor;
        UpdatedAt = updatedAt;
        return new KillSwitchTransition(before, after);
    }
}

internal sealed record KillSwitchTransition(string Before, string After);
