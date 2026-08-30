using System.Globalization;

namespace NotificationHub.UnitTests.Infrastructure;

/// <summary>
/// The two globalization settings this repository is pinned to, asserted in
/// code because neither of them fails loudly when it is dropped: the build
/// keeps succeeding, every other test keeps passing, and the only difference
/// is text that quietly starts depending on the host.
/// </summary>
public sealed class RuntimeGlobalizationTests
{
    /// <summary>
    /// Well formed under BCP 47 and defined by nobody. The locale validation of
    /// the template module accepts the shape, not a list of known cultures, so
    /// a tag like this reaches the runtime.
    /// </summary>
    private const string UndefinedTag = "qq-QQ";

    [Fact]
    public void A_culture_this_runtime_does_not_know_throws_instead_of_being_invented()
    {
        // Without PredefinedCulturesOnly this call answers a culture with LCID
        // 4096 that formats under the operating system locale on Windows and
        // under the ICU root on Linux. It never answers the invariant culture,
        // which is what a reader assumes an unknown tag falls back to, and
        // nothing anywhere reports that it happened.
        Should.Throw<CultureNotFoundException>(() => CultureInfo.GetCultureInfo(UndefinedTag));
        Should.Throw<CultureNotFoundException>(() => new CultureInfo(UndefinedTag));
    }

    [Fact]
    public void A_culture_this_runtime_knows_still_resolves()
    {
        // The other half of the setting, and the reason it is not the same as
        // turning globalization off: real cultures keep working, and they keep
        // coming from ICU.
        CultureInfo culture = CultureInfo.GetCultureInfo("pt-BR");

        culture.Name.ShouldBe("pt-BR");
        culture.NumberFormat.NumberDecimalSeparator.ShouldBe(",");
    }

    [Fact]
    public void Globalization_is_not_running_in_invariant_mode()
    {
        // InvariantGlobalization is forbidden here, and this is what says so at
        // run time. Under it, accent composition becomes a silent no-op and the
        // normalization the output policy runs first starts reporting success
        // over text it never normalized: a visible defect traded for an
        // invisible one. The four tests that fail under that switch are kept
        // failing on purpose, because they are the only free oracle this
        // repository has for the ICU dependency of the output path.
        AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant);
        invariant.ShouldBeFalse();

        // And the observable consequence, so that the assertion above is not
        // the only thing standing between the repository and the switch: under
        // invariant mode every culture collapses onto the invariant one.
        CultureInfo.GetCultureInfo("pt-BR").NumberFormat.NumberDecimalSeparator
            .ShouldNotBe(CultureInfo.InvariantCulture.NumberFormat.NumberDecimalSeparator);
    }
}
