using System.Reflection;
using NotificationHub.Api.Modules.Compliance.Features.Queries;
using NotificationHub.Api.Modules.ContactConsent.Domain;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.UnitTests.Compliance;

/// <summary>
/// The audit surface is the widest disclosure in the platform, so what it does
/// not declare matters as much as what it does. These checks walk the whole
/// response graph instead of trusting a reviewer.
/// </summary>
public sealed class AuditResponseShapeTests
{
    private static readonly Type[] ResponseRoots =
    [
        typeof(GetNotificationEvidence.Response),
        typeof(GetAttemptContent.Response),
    ];

    [Fact]
    public void The_guarded_sources_still_exist_on_the_entities_this_test_watches()
    {
        // Without this, renaming a column property would silently turn the
        // assertions below into checks against names nothing produces.
        typeof(NotificationAttempt).GetProperty(nameof(NotificationAttempt.DeliveredAt)).ShouldNotBeNull();
        typeof(NotificationAttempt).GetProperty(nameof(NotificationAttempt.RenderedContentEncrypted))
            .ShouldNotBeNull();
        typeof(DeviceToken).GetProperty(nameof(DeviceToken.Token)).ShouldNotBeNull();
    }

    [Fact]
    public void The_reconstruction_declares_no_delivery_member_in_any_form()
    {
        // The question "did the provider confirm delivery" is not answerable in
        // this phase, so no member claims it, not even as an empty array: an
        // empty array would state that no event happened.
        var names = MemberNames().ToArray();

        names.ShouldNotContain("DeliveryEvents");
        names.ShouldNotContain("DeliveredAt");
        names.ShouldNotContain("ReadAt");
        names.ShouldNotContain("ReadReceipt");
    }

    [Fact]
    public void No_member_of_the_reconstruction_carries_a_device_token_in_any_form()
    {
        var findings = MemberNames()
            .Where(name => name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("DeviceTokenId", StringComparison.Ordinal))
            .ToArray();

        findings.ShouldBeEmpty();
    }

    [Fact]
    public void No_member_of_the_reconstruction_is_a_byte_payload()
    {
        var findings = ResponseGraph()
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType == typeof(byte[]))
                .Select(property => $"{type.Name}.{property.Name}"))
            .ToArray();

        findings.ShouldBeEmpty();
    }

    [Fact]
    public void The_content_response_verifies_the_masked_hash_and_never_the_complete_one()
    {
        var names = typeof(GetAttemptContent.Response)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        names.ShouldContain(nameof(GetAttemptContent.Response.ContentHashMasked));
        names.ShouldContain(nameof(GetAttemptContent.Response.RecomputedContentHashMasked));
        names.ShouldContain(nameof(GetAttemptContent.Response.ContentHashMaskedVerified));
        names.ShouldContain(nameof(GetAttemptContent.Response.ContentHashFull));
        names.ShouldContain(nameof(GetAttemptContent.Response.DisclosedForm));

        // Cryptographic verification of the complete form is impossible once
        // the masking replaced it; a member claiming it would be a lie.
        names.ShouldNotContain("ContentHashFullVerified");
        names.ShouldNotContain("RecomputedContentHashFull");
    }

    [Fact]
    public void The_response_graph_actually_reaches_the_nested_blocks()
    {
        // The checks above only mean something if the walk descends; this pins
        // the traversal itself.
        Type[] graph = [.. ResponseGraph()];

        graph.ShouldContain(typeof(GetNotificationEvidence.TrailView));
        graph.ShouldContain(typeof(GetNotificationEvidence.LinkView));
        graph.ShouldContain(typeof(GetNotificationEvidence.StateView));
        graph.ShouldContain(typeof(GetNotificationEvidence.AttemptView));
        graph.ShouldContain(typeof(GetNotificationEvidence.RecipientView));
        graph.ShouldContain(typeof(GetNotificationEvidence.DeviceRegistrationView));
        graph.ShouldContain(typeof(GetNotificationEvidence.ConsentEntryView));
        graph.ShouldContain(typeof(GetNotificationEvidence.PolicyEvaluationView));
    }

    private static IEnumerable<string> MemberNames()
        => ResponseGraph()
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(property => property.Name);

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
