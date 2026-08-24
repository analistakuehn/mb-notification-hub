using NotificationHub.Platform.GoLiveChecks;

namespace NotificationHub.UnitTests.GoLive;

public sealed class ReceiptWriterTests
{
    [Fact]
    public async Task Unavailable_source_keeps_null_count_and_omits_unverified_identity()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "receipt.json");
        var receipt = new GoLiveReceipt(
            "2026-08-24T15:30:00.0000000+00:00",
            GoLiveStatuses.Error,
            [new GoLiveSourceReceipt(GoLiveSourceIdentifiers.MicrosoftGraph, null)],
            [GoLiveReasons.SourceUnavailable(GoLiveSourceIdentifiers.MicrosoftGraph)]);

        try
        {
            await new FileReceiptWriter().WriteAsync(path, receipt, CancellationToken.None);

            var json = await File.ReadAllTextAsync(path, CancellationToken.None);

            json.ShouldContain("\"count\": null");
            json.ShouldNotContain("verifiedIdentity");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Receipt_json_is_written_in_a_deterministic_shape()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "nested", "receipt.json");
        var receipt = new GoLiveReceipt(
            "2026-08-24T15:30:00.0000000+00:00",
            GoLiveStatuses.Fail,
            [
                new GoLiveSourceReceipt(GoLiveSourceIdentifiers.TemplateManagement, 3),
                new GoLiveSourceReceipt(GoLiveSourceIdentifiers.MicrosoftGraph, 0),
            ],
            [GoLiveReasons.PublishedOperationalTemplatesPresent]);

        try
        {
            await new FileReceiptWriter().WriteAsync(path, receipt, CancellationToken.None);

            var json = await File.ReadAllTextAsync(path, CancellationToken.None);

            json.ShouldBe("""
                {
                  "timestamp": "2026-08-24T15:30:00.0000000+00:00",
                  "status": "fail",
                  "sources": [
                    {
                      "identifier": "template-management.published-operational-templates",
                      "count": 3
                    },
                    {
                      "identifier": "microsoft-graph.operational-role-assignments",
                      "count": 0
                    }
                  ],
                  "reasons": [
                    "published-operational-templates-present"
                  ]
                }

                """);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
