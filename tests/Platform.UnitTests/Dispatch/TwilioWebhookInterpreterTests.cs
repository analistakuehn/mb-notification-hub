using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.Dispatch;

public sealed class TwilioWebhookInterpreterTests
{
    // Signature vector published by the provider for this exact request, and
    // recomputed with an independent implementation before it entered the
    // suite. It anchors the whole recipe at once: the URL, the field order,
    // the concatenation and the encoding.
    private const string VectorUrl = "https://mycompany.com/myapp.php?foo=1&bar=2";
    private const string VectorToken = "12345";
    private const string VectorSignature = "RSOYDt4T1cUTdK1PDd93/VVr8B8=";

    private const string VectorBody =
        "CallSid=CA1234567890ABCDE&Caller=%2B14158675309&Digits=1234"
        + "&From=%2B14158675309&To=%2B18005551212";

    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Accepts_the_signature_vector_published_by_the_provider()
    {
        TwilioWebhookInterpreter interpreter = Build(new TwilioWebhookOptions { AuthToken = VectorToken });

        Result<VerifiedProviderWebhook> result = interpreter.Verify(
            Request(VectorUrl, VectorBody, VectorSignature));

        result.IsSuccess.ShouldBeTrue(result.Error);
        result.Value!.ProviderKey.ShouldBe("twilio");
        result.Value.VerifiedAt.ShouldBe(Now);
    }

    [Fact]
    public void Refuses_the_vector_when_a_single_byte_of_the_body_changed()
    {
        TwilioWebhookInterpreter interpreter = Build(new TwilioWebhookOptions { AuthToken = VectorToken });
        var altered = VectorBody.Replace("Digits=1234", "Digits=1235", StringComparison.Ordinal);

        Result<VerifiedProviderWebhook> result = interpreter.Verify(
            Request(VectorUrl, altered, VectorSignature));

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.SignatureInvalid).ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Refuses_the_vector_when_a_single_character_of_the_signature_changed()
    {
        TwilioWebhookInterpreter interpreter = Build(new TwilioWebhookOptions { AuthToken = VectorToken });

        Result<VerifiedProviderWebhook> result = interpreter.Verify(
            Request(VectorUrl, VectorBody, "RSOZDt4T1cUTdK1PDd93/VVr8B8="));

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.SignatureInvalid).ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Refuses_the_vector_when_the_signature_header_is_absent()
    {
        TwilioWebhookInterpreter interpreter = Build(new TwilioWebhookOptions { AuthToken = VectorToken });
        ProviderWebhookRequest request = new(
            "twilio",
            VectorUrl,
            new Dictionary<string, string>(StringComparer.Ordinal),
            null,
            Encoding.UTF8.GetBytes(VectorBody));

        Result<VerifiedProviderWebhook> result = interpreter.Verify(request);

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.SignatureInvalid).ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Refuses_every_callback_while_the_verification_secret_is_absent()
    {
        TwilioWebhookInterpreter interpreter = Build(new TwilioWebhookOptions());

        Result<VerifiedProviderWebhook> result = interpreter.Verify(
            Request(VectorUrl, VectorBody, VectorSignature));

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.SignatureInvalid).ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Refuses_a_callback_from_an_address_outside_the_configured_allowlist()
    {
        TwilioWebhookInterpreter interpreter = Build(new TwilioWebhookOptions
        {
            AuthToken = VectorToken,
            AllowedIpPrefixes = ["54.172.60."],
        });

        Result<VerifiedProviderWebhook> result = interpreter.Verify(
            Request(VectorUrl, VectorBody, VectorSignature, "203.0.113.9"));

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.OriginNotAllowed).ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Accepts_a_callback_from_an_address_inside_the_configured_allowlist()
    {
        TwilioWebhookInterpreter interpreter = Build(new TwilioWebhookOptions
        {
            AuthToken = VectorToken,
            AllowedIpPrefixes = ["54.172.60."],
        });

        Result<VerifiedProviderWebhook> result = interpreter.Verify(
            Request(VectorUrl, VectorBody, VectorSignature, "54.172.60.3"));

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Accepts_any_address_while_the_allowlist_is_empty()
    {
        TwilioWebhookInterpreter interpreter = Build(new TwilioWebhookOptions
        {
            AuthToken = VectorToken,
            AllowedIpPrefixes = [],
        });

        Result<VerifiedProviderWebhook> result = interpreter.Verify(
            Request(VectorUrl, VectorBody, VectorSignature, "203.0.113.9"));

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Refuses_an_authentic_callback_whose_timestamp_left_the_window()
    {
        TwilioWebhookInterpreter interpreter = Build(new TwilioWebhookOptions
        {
            AuthToken = VectorToken,
            TimestampWindowSeconds = 300,
        });
        (var body, var signature) = Sign(
            VectorUrl,
            VectorToken,
            ("MessageSid", "SM7f1"),
            ("MessageStatus", "delivered"),
            ("Timestamp", Unix(Now.AddSeconds(-301))));

        Result<VerifiedProviderWebhook> result = interpreter.Verify(Request(VectorUrl, body, signature));

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.TimestampOutOfWindow)
            .ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Accepts_an_authentic_callback_whose_timestamp_is_inside_the_window()
    {
        TwilioWebhookInterpreter interpreter = Build(new TwilioWebhookOptions
        {
            AuthToken = VectorToken,
            TimestampWindowSeconds = 300,
        });
        (var body, var signature) = Sign(
            VectorUrl,
            VectorToken,
            ("MessageSid", "SM7f1"),
            ("MessageStatus", "delivered"),
            ("Timestamp", Unix(Now.AddSeconds(-299))));

        Result<VerifiedProviderWebhook> result = interpreter.Verify(Request(VectorUrl, body, signature));

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Theory]
    [InlineData("queued", DeliveryFeedbackKind.Sent)]
    [InlineData("sending", DeliveryFeedbackKind.Sent)]
    [InlineData("sent", DeliveryFeedbackKind.Sent)]
    [InlineData("accepted", DeliveryFeedbackKind.Sent)]
    [InlineData("delivered", DeliveryFeedbackKind.Delivered)]
    [InlineData("read", DeliveryFeedbackKind.Read)]
    [InlineData("failed", DeliveryFeedbackKind.Failed)]
    [InlineData("canceled", DeliveryFeedbackKind.Failed)]
    [InlineData("undelivered", DeliveryFeedbackKind.Bounced)]
    public void Translates_each_provider_status_into_its_canonical_kind(
        string status,
        DeliveryFeedbackKind expected)
    {
        TwilioWebhookInterpreter interpreter = Build(new TwilioWebhookOptions { AuthToken = VectorToken });

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = interpreter.Interpret(
            Verified($"MessageSid=SM7f1&MessageStatus={status}"));

        result.IsSuccess.ShouldBeTrue(result.Error);
        result.Value!.Count.ShouldBe(1);
        result.Value[0].Kind.ShouldBe(expected);
        result.Value[0].ProviderMessageId.ShouldBe("SM7f1");
    }

    [Fact]
    public void Derives_the_event_identity_from_the_message_and_the_status_it_reached()
    {
        TwilioWebhookInterpreter interpreter = Build(new TwilioWebhookOptions { AuthToken = VectorToken });

        Result<IReadOnlyList<ProviderDeliveryEvent>> first = interpreter.Interpret(
            Verified("MessageSid=SM7f1&MessageStatus=sent"));
        Result<IReadOnlyList<ProviderDeliveryEvent>> redelivered = interpreter.Interpret(
            Verified("MessageStatus=sent&MessageSid=SM7f1"));
        Result<IReadOnlyList<ProviderDeliveryEvent>> later = interpreter.Interpret(
            Verified("MessageSid=SM7f1&MessageStatus=delivered"));

        first.Value![0].ProviderEventId.ShouldBe("SM7f1:sent");
        redelivered.Value![0].ProviderEventId.ShouldBe(first.Value[0].ProviderEventId);
        later.Value![0].ProviderEventId.ShouldNotBe(first.Value[0].ProviderEventId);
    }

    [Fact]
    public void Leaves_the_correlation_absent_because_the_body_carries_no_identifiers()
    {
        TwilioWebhookInterpreter interpreter = Build(new TwilioWebhookOptions { AuthToken = VectorToken });

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = interpreter.Interpret(
            Verified("MessageSid=SM7f1&MessageStatus=delivered"));

        result.Value![0].Correlation.ShouldBeNull();
    }

    [Fact]
    public void Reports_a_configured_error_code_as_an_invalid_destination()
    {
        TwilioWebhookInterpreter interpreter = Build(new TwilioWebhookOptions
        {
            AuthToken = VectorToken,
            InvalidDestinationCodes = ["30005"],
        });

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = interpreter.Interpret(
            Verified("MessageSid=SM7f1&MessageStatus=undelivered&ErrorCode=30005"));

        result.Value![0].Kind.ShouldBe(DeliveryFeedbackKind.Bounced);
        result.Value[0].Signal.ShouldBe(SuppressionSignal.InvalidDestination);
        result.Value[0].ErrorCode.ShouldBe("30005");
    }

    [Fact]
    public void Reports_a_code_the_configured_vocabulary_does_not_name_as_no_signal()
    {
        TwilioWebhookInterpreter interpreter = Build(new TwilioWebhookOptions
        {
            AuthToken = VectorToken,
            InvalidDestinationCodes = ["30005"],
            HardBounceCodes = ["21610"],
        });

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = interpreter.Interpret(
            Verified("MessageSid=SM7f1&MessageStatus=undelivered&ErrorCode=30003"));

        result.Value![0].Kind.ShouldBe(DeliveryFeedbackKind.Bounced);
        result.Value[0].Signal.ShouldBe(SuppressionSignal.None);
    }

    [Fact]
    public void Refuses_a_status_outside_the_mapped_vocabulary_instead_of_guessing_a_state()
    {
        TwilioWebhookInterpreter interpreter = Build(new TwilioWebhookOptions { AuthToken = VectorToken });

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = interpreter.Interpret(
            Verified("MessageSid=SM7f1&MessageStatus=teleported"));

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.PayloadUnreadable).ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Refuses_a_body_without_the_message_identifier()
    {
        TwilioWebhookInterpreter interpreter = Build(new TwilioWebhookOptions { AuthToken = VectorToken });

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = interpreter.Interpret(
            Verified("MessageStatus=delivered"));

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.PayloadUnreadable).ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Refuses_a_callback_addressed_to_another_provider()
    {
        TwilioWebhookInterpreter interpreter = Build(new TwilioWebhookOptions { AuthToken = VectorToken });
        ProviderWebhookRequest request = new(
            "sendgrid",
            VectorUrl,
            new Dictionary<string, string>(StringComparer.Ordinal),
            null,
            Encoding.UTF8.GetBytes(VectorBody));

        Result<VerifiedProviderWebhook> result = interpreter.Verify(request);

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.ProviderUnknown).ShouldBeTrue(result.Error);
    }

    private static TwilioWebhookInterpreter Build(TwilioWebhookOptions options)
        => new(
            Options.Create(options),
            new FrozenClock(Now),
            NullLogger<TwilioWebhookInterpreter>.Instance);

    private static ProviderWebhookRequest Request(
        string requestUrl,
        string body,
        string signature,
        string? remoteIpAddress = null)
        => new(
            "twilio",
            requestUrl,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["X-Twilio-Signature"] = signature,
            },
            remoteIpAddress,
            Encoding.UTF8.GetBytes(body));

    private static VerifiedProviderWebhook Verified(string body)
        => new("twilio", Now, Encoding.UTF8.GetBytes(body));

    private static string Unix(DateTimeOffset instant)
        => instant.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    // Builds a body and the signature the provider would send for it, so a
    // window or vocabulary assertion is never satisfied by a signature that
    // failed first. The recipe it leans on is itself pinned by the published
    // vector above.
    private static (string Body, string Signature) Sign(
        string requestUrl,
        string authToken,
        params (string Name, string Value)[] fields)
    {
        List<KeyValuePair<string, string>> ordered =
        [
            .. fields
                .Select(field => new KeyValuePair<string, string>(field.Name, field.Value))
                .OrderBy(field => field.Key, StringComparer.Ordinal)
                .ThenBy(field => field.Value, StringComparer.Ordinal),
        ];
        var body = string.Join(
            '&',
            fields.Select(field =>
                $"{Uri.EscapeDataString(field.Name)}={Uri.EscapeDataString(field.Value)}"));

        return (body, Convert.ToBase64String(
            TwilioWebhookInterpreter.ComputeSignature(requestUrl, ordered, authToken)));
    }

    private sealed class FrozenClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
