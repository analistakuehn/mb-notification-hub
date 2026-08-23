using System.Text.Json.Serialization;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Http;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Reads;

/// <summary>
/// One notification as the history lists it. The list answers "which
/// notifications", not "what happened to this one": attempts, policy
/// evaluations and hashes belong to the single-notification read.
/// </summary>
internal sealed record NotificationHistoryItem
{
    public required string Id { get; init; }

    public required string Application { get; init; }

    public required string RecipientId { get; init; }

    public required string Class { get; init; }

    public required string Status { get; init; }

    public required string TemplateKey { get; init; }

    public required int TemplateVersion { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; init; }
}

/// <summary>
/// One page of the history, shared by the two list routes because they answer
/// the same question over a different subject. The effective window travels
/// with the page: defaults applied silently would leave the caller unable to
/// tell an empty history from a window that never covered the rows.
/// </summary>
internal sealed record NotificationHistoryPage
{
    public required IReadOnlyList<NotificationHistoryItem> Items { get; init; }

    /// <summary>Absent on the last page.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextCursor { get; init; }

    public required NotificationQueryWindow Window { get; init; }
}

/// <summary>
/// Outcomes of a history read. The two refusals carry different problem types
/// on purpose: a bad limit is the caller's parameter, a bad cursor is the
/// caller's position, and a client retrying blindly needs to know which one to
/// drop.
/// </summary>
internal abstract record NotificationHistoryOutcome
{
    private NotificationHistoryOutcome()
    {
    }

    internal sealed record Page(NotificationHistoryPage Value) : NotificationHistoryOutcome;

    internal sealed record InvalidRequest(string Detail) : NotificationHistoryOutcome;

    internal sealed record InvalidCursor(string Detail) : NotificationHistoryOutcome;
}
