using System.Net;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;

/// <summary>
/// Mail Send v3 adapter. Maps the provider verdict without optimism: only an
/// explicit acceptance is <see cref="ProviderOutcome.Accepted"/>; a 4xx is a
/// permanent rejection except 429, which is throttling; 5xx, timeout,
/// network fault and open circuit stay transient because the provider gave
/// no verdict. The adapter never retries a send: a mail send is not
/// idempotent at the provider, so redelivery belongs to the queue.
/// <para>
/// A send that carries an accepted set carries the whole of it: the members
/// are composed as attachment fields of the one body, in the order they were
/// accepted in, and their bytes travel from custody onto the connection
/// without ever being held here. Nothing in this adapter revalidates the set,
/// converts it to a link or leaves a member out; whether the set may still go
/// out was settled before this call, and a channel that cannot carry it is
/// refused by the route that planned the send.
/// </para>
/// </summary>
internal sealed class SendGridChannelProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<SendGridOptions> options,
    ILogger<SendGridChannelProvider> logger,
    IAcceptedAttachmentContent? attachmentContent = null) : IChannelProvider
{
    internal const string Key = "sendgrid";
    internal const string HttpClientName = "dispatch-sendgrid";

    /// <summary>
    /// Stable code of a send whose composed message is larger than one call
    /// may carry. It is a permanent rejection and not a transient failure: no
    /// redelivery of the same message composes a smaller one.
    /// </summary>
    internal const string MessageTooLargeErrorCode = "message-too-large";

    private const string MessageIdHeader = "X-Message-Id";

    /// <summary>
    /// Serialização do corpo do Mail Send. O escape relaxado é obrigatório e
    /// não é preferência de estilo: o codificador padrão gasta seis bytes por
    /// ocorrência de <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, <c>'</c> e
    /// <c>+</c>. Medido nesta base, um corpo HTML de 22.800 bytes vai a 48.802
    /// sob o padrão e a 23.602 sob o relaxado. O provedor recusa mensagens
    /// acima de trinta milhões de bytes, então a expansão consome orçamento de
    /// envelope sem contrapartida: o corpo é uma requisição HTTPS a uma API e
    /// nunca é embutido em HTML, que é o único contexto em que o escape do
    /// padrão protegeria alguma coisa.
    /// </summary>
    internal static readonly JsonSerializerOptions BodySerialization = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public Channel Channel => Channel.Email;

    public string ProviderKey => Key;

    public async Task<ProviderResult> SendAsync(DispatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        (EmailDeliveryTarget target, EmailMessage message) = Discriminate(request);
        SendGridOptions config = options.Value;
        EnsureConfigured(config);

        // Composed and measured before anything is opened and before anything
        // is called. A message past what one call may carry is refused here,
        // with the provider untouched and not a byte of custody read.
        SendGridBodyComposition composition = SendGridMailBody.Compose(
            target, message, config, request.Correlation, request.Attachments);
        if (composition.Body is not { } body)
        {
            logger.SendGridMessageOverCeiling(
                SendGridMailLimits.MaxBodyBytes, request.Attachments?.Count ?? 0);
            return ProviderResult.Rejected(MessageTooLargeErrorCode, null);
        }

        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v3/mail/send");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        using var content = new SendGridMailContent(body, ContentSource(request));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        httpRequest.Content = content;

        try
        {
            using HttpResponseMessage response = await client.SendAsync(httpRequest, cancellationToken);
            return await MapAsync(response, cancellationToken);
        }
        catch (BrokenCircuitException)
        {
            logger.SendGridCircuitOpen();
            return ProviderResult.Transient("circuit-open", null);
        }
        catch (TimeoutRejectedException)
        {
            logger.SendGridTimedOut(config.TimeoutSeconds);
            return ProviderResult.Transient("timeout", null);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            // A body that stopped reads as a broken request, and the transport
            // cannot say whose fault it was. The content knows, and what it
            // says takes precedence: a custody that did not hand the bytes
            // over is not a network fault, and reporting it as one would send
            // an operator to the wrong system.
            if (content.Interrupted is { } reason)
            {
                logger.SendGridBodyInterrupted(reason);
                return ProviderResult.Transient(reason, null);
            }

            logger.SendGridNetworkFault(exception);
            return ProviderResult.Transient("network", ProviderErrorSanitizer.Sanitize(exception.Message));
        }
    }

    /// <summary>
    /// Where the bytes of this send come from. A send with no set never opens
    /// anything, and a send with one that reached an adapter composed without
    /// the port is a composition defect: it is raised rather than answered,
    /// because a result would report a provider verdict for a message this
    /// host never had the means to compose.
    /// </summary>
    private IAcceptedAttachmentContent ContentSource(DispatchRequest request)
        => request.Attachments is null
            ? UnaskedAttachmentContent.Instance
            : attachmentContent ?? throw new InvalidOperationException(
                "O envio carrega um conjunto de anexos aceito e este host não compôs a porta "
                + "de conteúdo do módulo que os detém; a mensagem não pode ser montada.");

    internal static SendGridMailRequest BuildRequest(
        EmailDeliveryTarget target,
        EmailMessage message,
        SendGridOptions config,
        DispatchCorrelation? correlation = null,
        IReadOnlyList<SendGridAttachment>? attachments = null)
        => new(
            // custom_args carries the correlation ids the Event Webhook echoes
            // back; a pure pass-through that never touches the content bytes.
            [new SendGridPersonalization(
                [new SendGridAddress(target.EmailAddress, null)],
                correlation is null
                    ? null
                    : new Dictionary<string, string>
                    {
                        ["notification_id"] = correlation.NotificationId.ToString(),
                        ["attempt_id"] = correlation.AttemptId.ToString(),
                    })],
            new SendGridAddress(
                config.SenderEmail,
                string.IsNullOrWhiteSpace(config.SenderName) ? null : config.SenderName),
            message.Subject,
            // text/plain before text/html: Mail Send v3 requires content
            // ordered by ascending preference.
            [
                new SendGridContent("text/plain", message.TextBody),
                new SendGridContent("text/html", message.HtmlBody),
            ],
            attachments,
            new SendGridMailSettings(new SendGridSandboxMode(config.SandboxMode)));

    private async Task<ProviderResult> MapAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var statusCode = (int)response.StatusCode;

        // 202 is the live acceptance; sandbox-mode validation answers 200.
        if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK)
        {
            logger.SendGridAccepted(statusCode);
            return ProviderResult.Accepted(ReadMessageId(response));
        }

        var errorCode = $"http-{statusCode}";
        var errorMessage = await ReadErrorMessageAsync(response, cancellationToken);
        logger.SendGridSendFailed(statusCode, errorCode);

        if (response.StatusCode is HttpStatusCode.TooManyRequests)
        {
            return ProviderResult.Throttled(errorCode, errorMessage, response.Headers.RetryAfter?.Delta);
        }

        return statusCode is >= 400 and < 500
            ? ProviderResult.Rejected(errorCode, errorMessage)
            : ProviderResult.Transient(errorCode, errorMessage);
    }

    private static (EmailDeliveryTarget Target, EmailMessage Message) Discriminate(DispatchRequest request)
    {
        if (request.Target is not EmailDeliveryTarget target || request.Message is not EmailMessage message)
        {
            throw new InvalidOperationException(
                "The SendGrid adapter delivers e-mail only; it received a request for another channel.");
        }

        return (target, message);
    }

    private static void EnsureConfigured(SendGridOptions config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new InvalidOperationException(
                $"Missing configuration '{SendGridOptions.SectionName}:ApiKey'.");
        }

        if (string.IsNullOrWhiteSpace(config.SenderEmail))
        {
            throw new InvalidOperationException(
                $"Missing configuration '{SendGridOptions.SectionName}:SenderEmail'.");
        }
    }

    private static string? ReadMessageId(HttpResponseMessage response)
        => response.Headers.TryGetValues(MessageIdHeader, out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : null;

    private static async Task<string?> ReadErrorMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            SendGridErrorResponse? parsed = JsonSerializer.Deserialize<SendGridErrorResponse>(body);
            if (parsed?.Errors is not { Count: > 0 } errors)
            {
                return ProviderErrorSanitizer.Sanitize(body);
            }

            SendGridError first = errors[0];

            var text = string.IsNullOrWhiteSpace(first.Field)
                ? first.Message
                : $"{first.Field}: {first.Message}";
            return ProviderErrorSanitizer.Sanitize(text);
        }
        catch (JsonException)
        {
            return ProviderErrorSanitizer.Sanitize(body);
        }
    }
}
