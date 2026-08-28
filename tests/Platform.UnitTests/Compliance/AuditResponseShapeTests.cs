using System.Reflection;
using System.Text.Json.Serialization;
using NotificationHub.Api.Modules.Compliance.Features.Disclosure;
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
    public void The_reconstruction_answers_delivery_and_still_declares_no_read_receipt()
    {
        // "Did the provider confirm delivery" is answerable now that the
        // feedback is recorded, so both members exist. Whether the recipient
        // read the message is still recorded nowhere, so no member claims it,
        // not even as an empty array: an empty array would state that nobody
        // read it.
        var names = MemberNames().ToArray();

        names.ShouldContain("DeliveryEvents");
        names.ShouldContain("DeliveredAt");
        names.ShouldNotContain("ReadAt");
        names.ShouldNotContain("ReadReceipt");
    }

    [Fact]
    public void The_provider_feedback_view_carries_five_members_and_no_payload()
    {
        var names = typeof(GetNotificationEvidence.DeliveryEventView)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // The stored provider payload carries the destination in the clear, so
        // the view has no member it could land in, under any name.
        names.ShouldBe(["ErrorCode", "Kind", "OccurredAt", "ProviderEventId", "ProviderKey"]);
    }

    [Fact]
    public void The_provider_feedback_list_is_never_omitted_from_an_attempt()
    {
        PropertyInfo feedback = typeof(GetNotificationEvidence.AttemptView)
            .GetProperty(nameof(GetNotificationEvidence.AttemptView.DeliveryEvents))
            .ShouldNotBeNull();

        // An empty list asserts that the store holds no feedback for the
        // attempt. Serializing it away would turn that assertion back into the
        // silence the surface used to keep.
        feedback.GetCustomAttribute<JsonIgnoreAttribute>().ShouldBeNull();
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
        graph.ShouldContain(typeof(GetNotificationEvidence.DeliveryEventView));
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
