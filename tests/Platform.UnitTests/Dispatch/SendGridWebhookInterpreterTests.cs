using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.Dispatch;

public sealed class SendGridWebhookInterpreterTests
{
    private const string NotificationId = "3f2b1c44-9a7e-4c1d-8f10-2b6e5d9a7c31";
    private const string AttemptId = "7c9d0e21-4b83-4a6f-9d52-1e8a3f6c0b47";

    private const string Batch = """
        [
          {"sg_event_id":"evt-1","event":"processed","timestamp":1787923200,"sg_message_id":"msg-1"},
          {"sg_event_id":"evt-2","event":"delivered","timestamp":1787923260,"sg_message_id":"msg-1"},
          {"sg_event_id":"evt-3","event":"open","timestamp":1787923320,"sg_message_id":"msg-1"}
        ]
        """;

    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Accepts_a_callback_signed_with_the_configured_verification_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions
        {
            PublicKey = PublicKeyOf(key),
        });

        Result<VerifiedProviderWebhook> result = interpreter.Verify(Request(key, Batch, Now));

        result.IsSuccess.ShouldBeTrue(result.Error);
        result.Value!.ProviderKey.ShouldBe("sendgrid");
        result.Value.VerifiedAt.ShouldBe(Now);
    }

    [Fact]
    public void Refuses_a_callback_whose_body_changed_after_it_was_signed()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions
        {
            PublicKey = PublicKeyOf(key),
        });
        ProviderWebhookRequest signed = Request(key, Batch, Now);
        ProviderWebhookRequest tampered = signed with
        {
            Body = Encoding.UTF8.GetBytes(Batch.Replace("evt-1", "evt-9", StringComparison.Ordinal)),
        };

        Result<VerifiedProviderWebhook> result = interpreter.Verify(tampered);

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.SignatureInvalid).ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Refuses_a_callback_signed_by_a_key_that_is_not_the_configured_one()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var configured = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions
        {
            PublicKey = PublicKeyOf(configured),
        });

        Result<VerifiedProviderWebhook> result = interpreter.Verify(Request(signer, Batch, Now));

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.SignatureInvalid).ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Refuses_every_callback_while_the_verification_key_is_absent()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions());

        Result<VerifiedProviderWebhook> result = interpreter.Verify(Request(key, Batch, Now));

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.SignatureInvalid).ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Refuses_an_authentic_callback_whose_timestamp_left_the_window()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions
        {
            PublicKey = PublicKeyOf(key),
            TimestampWindowSeconds = 600,
        });

        Result<VerifiedProviderWebhook> result = interpreter.Verify(
            Request(key, Batch, Now.AddSeconds(-601)));

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.TimestampOutOfWindow)
            .ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Accepts_an_authentic_callback_whose_timestamp_is_inside_the_window()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions
        {
            PublicKey = PublicKeyOf(key),
            TimestampWindowSeconds = 600,
        });

        Result<VerifiedProviderWebhook> result = interpreter.Verify(
            Request(key, Batch, Now.AddSeconds(-599)));

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Refuses_a_callback_that_carries_no_timestamp_header()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions
        {
            PublicKey = PublicKeyOf(key),
        });
        ProviderWebhookRequest signed = Request(key, Batch, Now);
        ProviderWebhookRequest stripped = signed with
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["X-Twilio-Email-Event-Webhook-Signature"] =
                    signed.Headers["X-Twilio-Email-Event-Webhook-Signature"],
            },
        };

        Result<VerifiedProviderWebhook> result = interpreter.Verify(stripped);

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.TimestampOutOfWindow)
            .ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Refuses_a_callback_from_an_address_outside_the_configured_allowlist()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions
        {
            PublicKey = PublicKeyOf(key),
            AllowedIpPrefixes = ["168.245."],
        });

        Result<VerifiedProviderWebhook> result = interpreter.Verify(
            Request(key, Batch, Now, "203.0.113.9"));

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.OriginNotAllowed).ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Accepts_any_address_while_the_allowlist_is_empty()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions
        {
            PublicKey = PublicKeyOf(key),
            AllowedIpPrefixes = [],
        });

        Result<VerifiedProviderWebhook> result = interpreter.Verify(
            Request(key, Batch, Now, "203.0.113.9"));

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Produces_one_canonical_event_for_every_entry_of_a_batch()
    {
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions());

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = interpreter.Interpret(Verified(Batch));

        result.IsSuccess.ShouldBeTrue(result.Error);
        result.Value!.Count.ShouldBe(3);
        result.Value.Select(entry => entry.ProviderEventId).ShouldBe(["evt-1", "evt-2", "evt-3"]);
        result.Value.Select(entry => entry.Kind).ShouldBe(
        [
            DeliveryFeedbackKind.Sent,
            DeliveryFeedbackKind.Delivered,
            DeliveryFeedbackKind.Read,
        ]);
        result.Value[0].OccurredAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1787923200));
        result.Value[0].ProviderMessageId.ShouldBe("msg-1");
    }

    [Fact]
    public void Reports_a_definitive_bounce_as_a_hard_bounce()
    {
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions());

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = interpreter.Interpret(Verified(
            """[{"sg_event_id":"evt-b","event":"bounce","type":"bounce","status":"5.1.1"}]"""));

        result.Value![0].Kind.ShouldBe(DeliveryFeedbackKind.Bounced);
        result.Value[0].Signal.ShouldBe(SuppressionSignal.HardBounce);
        result.Value[0].ErrorCode.ShouldBe("5.1.1");
    }

    [Fact]
    public void Reports_a_transient_bounce_as_no_signal()
    {
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions());

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = interpreter.Interpret(Verified(
            """[{"sg_event_id":"evt-s","event":"bounce","type":"blocked","status":"4.2.2"}]"""));

        result.Value![0].Kind.ShouldBe(DeliveryFeedbackKind.Bounced);
        result.Value[0].Signal.ShouldBe(SuppressionSignal.None);
    }

    [Fact]
    public void Reads_the_definitive_vocabulary_from_configuration_rather_than_from_the_assembly()
    {
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions
        {
            HardBounceCodes = ["blocked"],
            InvalidDestinationCodes = [],
        });

        Result<IReadOnlyList<ProviderDeliveryEvent>> blocked = interpreter.Interpret(Verified(
            """[{"sg_event_id":"evt-s","event":"bounce","type":"blocked"}]"""));
        Result<IReadOnlyList<ProviderDeliveryEvent>> bounced = interpreter.Interpret(Verified(
            """[{"sg_event_id":"evt-b","event":"bounce","type":"bounce"}]"""));

        blocked.Value![0].Signal.ShouldBe(SuppressionSignal.HardBounce);
        bounced.Value![0].Signal.ShouldBe(SuppressionSignal.None);
    }

    [Fact]
    public void Reports_a_drop_that_accuses_the_address_as_an_invalid_destination()
    {
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions());

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = interpreter.Interpret(Verified(
            """[{"sg_event_id":"evt-d","event":"dropped","reason":"Invalid SMTP"}]"""));

        result.Value![0].Kind.ShouldBe(DeliveryFeedbackKind.Bounced);
        result.Value[0].Signal.ShouldBe(SuppressionSignal.InvalidDestination);
        result.Value[0].ErrorCode.ShouldBe("Invalid SMTP");
    }

    [Fact]
    public void Reports_a_drop_that_blames_the_provider_as_a_plain_failure()
    {
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions());

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = interpreter.Interpret(Verified(
            """[{"sg_event_id":"evt-d","event":"dropped","reason":"Sandbox Mode"}]"""));

        result.Value![0].Kind.ShouldBe(DeliveryFeedbackKind.Failed);
        result.Value[0].Signal.ShouldBe(SuppressionSignal.None);
    }

    [Fact]
    public void Reports_a_blocked_message_as_a_plain_failure()
    {
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions());

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = interpreter.Interpret(Verified(
            """[{"sg_event_id":"evt-x","event":"blocked","status":"4.7.1"}]"""));

        result.Value![0].Kind.ShouldBe(DeliveryFeedbackKind.Failed);
        result.Value[0].Signal.ShouldBe(SuppressionSignal.None);
    }

    [Fact]
    public void Reads_the_correlation_identifiers_the_provider_echoes_back()
    {
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions());

        Result<IReadOnlyList<ProviderDeliveryEvent>> flattened = interpreter.Interpret(Verified(
            $$$"""
            [{"sg_event_id":"evt-1","event":"delivered",
              "notification_id":"{{{NotificationId}}}","attempt_id":"{{{AttemptId}}}"}]
            """));
        Result<IReadOnlyList<ProviderDeliveryEvent>> nested = interpreter.Interpret(Verified(
            $$$"""
            [{"sg_event_id":"evt-2","event":"delivered",
              "custom_args":{"notification_id":"{{{NotificationId}}}","attempt_id":"{{{AttemptId}}}"}}]
            """));

        flattened.Value![0].Correlation.ShouldBe(
            new DispatchCorrelation(Guid.Parse(NotificationId), Guid.Parse(AttemptId)));
        nested.Value![0].Correlation.ShouldBe(flattened.Value[0].Correlation);
    }

    [Fact]
    public void Leaves_the_correlation_absent_when_the_provider_echoes_nothing()
    {
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions());

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = interpreter.Interpret(Verified(
            """[{"sg_event_id":"evt-1","event":"delivered"}]"""));

        result.Value![0].Correlation.ShouldBeNull();
    }

    [Fact]
    public void Drops_engagement_entries_the_hub_does_not_track_without_losing_the_batch()
    {
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions());

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = interpreter.Interpret(Verified(
            """
            [{"sg_event_id":"evt-c","event":"click"},
             {"sg_event_id":"evt-2","event":"delivered"}]
            """));

        result.IsSuccess.ShouldBeTrue(result.Error);
        result.Value!.Count.ShouldBe(1);
        result.Value[0].ProviderEventId.ShouldBe("evt-2");
    }

    [Fact]
    public void Refuses_a_body_that_is_not_a_batch()
    {
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions());

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = interpreter.Interpret(Verified(
            """{"sg_event_id":"evt-1","event":"delivered"}"""));

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.PayloadUnreadable).ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Refuses_a_body_that_is_not_readable_as_json()
    {
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions());

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = interpreter.Interpret(
            Verified("this is not a batch"));

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.PayloadUnreadable).ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Refuses_an_entry_without_the_event_identifier_of_the_provider()
    {
        SendGridWebhookInterpreter interpreter = Build(new SendGridWebhookOptions());

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = interpreter.Interpret(Verified(
            """[{"event":"delivered"}]"""));

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.PayloadUnreadable).ShouldBeTrue(result.Error);
    }

    private static SendGridWebhookInterpreter Build(SendGridWebhookOptions options)
        => new(
            Options.Create(options),
            new FrozenClock(Now),
            NullLogger<SendGridWebhookInterpreter>.Instance);

    private static string PublicKeyOf(ECDsa key)
        => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    private static VerifiedProviderWebhook Verified(string body)
        => new("sendgrid", Now, Encoding.UTF8.GetBytes(body));

    // Signs the payload the provider signs, the request timestamp followed by
    // the raw body, with a key pair minted for this test alone, so the suite
    // never carries a committed secret nor depends on an external vector.
    private static ProviderWebhookRequest Request(
        ECDsa key,
        string body,
        DateTimeOffset stamp,
        string? remoteIpAddress = null)
    {
        var timestamp = stamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = Convert.ToBase64String(key.SignData(
            Encoding.UTF8.GetBytes(timestamp + body),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));

        return new ProviderWebhookRequest(
            "sendgrid",
            "https://hub.example/webhooks/sendgrid",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["X-Twilio-Email-Event-Webhook-Timestamp"] = timestamp,
                ["X-Twilio-Email-Event-Webhook-Signature"] = signature,
            },
            remoteIpAddress,
            Encoding.UTF8.GetBytes(body));
    }

    private sealed class FrozenClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
