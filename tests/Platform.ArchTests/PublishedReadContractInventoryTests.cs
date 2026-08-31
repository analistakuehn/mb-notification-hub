using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.TemplateManagement;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.ArchTests;

/// <summary>
/// The in-process read surface of the template context, held against the one
/// document an agent reads before touching that boundary. The module guide
/// claims a sibling reads this context exclusively through the contracts it
/// enumerates, and that word makes an omission worse than silence: a contract
/// the module registers and the guide leaves out reads as surface nobody
/// sanctioned, and the next reader removes it or routes around it, taking the
/// consumer that depended on it with it.
/// </summary>
/// <remarks>
/// <para>
/// The registered set is taken from the container the module actually builds,
/// never from a literal list here, so this rule cannot agree with itself. That
/// choice also decides the harder question the boundary poses, which is which
/// contracts belong in the enumeration at all. An inverted contract is
/// declared by this module and implemented and registered by the consumer that
/// depends on it, so the owner never registers it and it falls out of the set
/// by construction, with no exception list to maintain and no chance of a
/// stale one.
/// </para>
/// <para>
/// A count of the interfaces declared under the contract namespace would be
/// the wrong predicate for the same reason: it would demand that an inverted
/// contract appear in a section about how a sibling reads this context, which
/// is a sentence nobody should write, because reading is not what that
/// contract does.
/// </para>
/// </remarks>
public sealed class PublishedReadContractInventoryTests
{
    /// <summary>
    /// The module guide, named here rather than discovered, because the rule
    /// claims that this one document carries the enumeration.
    /// </summary>
    private static readonly string[] GuideSegments =
        ["src", "Platform.Api", "Modules", "TemplateManagement", "AGENTS.md"];

    /// <summary>
    /// Namespace the contracts live in, taken from a contract instead of
    /// spelled out, so moving the folder moves the rule with it.
    /// </summary>
    private static readonly string ContractNamespace = typeof(IPublishedCatalog).Namespace!;

    /// <summary>Heading of the section that carries the enumeration.</summary>
    private const string SectionHeader = "## Published read contracts";

    /// <summary>
    /// Opening clause of the one bullet inside that section that enumerates
    /// the contracts. The bullet is located by this clause instead of by a
    /// line range or by a position in the list, so editing the prose around it
    /// cannot move the rule, and rewriting the clause fails these tests loudly
    /// instead of quietly matching nothing.
    /// </summary>
    private const string EnumerationAnchor = "registered by `TemplateManagementModule`:";

    [Fact]
    public void Every_in_process_contract_the_module_registers_is_named_in_the_guide()
    {
        HashSet<string> registered = RegisteredContracts();
        HashSet<string> documented = DocumentedContracts();

        AssertBothSidesWereFound(registered, documented);

        var undocumented = registered.Except(documented).Order(StringComparer.Ordinal).ToArray();
        undocumented.ShouldBeEmpty(
            "The module registers these contracts and the guide does not name them, so a reader "
            + "of the guide concludes they are unsanctioned surface: "
            + string.Join(", ", undocumented));
    }

    [Fact]
    public void Every_contract_the_guide_names_is_registered_by_the_module()
    {
        HashSet<string> registered = RegisteredContracts();
        HashSet<string> documented = DocumentedContracts();

        AssertBothSidesWereFound(registered, documented);

        var unregistered = documented.Except(registered).Order(StringComparer.Ordinal).ToArray();
        unregistered.ShouldBeEmpty(
            "The guide names these contracts and the module no longer registers them, so it sends "
            + "a consumer to a surface that is gone: " + string.Join(", ", unregistered));
    }

    /// <summary>
    /// Both sides have to be found before comparing them means anything. An
    /// empty set on either side makes the comparisons above pass for the wrong
    /// reason, and the cheapest way to reach one is a rename: of the section
    /// heading, of the anchoring clause, or of the registration itself.
    /// </summary>
    private static void AssertBothSidesWereFound(
        HashSet<string> registered,
        HashSet<string> documented)
    {
        registered.Count.ShouldBeGreaterThan(
            1,
            "No in-process contract of this context was found in the module's own container. "
            + "The scan is looking at the wrong namespace or the registrations moved, and until "
            + "that is fixed this rule proves nothing.");

        documented.Count.ShouldBeGreaterThan(
            1,
            $"The enumeration was not found in the guide. Either the '{SectionHeader}' heading or "
            + $"the clause '{EnumerationAnchor}' was renamed, and this rule cannot read a section "
            + "it cannot locate.");
    }

    /// <summary>
    /// The contracts of this context that the module puts in the container,
    /// read off the descriptors the module produces for an empty
    /// configuration. Only a service type that is an interface of the contract
    /// namespace counts: a concrete registration is an internal collaborator,
    /// and an interface from anywhere else is somebody else's boundary.
    /// </summary>
    private static HashSet<string> RegisteredContracts()
    {
        var services = new ServiceCollection();
        TemplateManagementModule.ConfigureServices(services, new ConfigurationBuilder().Build());

        var contracts = new HashSet<string>(StringComparer.Ordinal);
        foreach (ServiceDescriptor descriptor in services)
        {
            Type serviceType = descriptor.ServiceType;
            if (serviceType.IsInterface
                && string.Equals(serviceType.Namespace, ContractNamespace, StringComparison.Ordinal))
            {
                contracts.Add(serviceType.Name);
            }
        }

        return contracts;
    }

    /// <summary>
    /// The contracts the guide enumerates, taken from the code spans of the
    /// anchored bullet. A span counts when it is shaped like the name of an
    /// interface, which is what every entry of the enumeration is and what no
    /// other span in that bullet is.
    /// </summary>
    private static HashSet<string> DocumentedContracts()
    {
        var contracts = new HashSet<string>(StringComparer.Ordinal);
        if (EnumerationBullet() is not string bullet)
        {
            return contracts;
        }

        var spans = bullet.Split('`');
        for (var index = 1; index < spans.Length; index += 2)
        {
            var span = spans[index];
            if (LooksLikeContractName(span))
            {
                contracts.Add(span);
            }
        }

        return contracts;
    }

    private static string? EnumerationBullet()
        => GuideEnumeration.Bullet(GuidePath(), SectionHeader, EnumerationAnchor);

    private static bool LooksLikeContractName(string span)
        => span.Length > 1
            && span[0] == 'I'
            && char.IsUpper(span[1])
            && span.All(char.IsLetterOrDigit);

    private static string GuidePath()
        => Path.Combine([FindSolutionRoot(), .. GuideSegments]);

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the solution root.");
    }
}
