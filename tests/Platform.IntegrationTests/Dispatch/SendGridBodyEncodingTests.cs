using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.IntegrationTests.Dispatch;

/// <summary>
/// Prova a serialização pelo corpo que sai na requisição, e não por uma
/// composição paralela montada no teste. Um caso que serializasse por conta
/// própria continuaria verde depois de o adaptador voltar ao codificador
/// padrão, porque mediria outra coisa.
/// </summary>
public sealed class SendGridBodyEncodingTests
{
    private const string RenderedHtml =
        "<html><body><div class=\"wrap\"><p>Olá &amp; bem-vindo</p>"
        + "<a href=\"https://example.com/?a=1&b=2\">Confirmar</a></div></body></html>";

    private static readonly DispatchRequest Request = new(
        new EmailDeliveryTarget("person@example.com"),
        new EmailMessage("Confirme sua operação", "Aguardando confirmação", RenderedHtml, "Olá"));

    [Fact]
    public async Task Submits_the_rendered_body_without_multiplying_its_delimiters()
    {
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = _ => Task.FromResult(new FakeProviderResponse(202, null, null));
        await using ServiceProvider services = DispatchTestServices.BuildProviderHost(
            Settings(server.BaseAddress));
        IChannelProvider provider = DispatchTestServices.ResolveProviderByKey(services, "sendgrid");

        ProviderResult result = await provider.SendAsync(Request, CancellationToken.None);

        result.Outcome.ShouldBe(ProviderOutcome.Accepted);
        FakeProviderRequest captured = server.Requests.ShouldHaveSingleItem();

        // Os delimitadores viajam como si mesmos. Sob o codificador padrão cada
        // um custaria seis bytes, e o provedor recusa mensagens acima de trinta
        // milhões de bytes.
        captured.Body.ShouldContain("<p>Olá &amp; bem-vindo</p>");
        captured.Body.ShouldNotContain("\\u003C");
        captured.Body.ShouldNotContain("\\u003E");
        captured.Body.ShouldNotContain("\\u0026");

        // O corpo carrega o HTML renderizado duas vezes, uma no assunto e outra
        // no conteúdo, mais o envelope. Um corpo expandido passaria de longe
        // deste limite: a mesma amostra mede 2,14 vezes sob o codificador
        // padrão.
        var html = Encoding.UTF8.GetByteCount(RenderedHtml);
        Encoding.UTF8.GetByteCount(captured.Body).ShouldBeLessThan(html + 512);
    }

    private static Dictionary<string, string?> Settings(Uri baseAddress)
        => new()
        {
            ["Modules:Dispatch:Providers:SendGrid:BaseAddress"] = baseAddress.ToString(),
            ["Modules:Dispatch:Providers:SendGrid:ApiKey"] = "sg-test-key",
            ["Modules:Dispatch:Providers:SendGrid:SenderEmail"] = "no-reply@example.com",
            ["Modules:Dispatch:Providers:SendGrid:TimeoutSeconds"] =
                5.ToString(CultureInfo.InvariantCulture),
        };
}
