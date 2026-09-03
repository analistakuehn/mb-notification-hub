using System.Security.Cryptography;
using System.Text;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

namespace NotificationHub.UnitTests.AttachmentManagement;

public sealed class AttachmentContentProofTests
{
    private static readonly byte[] Digest = SHA256.HashData("custody bytes"u8.ToArray());

    [Fact]
    public void Two_proofs_over_the_same_bytes_match()
    {
        AttachmentContentProof recorded = AttachmentContentProof.Sha256Of(Digest, 13);
        AttachmentContentProof measured = AttachmentContentProof.Sha256Of(
            SHA256.HashData(Encoding.UTF8.GetBytes("custody bytes")),
            13);

        recorded.Matches(measured).ShouldBeTrue();
    }

    [Fact]
    public void A_single_differing_digest_byte_does_not_match()
    {
        var altered = (byte[])Digest.Clone();
        altered[^1] ^= 0x01;

        AttachmentContentProof.Sha256Of(Digest, 13)
            .Matches(AttachmentContentProof.Sha256Of(altered, 13))
            .ShouldBeFalse();
    }

    [Fact]
    public void A_differing_length_does_not_match()
        => AttachmentContentProof.Sha256Of(Digest, 13)
            .Matches(AttachmentContentProof.Sha256Of(Digest, 14))
            .ShouldBeFalse();

    [Fact]
    public void The_same_digest_under_another_algorithm_does_not_match()
        => AttachmentContentProof.Sha256Of(Digest, 13)
            .Matches(AttachmentContentProof.Recorded("sha-512", Digest, 13))
            .ShouldBeFalse();

    [Fact]
    public void Rendering_a_proof_as_text_names_the_algorithm_and_the_length_and_nothing_else()
    {
        // Asserting the exact rendering is what makes this fail when the
        // override goes away. Asking whether a ten-character rendering
        // contains a sixty-four-character digest cannot fail whatever the
        // rendering is, and the rendering a record synthesizes carries no
        // digest either, because every member here is internal.
        var rendered = $"{AttachmentContentProof.Sha256Of(Digest, 13)}";

        rendered.ShouldBe($"{AttachmentContentProof.Sha256}/13");
    }

    [Fact]
    public void Two_proofs_over_byte_identical_digests_in_different_arrays_are_one_value()
    {
        var first = SHA256.HashData(Encoding.UTF8.GetBytes("custody bytes"));
        var second = SHA256.HashData(Encoding.UTF8.GetBytes("custody bytes"));

        // The two digests are byte identical and live in different arrays, so
        // a comparison by memory segment answers no to every line below.
        ReferenceEquals(first, second).ShouldBeFalse();
        Convert.ToHexString(first).ShouldBe(Convert.ToHexString(second));
        AttachmentContentProof recorded = AttachmentContentProof.Sha256Of(first, 13);
        AttachmentContentProof measured = AttachmentContentProof.Sha256Of(second, 13);

        (recorded == measured).ShouldBeTrue();
        recorded.Equals(measured).ShouldBeTrue();
        recorded.Equals((object)measured).ShouldBeTrue();
        recorded.GetHashCode().ShouldBe(measured.GetHashCode());
        HashSet<AttachmentContentProof> distinct = [recorded, measured];
        distinct.Count.ShouldBe(1);
    }

    [Fact]
    public void A_proof_over_other_bytes_is_another_value()
    {
        var altered = (byte[])Digest.Clone();
        altered[^1] ^= 0x01;
        AttachmentContentProof recorded = AttachmentContentProof.Sha256Of(Digest, 13);
        AttachmentContentProof other = AttachmentContentProof.Sha256Of(altered, 13);

        (recorded == other).ShouldBeFalse();
        recorded.Equals(other).ShouldBeFalse();
        HashSet<AttachmentContentProof> distinct = [recorded, other];
        distinct.Count.ShouldBe(2);
    }
}
