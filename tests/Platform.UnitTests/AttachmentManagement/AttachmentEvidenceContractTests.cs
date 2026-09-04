using System.Reflection;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

namespace NotificationHub.UnitTests.AttachmentManagement;

/// <summary>
/// The shape of the evidence this module publishes about accepted content.
/// <para>
/// Two rules and they pull in opposite directions. The proof of the bytes has
/// to be here, because this is the one surface an auditor reaches and a digest
/// nobody can read proves nothing; the way to the bytes must not be, because a
/// coordinate is capacity to fetch content rather than proof of it. A member
/// list is the only place both can be checked at once, and the rendering is
/// where the first rule turns against itself: a record prints everything it
/// has, so the value that belongs in an authorized answer is one interpolation
/// away from an operational line.
/// </para>
/// </summary>
public sealed class AttachmentEvidenceContractTests
{
    /// <summary>
    /// Values distinctive enough that finding one in a rendering is a finding
    /// and never a coincidence.
    /// </summary>
    private const string DigestProbe = "d1e5735a4c0f4a4d9b7f2c8e6a1b3d5f7091a2b3c4d5e6f708192a3b4c5d6e7f";

    private const string HandleProbe = "aci_2f7c1d0a8b3e4f5a9c6d7e8f0a1b2c3d";

    private const string DetailProbe = "probe-validation-detail";

    /// <summary>
    /// The whole published surface, named once. Adding a member without adding
    /// it here fails, which is the point: the members that must not exist are
    /// the ones nobody would think to write a test about, and a store, a key or
    /// a generation of the provider would arrive exactly that way.
    /// </summary>
    [Fact]
    public void The_published_evidence_names_its_members_and_no_coordinate_among_them()
    {
        var members = typeof(AttachmentEvidence)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(member => member.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        members.ShouldBe(
        [
            "Application",
            "CapturedAt",
            "ContentIdentity",
            "DetectedContentType",
            "Digest",
            "DigestAlgorithm",
            "DigestedLengthBytes",
            "Reference",
            "ReleasedAt",
            "RevocationReason",
            "RevokedAt",
            "State",
            "ValidationDetail",
        ]);
    }

    /// <summary>
    /// The rendering of a record is the free surface: any interpolation of the
    /// value in a log line prints it. The digest is the member that must not
    /// travel that way, and it is the one this asserts against, because it is
    /// the only member here that a copy of turns into a fingerprint of the
    /// exact bytes anybody can compare against content they hold.
    /// </summary>
    [Fact]
    public void The_rendering_carries_the_handle_and_neither_the_digest_nor_the_verdict()
    {
        var rendered = Evidence().ToString();

        rendered.ShouldBe(AttachmentEvidence.Redacted + " " + HandleProbe);
        rendered.Contains(DigestProbe, StringComparison.Ordinal).ShouldBeFalse(
            "o resumo criptográfico do conteúdo não pode sair numa renderização de texto.");
        rendered.Contains(DetailProbe, StringComparison.Ordinal).ShouldBeFalse(
            "o detalhe da validação é registro durável, não texto de linha operacional.");
    }

    /// <summary>
    /// The probes above only mean something if the rendering is what carries
    /// them when nothing suppresses it. The default rendering of the same
    /// value is asked here, over a member set built the same way, so the two
    /// assertions above are a difference rather than a sentence about a value
    /// that never appears anywhere.
    /// </summary>
    [Fact]
    public void The_default_rendering_of_the_same_members_would_have_carried_them()
    {
        AttachmentEvidence evidence = Evidence();
        var unsuppressed = string.Join(
            ", ",
            typeof(AttachmentEvidence)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(member => member.Name + " = " + member.GetValue(evidence)));

        unsuppressed.Contains(DigestProbe, StringComparison.Ordinal).ShouldBeTrue();
        unsuppressed.Contains(DetailProbe, StringComparison.Ordinal).ShouldBeTrue();
    }

    private static AttachmentEvidence Evidence()
        => new()
        {
            ContentIdentity = HandleProbe,
            Reference = "att_2f7c1d0a8b3e4f5a9c6d7e8f0a1b2c3d",
            Application = "billing",
            State = "released",
            ValidationDetail = DetailProbe,
            DigestAlgorithm = "sha-256",
            Digest = DigestProbe,
            DigestedLengthBytes = 2048,
            DetectedContentType = "application/pdf",
            CapturedAt = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero),
            ReleasedAt = new DateTimeOffset(2026, 8, 25, 12, 5, 0, TimeSpan.Zero),
        };
}
