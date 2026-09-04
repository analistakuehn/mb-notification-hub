using System.Globalization;
using NotificationHub.PerformanceTests.ProviderTransfer;

namespace NotificationHub.IntegrationTests.ProviderTransfer;

/// <summary>
/// The double answers what a provider is entitled to answer, and the arm reads
/// each answer for what it is. Nothing here decides a retry policy: the probe
/// only has to be able to tell a refusal from a stall from a dropped
/// connection, or a failed run would read as a slow one.
/// </summary>
public sealed class ProviderAnswerClassificationTests
{
    [Fact]
    public async Task A_provider_that_accepts_the_message_answers_with_its_identifier()
    {
        await using ProviderCaptureServer server = await ProviderCaptureServer.StartAsync(CancellationToken.None);
        using var client = new HttpClient { BaseAddress = server.BaseAddress };

        TransferAttempt attempt = await SendAsync(client);

        attempt.Classification.ShouldBe("accepted");
        attempt.StatusCode.ShouldBe(202);
        attempt.ProviderMessageId.ShouldBe("probe-message");
        server.CallCount.ShouldBe(1);
        CapturedMailSend captured = server.Calls.ShouldHaveSingleItem();
        captured.Path.ShouldBe("/v3/mail/send");
        captured.Method.ShouldBe("POST");
        captured.Authorization.ShouldBe("Bearer probe-api-key");
        captured.ContentType.ShouldBe("application/json; charset=utf-8");
        captured.BodyIsWellFormedJson.ShouldBe(true);
    }

    [Theory]
    [InlineData(400, "rejected")]
    [InlineData(429, "throttled")]
    [InlineData(500, "transient")]
    public async Task The_arm_reads_the_answer_the_provider_gave(int statusCode, string classification)
    {
        await using ProviderCaptureServer server = await ProviderCaptureServer.StartAsync(CancellationToken.None);
        using var client = new HttpClient { BaseAddress = server.BaseAddress };
        server.Answer = _ => statusCode switch
        {
            400 => ProviderAnswer.Reject(),
            429 => ProviderAnswer.Throttle(TimeSpan.FromSeconds(7)),
            _ => ProviderAnswer.ServerFault(),
        };

        TransferAttempt attempt = await SendAsync(client);

        attempt.StatusCode.ShouldBe(statusCode);
        attempt.Classification.ShouldBe(classification);
        server.Calls.ShouldHaveSingleItem().BodyBytes.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_provider_that_never_answers_within_the_patience_of_the_client_is_a_timeout()
    {
        await using ProviderCaptureServer server = await ProviderCaptureServer.StartAsync(CancellationToken.None);
        using var client = new HttpClient
        {
            BaseAddress = server.BaseAddress,
            Timeout = TimeSpan.FromSeconds(1),
        };
        server.Answer = _ => ProviderAnswer.Stall(TimeSpan.FromSeconds(30));

        TransferAttempt attempt = await SendAsync(client);

        attempt.Classification.ShouldBe("timeout");
        attempt.StatusCode.ShouldBe(0);
    }

    [Fact]
    public async Task A_provider_that_drops_the_connection_is_a_network_fault()
    {
        await using ProviderCaptureServer server = await ProviderCaptureServer.StartAsync(CancellationToken.None);
        using var client = new HttpClient { BaseAddress = server.BaseAddress };
        server.Answer = _ => ProviderAnswer.Drop(0);

        TransferAttempt attempt = await SendAsync(client);

        attempt.Classification.ShouldBe("network");
        attempt.StatusCode.ShouldBe(0);
        server.CallCount.ShouldBe(1);
        server.Calls.ShouldBeEmpty();
    }

    private static async Task<TransferAttempt> SendAsync(HttpClient client)
    {
        var source = new SyntheticAttachmentByteSource(
            32 * 1_024, "comprovante-0.pdf", "application/pdf", 8 * 1_024, TimeSpan.Zero);
        var root = Path.Combine(
            Path.GetTempPath(),
            $"notification-hub-answer-test-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}");
        Directory.CreateDirectory(root);
        try
        {
            using var interrupter = TransferInterrupter.Idle(CancellationToken.None);
            var plan = new TransferPlan(
                ProviderTransferArms.StreamingArm,
                MailSendEnvelope.Default,
                [source],
                "probe-api-key",
                root,
                true,
                interrupter);
            return await ProviderTransferArms.SendAsync(client, plan, interrupter.Token);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
