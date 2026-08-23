using System.Text.Json;
using System.Text.Json.Nodes;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Rules;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Auditing;

/// <summary>
/// The allow-list that turns the free-form evidence of a policy rule into a
/// disclosable projection, key by key, per rule.
///
/// Two reasons keep the raw document inside this module, and only one of them is
/// about personal data. The evidence carries facts about the recipient (the
/// quiet-hours rule records the timezone and the local time, which infer
/// approximate geography; the channel rule records which channels were
/// reachable, which states whether the recipient has a usable contact). Under
/// the audit role those facts are disclosable, and they are on this list. The
/// other reason does not fall away with the role: the document is free-form in
/// each rule's own shape, so serving it raw would freeze an internal rule shape
/// as a public contract and break every consumer the next time a rule is
/// adjusted, which is the opposite of reproducible evidence.
/// </summary>
/// <remarks>
/// The support query surface does not project rule evidence at all in this
/// phase. When it does, it gets a narrower list of its own; this one is the
/// audit list and it is the widest.
/// </remarks>
internal static class PolicyEvidenceProjection
{
    /// <summary>
    /// The disclosable evidence keys of each rule, by rule name. A rule with no
    /// entry discloses nothing, which is a defect the completeness check exists
    /// to catch before it reaches an auditor.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedKeysByRule { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [ConsentGateRule.RuleName] = Keys("basis", "purpose", "granted", "denied"),
            [QuietHoursRule.RuleName] = Keys("guard", "window", "timezone", "localTime", "releaseAt"),
            [DedupeWindowRule.RuleName] = Keys(
                "windowSeconds", "acquired", "heldByThisNotification", "failOpen", "risk"),
            [ChannelSelectionRule.RuleName] = Keys(
                "remaining", "plan", "withContent", "reachable", "selected"),
        };

    /// <summary>
    /// Projects one recorded evidence document: the allow-listed members, plus
    /// the names of the members the list does not cover. Withholding a value
    /// silently would turn the allow-list into an invisible hole in the trail,
    /// so the uncovered names travel and only the values stay behind.
    /// </summary>
    internal static PolicyEvidenceView Project(string rule, string evidenceJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceJson);

        IReadOnlySet<string> allowed = AllowedKeysByRule.TryGetValue(rule, out IReadOnlySet<string>? keys)
            ? keys
            : new HashSet<string>(StringComparer.Ordinal);

        using JsonDocument document = JsonDocument.Parse(evidenceJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new PolicyEvidenceView(EmptyObject(), []);
        }

        var projected = new JsonObject();
        var undisclosed = new List<string>();
        foreach (JsonProperty member in document.RootElement.EnumerateObject())
        {
            if (allowed.Contains(member.Name))
            {
                projected[member.Name] = JsonNode.Parse(member.Value.GetRawText());
            }
            else
            {
                undisclosed.Add(member.Name);
            }
        }

        undisclosed.Sort(StringComparer.Ordinal);
        return new PolicyEvidenceView(ToElement(projected), undisclosed);
    }

    private static HashSet<string> Keys(params string[] names) => new(names, StringComparer.Ordinal);

    private static JsonElement EmptyObject() => ToElement(new JsonObject());

    private static JsonElement ToElement(JsonObject value)
    {
        using JsonDocument document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }
}

/// <summary>The disclosable part of one rule's evidence, plus the names it withheld.</summary>
internal sealed record PolicyEvidenceView(JsonElement Evidence, IReadOnlyList<string> UndisclosedKeys);
