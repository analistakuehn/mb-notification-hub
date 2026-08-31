using System.Reflection;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Stages;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.ArchTests;

/// <summary>
/// Coherence between the refusal catalog one module publishes and the map the
/// consuming module recognizes those refusals by. The producer answers its own
/// refusals with the bare word and the consumer compares the whole error text
/// against it, so the two sides are joined by a value and by nothing a compiler
/// checks: a member added on one side and not on the other compiles clean and
/// degrades where nobody is watching.
/// </summary>
/// <remarks>
/// <para>
/// The scope is stated here because the wrong reading of it disarms the next
/// review. What this proves is that every value of the published
/// render-refusal catalogs is canonical and survives the consumer's map
/// unchanged. What it does not prove, and cannot, is that the render only ever
/// emits published vocabulary. The render also answers with codes from the
/// producer's internal error vocabulary, which no published catalog carries,
/// and every one of them passes under these rules untouched, because these
/// rules read the catalogs and never read what the render emits. A rule that
/// announced the second thing would be worse than no rule at all.
/// </para>
/// <para>
/// The inventory at the bottom records the ones that were measured, as known
/// state rather than as absence. Deriving the whole set means comparing the
/// emission domain of the render against the domain the map discriminates,
/// plus the small third set of collapses somebody chose on purpose. That
/// comparison has not been made, so the inventory is a floor: a code missing
/// from it is unrecorded, never shown not to exist.
/// </para>
/// </remarks>
public sealed class RenderRefusalCatalogMappingTests
{
    /// <summary>
    /// The published catalogs whose members the render answers with. Named as
    /// types and never as values: every value is read off the type by
    /// reflection, so a member added later is judged without anyone editing
    /// this rule, which is the whole point of asking the catalog instead of
    /// keeping a copy of it here.
    /// </summary>
    private static readonly Type[] RenderRefusalCatalogs =
        [typeof(LayoutRejectionReasons), typeof(RenderedContentRejectionReasons)];

    /// <summary>
    /// The published catalog the render never answers with: it reports a
    /// template identity that rejects new requests, which is a decision taken
    /// before anything renders and travels on a lookup contract of its own. It
    /// is named so the census can account for every catalog of the surface,
    /// which is what stops a third one from being added and judged by nobody.
    /// </summary>
    private static readonly Type[] LookupRejectionCatalogs = [typeof(TemplateRejectionReasons)];

    /// <summary>
    /// Refusal codes the published render emits that no catalog above carries,
    /// with what the consumer's map answers for each of them today. Each one
    /// reaches the consumer wrapped in the module's error encoding rather than
    /// bare, so the map cannot recognize it in either form; the bare form is
    /// asserted below because it is the stronger of the two statements.
    /// <para>
    /// Repairing one of these is a decision about published vocabulary and
    /// belongs to whoever owns that catalog. Until it is taken, the degradation
    /// is recorded here rather than hidden by rules that pass over it.
    /// </para>
    /// </summary>
    private static readonly string[] UnpublishedRenderRefusals =
    [
        ErrorCodes.LayoutContentNotFound,
        ErrorCodes.LayoutNotFound,
        ErrorCodes.LayoutVersionNotFound,
        ErrorCodes.TemplateContentNotFound,
        ErrorCodes.UrlDomainNotAllowed,
        ErrorCodes.VariablesPayloadTooLarge,
        ErrorCodes.VariablesPayloadUnreadable,
    ];

    /// <summary>Suffix every rejection catalog of the published surface carries.</summary>
    private const string CatalogSuffix = "RejectionReasons";

    /// <summary>
    /// Namespace of the published surface, taken from a contract instead of
    /// spelled out, so moving the folder moves the rule with it.
    /// </summary>
    private static readonly string ContractNamespace = typeof(PublishedTemplate).Namespace!;

    [Fact]
    public void Every_published_render_refusal_belongs_to_the_canonical_rejection_catalog()
    {
        IReadOnlyList<PublishedRefusal> refusals = PublishedRenderRefusals();
        AssertEveryCatalogAnswered(refusals);

        var uncatalogued = refusals
            .Where(refusal => !NotificationRejectionReasons.IsCanonical(refusal.Value))
            .Select(refusal => refusal.Origin)
            .Order(StringComparer.Ordinal)
            .ToArray();

        uncatalogued.ShouldBeEmpty(
            "The producer publishes these refusals and the canonical catalog does not carry them, "
            + "so each one reaches a consumer as a value no reader can interpret: "
            + string.Join(", ", uncatalogued));
    }

    [Fact]
    public void Every_published_render_refusal_survives_the_consumer_map_unchanged()
    {
        IReadOnlyList<PublishedRefusal> refusals = PublishedRenderRefusals();
        AssertEveryCatalogAnswered(refusals);

        var collapsed = refusals
            .Where(refusal => !string.Equals(
                RenderStage.ReasonForFailedRender(refusal.Value),
                refusal.Value,
                StringComparison.Ordinal))
            .Select(refusal => $"{refusal.Origin} -> {RenderStage.ReasonForFailedRender(refusal.Value)}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        collapsed.ShouldBeEmpty(
            "The consumer's map does not answer these published refusals with themselves, so each "
            + "one reaches the producer of the notification as a generic render failure and the "
            + "diagnosis the refusal carried is gone: " + string.Join(", ", collapsed));
    }

    [Fact]
    public void A_reason_no_catalog_carries_collapses_into_the_generic_one()
    {
        // Falsification: the map has to be able to answer with something other
        // than its argument, or the rule above would hold for the identity
        // function and prove nothing about any catalog.
        RenderStage.ReasonForFailedRender("template-taken-by-aliens")
            .ShouldBe(RenderStage.ReasonRenderFailed);
        RenderStage.ReasonForFailedRender(null).ShouldBe(RenderStage.ReasonRenderFailed);
    }

    [Fact]
    public void The_census_reaches_every_rejection_catalog_of_the_published_surface()
    {
        var discovered = DiscoveredCatalogs();
        var classified = RenderRefusalCatalogs
            .Concat(LookupRejectionCatalogs)
            .Select(catalog => catalog.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        discovered.ShouldNotBeEmpty(
            "No rejection catalog was found on the published surface of this module. The scan is "
            + "reading the wrong namespace or the catalogs were renamed, and until that is fixed "
            + "every rule here passes over an empty set.");

        // Both directions: a catalog added to the surface and left unclassified
        // fails just as loudly as one classified here and since removed.
        discovered.ShouldBe(classified);
    }

    [Fact]
    public void The_recorded_refusals_the_render_emits_outside_the_catalog_still_degrade()
    {
        UnpublishedRenderRefusals.ShouldNotBeEmpty();

        foreach (var code in UnpublishedRenderRefusals)
        {
            NotificationRejectionReasons.IsCanonical(code).ShouldBeFalse(
                $"'{code}' reached the canonical catalog and this record still calls it unpublished.");

            var mapped = RenderStage.ReasonForFailedRender(code);
            string.Equals(mapped, RenderStage.ReasonRenderFailed, StringComparison.Ordinal).ShouldBeTrue(
                $"'{code}' is discriminated now and answers '{mapped}', so take it out of this record.");
        }
    }

    [Fact]
    public void Belonging_to_the_canonical_catalog_does_not_make_a_reason_discriminated()
    {
        // The case that closes the comfortable reading of the rules above. This
        // code is a member of the canonical catalog, the render emits it, and
        // the consumer's map still collapses it into the generic reason:
        // membership is not sufficient, and a rule that only checked membership
        // would prove less than its name suggests.
        NotificationRejectionReasons.IsCanonical(ErrorCodes.TemplateNotFound).ShouldBeTrue();

        var mapped = RenderStage.ReasonForFailedRender(ErrorCodes.TemplateNotFound);
        string.Equals(mapped, RenderStage.ReasonRenderFailed, StringComparison.Ordinal).ShouldBeTrue(
            $"the template lookup refusal answers '{mapped}' now, so this record is out of date.");
    }

    /// <summary>
    /// Every value the named catalogs declare, read off the metadata rather
    /// than off a copy, so the rules judge what the assembly actually carries.
    /// </summary>
    private static IReadOnlyList<PublishedRefusal> PublishedRenderRefusals()
        => [.. RenderRefusalCatalogs.SelectMany(catalog => catalog
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => new PublishedRefusal(
                catalog.Name,
                field.Name,
                (string)field.GetRawConstantValue()!)))];

    /// <summary>
    /// Every rejection catalog the published surface declares, found by shape
    /// instead of by a list, which is what lets the census contradict the
    /// classification above.
    /// </summary>
    private static string[] DiscoveredCatalogs()
        => [.. typeof(PublishedTemplate).Assembly
            .GetTypes()
            .Where(type => string.Equals(type.Namespace, ContractNamespace, StringComparison.Ordinal)
                && type.Name.EndsWith(CatalogSuffix, StringComparison.Ordinal))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Every catalog these rules judge has to have answered with at least one
    /// value. One that yields nothing makes the comparisons pass over an empty
    /// set, and the cheapest way to reach that is a member that stops being a
    /// public string constant.
    /// </summary>
    private static void AssertEveryCatalogAnswered(IReadOnlyList<PublishedRefusal> refusals)
    {
        foreach (Type catalog in RenderRefusalCatalogs)
        {
            refusals
                .Count(refusal => string.Equals(refusal.Catalog, catalog.Name, StringComparison.Ordinal))
                .ShouldBeGreaterThan(
                    0,
                    $"'{catalog.Name}' declared no published value, so every rule that reads it "
                    + "answers about nothing.");
        }
    }

    /// <summary>One published refusal, with where it was declared.</summary>
    private readonly record struct PublishedRefusal(string Catalog, string Member, string Value)
    {
        internal string Origin => $"{Catalog}.{Member}";
    }
}
