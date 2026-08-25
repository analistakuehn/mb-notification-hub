using System.Globalization;
using System.Net;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.UnitTests.Dispatch;

public sealed class TwilioRequestShapeTests
{
    private static readonly Guid NotificationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AttemptId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Builds_a_programmable_messaging_request_with_the_sms_body()
    {
        TwilioOptions options = new()
        {
            Product = TwilioSmsProduct.ProgrammableMessaging,
            AccountSid = "AC123",
            FromNumber = "+5511999999999",
        };

        using HttpRequestMessage request = TwilioChannelProvider.BuildRequest(
            new SmsDeliveryTarget("+5511888888888"),
            new SmsMessage("Cotação atualizada"),
            options);
        var body = await request.Content!.ReadAsStringAsync();

        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri!.ToString().ShouldBe(
            "2010-04-01/Accounts/AC123/Messages.json");
        body.ShouldContain("To=%2B5511888888888");
        body.ShouldContain("From=%2B5511999999999");
        body.ShouldContain("Body=Cota%C3%A7%C3%A3o+atualizada");
    }

    [Fact]
    public async Task Builds_a_verify_request_with_the_custom_code()
    {
        TwilioOptions options = new()
        {
            Product = TwilioSmsProduct.Verify,
            ServiceSid = "VA123",
        };

        using HttpRequestMessage request = TwilioChannelProvider.BuildRequest(
            new SmsDeliveryTarget("+5511888888888"),
            new SmsMessage("123456"),
            options);
        var body = await request.Content!.ReadAsStringAsync();

        request.RequestUri!.ToString().ShouldBe(
            "https://verify.twilio.com/v2/Services/VA123/Verifications");
        body.ShouldContain("To=%2B5511888888888");
        body.ShouldContain("Channel=sms");
        body.ShouldContain("CustomCode=123456");
    }

    [Fact]
    public async Task The_sender_pool_replaces_the_single_number_when_one_is_configured()
    {
        TwilioOptions options = Messaging(messagingServiceSid: "MG0123456789");

        Dictionary<string, string> form = await BuildFormAsync(options);

        form["MessagingServiceSid"].ShouldBe("MG0123456789");
        form.ShouldNotContainKey("From");
    }

    [Fact]
    public async Task Without_a_sender_pool_the_send_keeps_the_single_verified_number()
    {
        // Falsification of the assertion above: the pool is what removes the
        // number, not the shape of the form.
        Dictionary<string, string> form = await BuildFormAsync(Messaging());

        form["From"].ShouldBe("+5511999999999");
        form.ShouldNotContainKey("MessagingServiceSid");
    }

    [Fact]
    public async Task The_callback_address_carries_the_identifiers_of_the_attempt()
    {
        TwilioOptions options = Messaging(
            statusCallbackUrl: "https://hooks.example.com/webhooks/twilio");

        Dictionary<string, string> form = await BuildFormAsync(options);

        var callback = new Uri(form["StatusCallback"]);
        callback.GetLeftPart(UriPartial.Path).ShouldBe("https://hooks.example.com/webhooks/twilio");
        Dictionary<string, string> query = ParseQuery(callback.Query);
        query[TwilioChannelProvider.NotificationIdParameter].ShouldBe(NotificationId.ToString());
        query[TwilioChannelProvider.AttemptIdParameter].ShouldBe(AttemptId.ToString());
    }

    [Fact]
    public async Task A_configured_callback_address_that_already_has_a_query_keeps_it()
    {
        TwilioOptions options = Messaging(
            statusCallbackUrl: "https://hooks.example.com/webhooks/twilio?tenant=mb");

        Dictionary<string, string> form = await BuildFormAsync(options);

        Dictionary<string, string> query = ParseQuery(new Uri(form["StatusCallback"]).Query);
        query["tenant"].ShouldBe("mb");
        query[TwilioChannelProvider.AttemptIdParameter].ShouldBe(AttemptId.ToString());
    }

    [Fact]
    public async Task Without_correlation_the_send_asks_for_no_callback()
    {
        // A callback nobody can tie to an attempt is feedback nobody applies,
        // and it would still cost the provider a retry loop against this hub.
        TwilioOptions options = Messaging(
            statusCallbackUrl: "https://hooks.example.com/webhooks/twilio");

        Dictionary<string, string> form = await BuildFormAsync(options, correlated: false);

        form.ShouldNotContainKey("StatusCallback");
    }

    [Fact]
    public async Task Without_a_configured_callback_address_the_send_asks_for_no_callback()
    {
        Dictionary<string, string> form = await BuildFormAsync(Messaging());

        form.ShouldNotContainKey("StatusCallback");
    }

    [Fact]
    public async Task The_validity_period_is_the_remaining_validity_in_seconds()
    {
        Dictionary<string, string> form = await BuildFormAsync(
            Messaging(), validity: TimeSpan.FromMinutes(3));

        form["ValidityPeriod"].ShouldBe("180");
    }

    [Fact]
    public async Task A_remaining_validity_above_the_ceiling_is_sent_as_the_ceiling()
    {
        // The provider refuses anything above its limit, and refusing the send
        // here would cost a notification that is still deliverable.
        Dictionary<string, string> form = await BuildFormAsync(
            Messaging(), validity: TimeSpan.FromDays(2));

        form["ValidityPeriod"].ShouldBe(
            TwilioOptions.DefaultMaxValidityPeriodSeconds.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Without_a_stated_validity_the_send_carries_no_validity_period()
    {
        Dictionary<string, string> form = await BuildFormAsync(Messaging(), validity: null);

        form.ShouldNotContainKey("ValidityPeriod");
    }

    [Fact]
    public async Task Verify_carries_neither_callback_nor_validity()
    {
        // The Verify product owns the message it sends; the knobs of
        // Programmable Messaging do not exist there and must not be invented.
        TwilioOptions options = new()
        {
            Product = TwilioSmsProduct.Verify,
            ServiceSid = "VA123",
            StatusCallbackUrl = "https://hooks.example.com/webhooks/twilio",
        };

        using HttpRequestMessage request = TwilioChannelProvider.BuildRequest(
            new SmsDeliveryTarget("+5511888888888"),
            new SmsMessage("123456"),
            options,
            new DispatchCorrelation(NotificationId, AttemptId),
            TimeSpan.FromMinutes(5));
        Dictionary<string, string> form = await ParseFormAsync(request);

        form.ShouldNotContainKey("StatusCallback");
        form.ShouldNotContainKey("ValidityPeriod");
    }

    private static TwilioOptions Messaging(
        string messagingServiceSid = "",
        string statusCallbackUrl = "")
        => new()
        {
            Product = TwilioSmsProduct.ProgrammableMessaging,
            AccountSid = "AC123",
            FromNumber = "+5511999999999",
            MessagingServiceSid = messagingServiceSid,
            StatusCallbackUrl = statusCallbackUrl,
        };

    private static async Task<Dictionary<string, string>> BuildFormAsync(
        TwilioOptions options,
        bool correlated = true,
        TimeSpan? validity = null)
    {
        using HttpRequestMessage request = TwilioChannelProvider.BuildRequest(
            new SmsDeliveryTarget("+5511888888888"),
            new SmsMessage("Código de acesso: 123456."),
            options,
            correlated ? new DispatchCorrelation(NotificationId, AttemptId) : null,
            validity);
        return await ParseFormAsync(request);
    }

    private static async Task<Dictionary<string, string>> ParseFormAsync(HttpRequestMessage request)
        => ParsePairs(await request.Content!.ReadAsStringAsync());

    private static Dictionary<string, string> ParseQuery(string query)
        => ParsePairs(query.TrimStart('?'));

    private static Dictionary<string, string> ParsePairs(string encoded)
        => encoded.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => WebUtility.UrlDecode(parts[0]),
                parts => parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) : "",
                StringComparer.Ordinal);
}
