using NetArchTest.Rules;
using NotificationHub.Api.Modules.TemplateManagement.Features.ClassPolicies;
using NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;
using NotificationHub.Api.Modules.TemplateManagement.Features.Templates;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;

namespace NotificationHub.ArchTests;

/// <summary>
/// Every type that builds a memoized pointer key, held against a declared
/// set. Two kinds of type touch that builder and they carry opposite
/// obligations: a reader accepts an answer as stale as the pointer window
/// allows, and a lifecycle transition owes the invalidation of every key its
/// commit makes obsolete. A type that joins the set without a decision on
/// which of the two it is, is what this rule exists to make loud, and it is
/// the quieter half of the risk: a new key family is a deliberate act somebody
/// reviews, while a new reader of a family that already exists looks like
/// ordinary code.
/// </summary>
/// <remarks>
/// <para>
/// The reference set is declared here, in code, and not in the module guide.
/// These are internal implementation types, so a rename would leave prose
/// stale and turn the rule red for a rename instead of for a risk, while
/// <c>typeof</c> follows the rename and keeps failing only when the set really
/// changed. It also leaves one source of truth, so the rule cannot pass
/// because a heading in a document moved.
/// </para>
/// <para>
/// The observed side is read off the compiled assembly and not off the source,
/// which is what lets it see a call the compiler moved into an async state
/// machine: most of these calls live inside one. The builder is named by
/// <c>typeof</c> too, so a namespace this rule spells wrong cannot quietly
/// turn every direction green.
/// </para>
/// </remarks>
public sealed class PublishedPointerKeyInventoryTests
{
    /// <summary>
    /// The builder every memoized key of this surface goes through, named by
    /// the type and never by a string, so the scan cannot drift away from it.
    /// </summary>
    private static readonly Type Builder = typeof(PublishedPointerKeys);

    /// <summary>
    /// The lifecycle transitions that own an invalidation. Each one commits a
    /// change of published state and drops the pointers that change makes
    /// obsolete, in the process that committed it.
    /// </summary>
    private static readonly Type[] DeclaredTransitions =
    [
        typeof(PublishTemplateVersion.Handler),
        typeof(RollbackTemplate.Handler),
        typeof(DisableTemplate.Handler),
        typeof(DeprecateTemplate.Handler),
        typeof(DisableLayout.Handler),
        typeof(DeprecateLayout.Handler),
        typeof(PublishClassPolicyVersion.Handler),
    ];

    /// <summary>
    /// The readers that answer from a memoized pointer. Each one accepts an
    /// answer as stale as the pointer window allows, which is a decision about
    /// the traffic it serves and not a detail of how it reads.
    /// </summary>
    private static readonly Type[] DeclaredReaders =
    [
        typeof(PublishedCatalog),
        typeof(PublishedContextLoader),
        typeof(PublishedTemplateRenderer),
    ];

    private static readonly Type[] Declared = [.. DeclaredTransitions, .. DeclaredReaders];

    [Fact]
    public void Every_type_that_builds_a_pointer_key_is_declared_in_this_inventory()
    {
        Type[] observed = Observed();
        AssertBothSidesWereFound(observed);

        var undeclared = observed.Except(Declared).Select(Describe).Order(StringComparer.Ordinal).ToArray();
        undeclared.ShouldBeEmpty(
            "These types build a memoized pointer key and this inventory does not name them, so "
            + "nobody decided whether each of them reads a pointer and accepts its window, or "
            + "commits a change of published state and owes the invalidation: "
            + string.Join(", ", undeclared));
    }

    [Fact]
    public void Every_type_this_inventory_names_still_builds_a_pointer_key()
    {
        Type[] observed = Observed();
        AssertBothSidesWereFound(observed);

        var absent = Declared.Except(observed).Select(Describe).Order(StringComparer.Ordinal).ToArray();
        absent.ShouldBeEmpty(
            "This inventory names these types and none of them builds a pointer key any more, so it "
            + "records an obligation nobody has and buries the ones that are real: "
            + string.Join(", ", absent));
    }

    /// <summary>
    /// Neither side may be empty before comparing them means anything, and
    /// each guard covers the assertion that would otherwise pass in silence:
    /// an empty observed set makes the first one vacuous, an empty declared
    /// set makes the second one vacuous, and the cheapest way to reach either
    /// is an edit that looks harmless.
    /// </summary>
    private static void AssertBothSidesWereFound(Type[] observed)
    {
        observed.Length.ShouldBeGreaterThan(
            1,
            $"No type of this assembly was found building a key through '{Builder.Name}'. The scan "
            + "is reading the wrong assembly or the builder no longer survives compilation, and "
            + "until that is fixed this rule proves nothing.");

        Declared.Length.ShouldBeGreaterThan(
            1,
            "The declared set is empty, so a type that stopped building keys can no longer be "
            + "told from one this inventory never named.");
    }

    /// <summary>
    /// The types of the module's own assembly whose compiled body reaches the
    /// key builder, which is the only place a memoized key can be spelled.
    /// </summary>
    private static Type[] Observed()
        => [.. Types.InAssembly(Builder.Assembly)
            .That()
            .HaveDependencyOnAny(Builder.FullName!)
            .GetTypes()
            .Select(candidate => candidate.ReflectionType)];

    /// <summary>
    /// Seven of these types are a nested <c>Handler</c>, so the bare name says
    /// nothing about which one failed; the slice around it is the part a
    /// reader can act on.
    /// </summary>
    private static string Describe(Type type)
        => type.DeclaringType is null ? type.Name : $"{type.DeclaringType.Name}.{type.Name}";
}
