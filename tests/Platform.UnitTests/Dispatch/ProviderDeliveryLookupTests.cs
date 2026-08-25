using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.Dispatch;

/// <summary>
/// What the two delivery lookups ask, and what they refuse to ask.
/// <para>
/// The refusals carry the weight here. One of them is the whole shape of an
/// external commercial decision: how far back the e-mail activity reaches is a
/// term of the contracted plan, and the code has to state the limit rather than
/// discover it as an empty answer, because an empty answer reads exactly like a
/// provider denying it ever saw the message.
/// </para>
/// </summary>
public sealed class ProviderDeliveryLookupTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private static readonly DispatchCorrelation Correlation = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public async Task The_email_lookup_refuses_a_message_older_than_the_contracted_reach()
    {
        CannedHandler handler = Handler(_ => Json(HttpStatusCode.OK, """{"messages":[]}"""));
        SendGridDeliveryLookup lookup = SendGrid(handler, new SendGridOptions
        {
            ApiKey = "sg-key",
            ActivityLookbackDays = 3,
        });

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = await lookup.LookupAsync(
            new ProviderDeliveryQuery(Correlation, "msg-1", null, Now.AddDays(-4)),
            CancellationToken.None);

        ProviderLookupRefusal.Is(result, ProviderLookupRefusal.HistoryExhausted)
            .ShouldBeTrue(result.Error);
        handler.Requests.ShouldBeEmpty(
            "uma mensagem fora do alcance histórico não deve custar sequer a chamada.");
    }

    [Fact]
    public async Task The_email_lookup_asks_inside_the_reach_by_the_identifiers_the_send_attached()
    {
        CannedHandler handler = Handler(_ => Json(
            HttpStatusCode.OK,
            """
            {"messages":[{"msg_id":"sg-1","status":"delivered","last_event_time":"2026-08-25T09:30:00Z"}]}
            """));
        SendGridDeliveryLookup lookup = SendGrid(handler, new SendGridOptions
        {
            ApiKey = "sg-key",
            ActivityLookbackDays = 3,
        });

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = await lookup.LookupAsync(
            new ProviderDeliveryQuery(Correlation, "msg-1", null, Now.AddHours(-7)),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error);
        var asked = Uri.UnescapeDataString(handler.Requests.ShouldHaveSingleItem());
        asked.ShouldContain(Correlation.NotificationId.ToString());
        asked.Contains(Correlation.AttemptId.ToString(), StringComparison.Ordinal).ShouldBeTrue(
            "a busca precisa nomear a tentativa: uma notificação pode ter várias no mesmo canal, e "
            + "uma busca só pela notificação atribuiria a uma tentativa o desfecho de outra.");

        ProviderDeliveryEvent found = result.Value!.ShouldHaveSingleItem();
        found.Kind.ShouldBe(DeliveryFeedbackKind.Delivered);
        found.Correlation.ShouldBe(Correlation);
        found.OccurredAt.ShouldBe(new DateTimeOffset(2026, 8, 25, 9, 30, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// The activity view says a message was not delivered and does not say why.
    /// Promoting that into an accusation against the destination would close a
    /// person's mailbox on a word that carries no reason at all.
    /// </summary>
    [Fact]
    public async Task An_undelivered_email_carries_no_suppression_signal_from_the_activity_view()
    {
        CannedHandler handler = Handler(_ => Json(
            HttpStatusCode.OK,
            """
            {"messages":[{"msg_id":"sg-2","status":"not_delivered","last_event_time":"2026-08-25T09:30:00Z"}]}
            """));
        SendGridDeliveryLookup lookup = SendGrid(handler, new SendGridOptions { ApiKey = "sg-key" });

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = await lookup.LookupAsync(
            new ProviderDeliveryQuery(Correlation, "msg-2", null, Now.AddHours(-7)),
            CancellationToken.None);

        ProviderDeliveryEvent found = result.Value!.ShouldHaveSingleItem();
        found.Kind.ShouldBe(DeliveryFeedbackKind.Failed);
        found.Signal.ShouldBe(SuppressionSignal.None);
    }

    /// <summary>
    /// The identity of a provider event has to be the same word whichever half
    /// of feedback observed it, or the hub honours one refusal twice: on SMS
    /// the contact ledger closes a destination at the second refusal inside a
    /// week, so a doubled count takes a reachable number away from a person who
    /// was refused once.
    /// </summary>
    [Fact]
    public async Task The_sms_lookup_and_the_callback_mint_the_same_event_identity()
    {
        CannedHandler handler = Handler(_ => Json(
            HttpStatusCode.OK,
            """
            {"sid":"SM123","status":"undelivered","error_code":30003,
             "date_sent":"2026-08-25T09:00:00Z","date_updated":"2026-08-25T09:05:00Z"}
            """));
        TwilioDeliveryLookup lookup = Twilio(handler);

        Result<IReadOnlyList<ProviderDeliveryEvent>> pulled = await lookup.LookupAsync(
            new ProviderDeliveryQuery(Correlation, "SM123", null, Now.AddHours(-7)),
            CancellationToken.None);

        var interpreter = new TwilioWebhookInterpreter(
            Options.Create(new TwilioWebhookOptions()),
            new FrozenClock(Now),
            NullLogger<TwilioWebhookInterpreter>.Instance);
        Result<IReadOnlyList<ProviderDeliveryEvent>> pushed = interpreter.Interpret(
            new VerifiedProviderWebhook(
                "twilio",
                Now,
                Encoding.UTF8.GetBytes("ErrorCode=30003&MessageSid=SM123&MessageStatus=undelivered")));

        ProviderDeliveryEvent found = pulled.Value!.ShouldHaveSingleItem();
        ProviderDeliveryEvent callback = pushed.Value!.ShouldHaveSingleItem();
        found.ProviderEventId.ShouldBe(callback.ProviderEventId);
        found.Kind.ShouldBe(callback.Kind);
        found.Signal.ShouldBe(
            callback.Signal,
            "o vocabulário que decide se um código acusa o destino é o mesmo nas duas metades.");
        found.Signal.ShouldBe(SuppressionSignal.InvalidDestination);
    }

    [Fact]
    public async Task The_sms_lookup_asks_by_identity_and_sends_no_destination_when_it_has_one()
    {
        CannedHandler handler = Handler(_ => Json(
            HttpStatusCode.OK,
            """{"sid":"SM123","status":"delivered","date_updated":"2026-08-25T09:05:00Z"}"""));
        TwilioDeliveryLookup lookup = Twilio(handler);

        await lookup.LookupAsync(
            new ProviderDeliveryQuery(
                Correlation, "SM123", new SmsDeliveryTarget("+5511999998888"), Now.AddHours(-7)),
            CancellationToken.None);

        var asked = Uri.UnescapeDataString(handler.Requests.ShouldHaveSingleItem());
        asked.ShouldContain("/Messages/SM123.json");
        asked.Contains("+5511999998888", StringComparison.Ordinal).ShouldBeFalse(
            "com identidade de mensagem não há por que enviar o destino ao provedor.");
    }

    [Fact]
    public async Task The_sms_lookup_falls_back_to_the_destination_inside_the_configured_window()
    {
        CannedHandler handler = Handler(_ => Json(HttpStatusCode.OK, """{"messages":[]}"""));
        TwilioDeliveryLookup lookup = Twilio(handler, new TwilioOptions
        {
            AccountSid = "AC1",
            CredentialSecret = "secret",
            LookupWindowSeconds = 300,
        });

        await lookup.LookupAsync(
            new ProviderDeliveryQuery(
                Correlation, null, new SmsDeliveryTarget("+5511999998888"), Now.AddHours(-7)),
            CancellationToken.None);

        var asked = Uri.UnescapeDataString(handler.Requests.ShouldHaveSingleItem());
        asked.ShouldContain("To=+5511999998888");
        asked.ShouldContain("DateSent>=2026-08-25T04:55:00Z");
        asked.ShouldContain("DateSent<=2026-08-25T05:05:00Z");
    }

    /// <summary>
    /// Best effort has to be able to say "I do not know". Picking one message
    /// out of a window that matched two would settle an attempt with another
    /// attempt's outcome, and on this channel that outcome can close a number.
    /// </summary>
    [Fact]
    public async Task The_sms_lookup_concludes_nothing_when_the_window_matched_more_than_one_message()
    {
        CannedHandler handler = Handler(_ => Json(
            HttpStatusCode.OK,
            """
            {"messages":[
              {"sid":"SM1","status":"delivered","date_updated":"2026-08-25T09:05:00Z"},
              {"sid":"SM2","status":"undelivered","error_code":30003,"date_updated":"2026-08-25T09:06:00Z"}
            ]}
            """));
        TwilioDeliveryLookup lookup = Twilio(handler);

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = await lookup.LookupAsync(
            new ProviderDeliveryQuery(
                Correlation, null, new SmsDeliveryTarget("+5511999998888"), Now.AddHours(-7)),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error);
        result.Value!.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_sms_lookup_refuses_a_query_with_neither_identity_nor_destination()
    {
        CannedHandler handler = Handler(_ => Json(HttpStatusCode.OK, "{}"));
        TwilioDeliveryLookup lookup = Twilio(handler);

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = await lookup.LookupAsync(
            new ProviderDeliveryQuery(Correlation, null, null, Now.AddHours(-7)),
            CancellationToken.None);

        ProviderLookupRefusal.Is(result, ProviderLookupRefusal.QueryUnusable).ShouldBeTrue(result.Error);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_identity_the_provider_holds_nothing_for_is_an_answer_and_not_a_fault()
    {
        CannedHandler handler = Handler(_ => Json(HttpStatusCode.NotFound, """{"code":20404}"""));
        TwilioDeliveryLookup lookup = Twilio(handler);

        Result<IReadOnlyList<ProviderDeliveryEvent>> result = await lookup.LookupAsync(
            new ProviderDeliveryQuery(Correlation, "SM404", null, Now.AddHours(-7)),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error);
        result.Value!.ShouldBeEmpty();
    }

    private static SendGridDeliveryLookup SendGrid(CannedHandler handler, SendGridOptions options)
        => new(
            new StubHttpClientFactory(handler),
            Options.Create(options),
            new FrozenClock(Now),
            NullLogger<SendGridDeliveryLookup>.Instance);

    private static TwilioDeliveryLookup Twilio(CannedHandler handler, TwilioOptions? options = null)
        => new(
            new StubHttpClientFactory(handler),
            Options.Create(options ?? new TwilioOptions { AccountSid = "AC1", CredentialSecret = "secret" }),
            Options.Create(new TwilioWebhookOptions()),
            NullLogger<TwilioDeliveryLookup>.Instance);

    private static CannedHandler Handler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(respond);

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Records every URL asked for, which is what these tests grade.</summary>
    private sealed class CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FrozenClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://provider.test") };
    }
}
