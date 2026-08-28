using System.Reflection;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.History;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Reads;

namespace NotificationHub.UnitTests.Notifications.History;

/// <summary>
/// The query surface is the one place where a careless projection would turn
/// stored ciphertext or a stored business payload into a public response.
/// These checks walk the whole response graph instead of trusting a reviewer.
/// </summary>
public sealed class NotificationQueryResponseShapeTests
{
    private static readonly Type[] ResponseRoots =
    [
        typeof(GetNotification.Response),
        typeof(NotificationHistoryPage),
    ];

    // Fragments of the stored columns the query contract forbids. The masked
    // contact value is not one of them: it is computed by the module that owns
    // the data and is exactly what this surface may show.
    private static readonly string[] ForbiddenFragments = ["RenderedContent", "Variables"];

    [Fact]
    public void The_forbidden_sources_still_exist_on_the_entities_this_test_guards()
    {
        // Without this, renaming a column property would silently turn every
        // assertion below into a check against names nothing produces.
        typeof(NotificationAttempt).GetProperty(nameof(NotificationAttempt.RenderedContentEncrypted))
            .ShouldNotBeNull();
        typeof(Notification).GetProperty(nameof(Notification.VariablesMaskedJson)).ShouldNotBeNull();
        typeof(Notification).GetProperty(nameof(Notification.VariablesEncrypted)).ShouldNotBeNull();
    }

    [Fact]
    public void No_member_of_the_query_responses_carries_the_rendered_content_or_the_variables()
    {
        var findings = ResponseGraph()
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => ForbiddenFragments.Any(fragment =>
                    property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                .Select(property => $"{type.Name}.{property.Name}"))
            .ToArray();

        findings.ShouldBeEmpty();
    }

    [Fact]
    public void No_member_of_the_query_responses_is_a_byte_payload()
    {
        var findings = ResponseGraph()
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType == typeof(byte[]))
                .Select(property => $"{type.Name}.{property.Name}"))
            .ToArray();

        findings.ShouldBeEmpty();
    }

    [Fact]
    public void The_response_graph_actually_reaches_the_attempt_and_its_target()
    {
        // The two checks above only mean something if the walk descends into
        // the nested records; this pins the traversal itself.
        Type[] graph = [.. ResponseGraph()];

        graph.ShouldContain(typeof(GetNotification.Attempt));
        graph.ShouldContain(typeof(GetNotification.Target));
        graph.ShouldContain(typeof(GetNotification.Evaluation));
        graph.ShouldContain(typeof(NotificationHistoryItem));
    }

    [Fact]
    public void The_single_notification_response_declares_no_delivery_phase_member()
    {
        var names = typeof(GetNotification.Response)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        names.ShouldNotContain("DeliveryEvents");
        names.ShouldNotContain("ReadAt");
    }

    private static HashSet<Type> ResponseGraph()
    {
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>(ResponseRoots);

        while (pending.Count > 0)
        {
            Type current = pending.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            foreach (PropertyInfo property in current.GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (Type candidate in Candidates(property.PropertyType))
                {
                    if (candidate.Assembly == current.Assembly)
                    {
                        pending.Push(candidate);
                    }
                }
            }
        }

        return seen;
    }

    private static IEnumerable<Type> Candidates(Type type)
    {
        yield return type;
        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                yield return argument;
            }
        }
    }
}
