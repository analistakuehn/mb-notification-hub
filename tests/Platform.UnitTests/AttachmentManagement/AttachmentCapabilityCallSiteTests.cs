using System.Reflection;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capability;

namespace NotificationHub.UnitTests.AttachmentManagement;

/// <summary>
/// Where the deployment state of the capability is asked about, read off the
/// compiled assembly rather than off the source tree.
/// <para>
/// Two doors close and no more. The value of the rule is not that two is a
/// pleasing number: it is that everything working on an attachment that already
/// exists must never ask this question, because a third reader anywhere on the
/// reading, the attempt, the repair, the sweep or the investigation would turn
/// switching the capability off into a freeze of what was already accepted.
/// </para>
/// <para>
/// It reads constructor parameters because that is how this composition hands
/// the answer over, and it counts a reader of the bound section as a reader of
/// the gate: a type that bypassed the gate by binding the options itself would
/// be exactly the drift this exists to catch.
/// </para>
/// </summary>
public sealed class AttachmentCapabilityCallSiteTests
{
    /// <summary>
    /// The types that may ask. Named rather than counted, because a count is
    /// satisfied by a reader moving from one place to another and the rule is
    /// about which places.
    /// </summary>
    private static readonly string[] Doors =
    [
        "NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments"
            + ".RegisterAttachment+Handler",
        "NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence"
            + ".TransactionalAttachmentClaim",
    ];

    [Fact]
    public void Only_the_two_doors_of_a_new_acceptance_ask_whether_the_capability_is_enabled()
    {
        var readers = Readers();

        // The walk has to reach its subject before the equality below means
        // anything: an empty set would satisfy a rule that lost its assembly.
        readers.ShouldNotBeEmpty();
        readers.ShouldBe(Doors.Order(StringComparer.Ordinal), ignoreOrder: false);
    }

    /// <summary>
    /// Every type that takes the gate or the section it binds, by full name,
    /// with the gate itself left out: it is the reader by definition and
    /// counting it would make the rule about one more name than it is about.
    /// </summary>
    private static string[] Readers()
        => [.. typeof(AttachmentCapabilityOptions).Assembly
            .GetTypes()
            .Where(type => type != typeof(AttachmentCapability))
            .Where(Asks)
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)];

    private static bool Asks(Type type)
        => type
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter => parameter.ParameterType == typeof(AttachmentCapability)
                || parameter.ParameterType == typeof(IOptions<AttachmentCapabilityOptions>)
                || parameter.ParameterType == typeof(IOptionsSnapshot<AttachmentCapabilityOptions>)
                || parameter.ParameterType == typeof(IOptionsMonitor<AttachmentCapabilityOptions>));
}
