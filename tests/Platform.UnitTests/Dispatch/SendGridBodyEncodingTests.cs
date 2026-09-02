using System.Text;
using System.Text.Json;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;

namespace NotificationHub.UnitTests.Dispatch;

/// <summary>
/// O provedor recusa mensagens acima de trinta milhões de bytes, então o
/// comprimento do corpo é um recurso escasso. Estes casos fixam que a
/// serialização do corpo não expande o conteúdo: sob o codificador padrão,
/// cada ocorrência de um caractere sensível a HTML custa seis bytes, e um
/// conteúdo escolhido por quem envia pode multiplicar o corpo por seis.
/// </summary>
public sealed class SendGridBodyEncodingTests
{
    /// <summary>
    /// Padrão de bytes cuja representação em base64 é composta apenas do
    /// caractere que o codificador padrão escapa. É o único conteúdo que
    /// distingue as duas serializações: com bytes legíveis, a forma expansiva
    /// mede o mesmo que a forma correta e o caso passaria sem provar nada.
    /// </summary>
    private static byte[] AdversarialContent(int length)
    {
        var content = new byte[length];
        for (var index = 0; index + 2 < content.Length; index += 3)
        {
            content[index] = 0xFB;
            content[index + 1] = 0xEF;
            content[index + 2] = 0xBE;
        }

        return content;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(3_000)]
    [InlineData(3_000_000)]
    public void Encoding_adversarial_content_costs_only_the_arithmetic_of_base64(int rawLength)
    {
        var encoded = Convert.ToBase64String(AdversarialContent(rawLength));
        var expected = (4 * ((rawLength + 2) / 3)) + 2;

        var measured = Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize(encoded, SendGridChannelProvider.BodySerialization));

        measured.ShouldBe(expected);
    }

    [Fact]
    public void Encoding_a_rendered_body_does_not_multiply_its_delimiters()
    {
        const string body = "<html><body><p>Olá &amp; bem-vindo</p></body></html>";
        var raw = Encoding.UTF8.GetByteCount(body);

        var measured = Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize(body, SendGridChannelProvider.BodySerialization));

        // Apenas as aspas do JSON separam o corpo serializado do conteúdo cru.
        measured.ShouldBe(raw + 2);
    }

    [Fact]
    public void Encoding_preserves_content_that_would_otherwise_break_the_document()
    {
        var body = "aspa " + (char)34 + " barra " + (char)92 + " controle " + (char)1;

        var serialized = JsonSerializer.Serialize(
            body, SendGridChannelProvider.BodySerialization);

        // A propriedade que importa e a ida e volta, nao a forma do escape: o
        // escape relaxado nao afrouxa aspa, barra invertida nem controle, e um
        // caso que fixasse a grafia mediria o codificador em vez do contrato.
        JsonSerializer.Deserialize<string>(serialized).ShouldBe(body);
        serialized.ShouldNotContain((char)1);
    }
}
