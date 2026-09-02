using NotificationHub.PerformanceTests.Gate;
using NotificationHub.PerformanceTests.ProviderTransfer;

namespace NotificationHub.PerformanceTests.Scenarios;

/// <summary>Size, count and content of one cell of the comparison matrix.</summary>
internal sealed record ProviderTransferCorpus(
    long AttachmentBytes,
    int AttachmentCount,
    AttachmentContentShape ContentShape);

/// <summary>
/// The corpora the comparison is run over. All four sit inside the ratified
/// envelope of five attachments and seven mebibytes of raw content, and each
/// one exists to separate something the others cannot.
/// <list type="bullet">
/// <item>the floor and the single maximum are twenty-eight times apart, which
/// is what turns a ceiling on allocation from one number into a line;</item>
/// <item>fragmentation carries the same seven mebibytes in five attachments,
/// which separates a cost paid per item from a cost paid per byte;</item>
/// <item>the adversarial corpus is the same maximum in content whose base64 is
/// nothing but characters the default JSON encoder escapes. It is the only one
/// that tells a body composed by the right writer call from a body composed by
/// the call a sender can make eight times longer.</item>
/// </list>
/// </summary>
internal static class ProviderTransferProfiles
{
    internal const string Floor = "floor";

    internal const string MaxSingle = "max-single";

    internal const string Fragmented = "fragmented";

    internal const string Adversarial = "adversarial";

    internal const string Custom = "custom";

    /// <summary>A quarter of a mebibyte, the floor of the size axis.</summary>
    private const long FloorBytes = 256 * 1_024;

    /// <summary>
    /// Five attachments that add up to the ratified total. Seven mebibytes do
    /// not divide by five, so each one carries the floor of the quotient and
    /// the corpus lands two bytes under the ceiling rather than three over it.
    /// </summary>
    private const long FragmentBytes = ProviderTransferBudget.MaxTotalRawAttachmentBytes / 5;

    internal static IReadOnlyList<string> All => [Floor, MaxSingle, Fragmented, Adversarial];

    internal static ProviderTransferCorpus Of(string profileId) => profileId switch
    {
        Floor => new ProviderTransferCorpus(FloorBytes, 1, AttachmentContentShape.Readable),
        MaxSingle => new ProviderTransferCorpus(
            ProviderTransferBudget.MaxTotalRawAttachmentBytes, 1, AttachmentContentShape.Readable),
        Fragmented => new ProviderTransferCorpus(FragmentBytes, 5, AttachmentContentShape.Readable),
        Adversarial => new ProviderTransferCorpus(
            ProviderTransferBudget.MaxTotalRawAttachmentBytes, 1, AttachmentContentShape.Escapable),
        _ => throw new ArgumentException(
            $"Perfil de transferência desconhecido: {profileId}. Use floor, max-single, fragmented ou adversarial.",
            nameof(profileId)),
    };
}
