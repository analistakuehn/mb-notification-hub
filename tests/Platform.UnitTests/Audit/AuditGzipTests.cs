using System.Text;
using NotificationHub.Api.Modules.Audit.Domain;

namespace NotificationHub.UnitTests.Audit;

public sealed class AuditGzipTests
{
    [Fact]
    public void Compression_round_trips_the_exported_stream()
    {
        var content = Encoding.UTF8.GetBytes(
            string.Join('\n', Enumerable.Range(0, 500).Select(index => $$"""{"seq":{{index}}}""")));

        AuditGzip.Decompress(AuditGzip.Compress(content)).ShouldBe(content);
    }

    [Fact]
    public void The_same_content_always_compresses_to_the_same_object()
    {
        // The rerun of an export compares the digest of the compressed object
        // to decide whether to write; a compressor that embedded a timestamp
        // would make every rerun look like new evidence.
        var content = "linha de evidência"u8.ToArray();

        AuditGzip.Compress(content).ShouldBe(AuditGzip.Compress(content));
    }

    [Fact]
    public void An_empty_stream_still_produces_a_readable_object()
    {
        AuditGzip.Decompress(AuditGzip.Compress([])).ShouldBeEmpty();
    }
}
