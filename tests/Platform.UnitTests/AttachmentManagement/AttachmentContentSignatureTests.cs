using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

namespace NotificationHub.UnitTests.AttachmentManagement;

public sealed class AttachmentContentSignatureTests
{
    [Theory]
    [InlineData("application/pdf", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37 })]
    [InlineData("image/gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })]
    [InlineData("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 })]
    [InlineData(
        "image/png",
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00 })]
    public void Leading_bytes_are_recognized_by_the_table_that_names_them(
        string expected,
        byte[] prefix)
        => AttachmentContentSignatures.Detect(prefix).ShouldBe(expected);

    [Fact]
    public void Bytes_no_signature_describes_are_left_unrecognized()
    {
        AttachmentContentSignatures.Detect("plain text, no signature"u8).ShouldBeNull();
        AttachmentContentSignatures.Detect([]).ShouldBeNull();

        // One byte short of the signature it looks like. Answering here would
        // let content that only starts like a known type be treated as one.
        AttachmentContentSignatures
            .Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A])
            .ShouldBeNull();
    }

    [Fact]
    public void A_declaration_is_read_as_its_media_type_and_not_as_what_was_written()
    {
        AttachmentContentSignatures.Canonical("APPLICATION/PDF").ShouldBe("application/pdf");
        AttachmentContentSignatures
            .Canonical("application/pdf; charset=utf-8")
            .ShouldBe("application/pdf");
        AttachmentContentSignatures.Canonical("not a media type").ShouldBeNull();
        AttachmentContentSignatures.Canonical(null).ShouldBeNull();
    }

    /// <summary>
    /// A tripwire, and it is written as one. Every format built on a container
    /// shares one prefix, so a table that named any of them would answer the
    /// same for all of them, and admitting one would silently admit the rest.
    /// The startup guard turns this into a refusal an operator can read.
    /// </summary>
    [Theory]
    [InlineData("application/zip")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("application/vnd.oasis.opendocument.text")]
    public void No_format_built_on_a_container_is_detectable(string mediaType)
        => AttachmentContentSignatures.Knows(mediaType).ShouldBeFalse();

    [Fact]
    public void The_table_recognizes_exactly_the_types_it_publishes()
    {
        AttachmentContentSignatures.Known.ShouldBe(
            ["application/pdf", "image/gif", "image/jpeg", "image/png"]);
        AttachmentContentSignatures.Knows("IMAGE/PNG").ShouldBeTrue();
        AttachmentContentSignatures.Knows("image/webp").ShouldBeFalse();
        AttachmentContentSignatures.Knows(null).ShouldBeFalse();
    }
}
