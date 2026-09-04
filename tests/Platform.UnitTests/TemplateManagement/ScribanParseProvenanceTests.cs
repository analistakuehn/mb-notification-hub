using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.SharedKernel;
using Scriban;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// Which renders may write to the parse memoization. Published content is
/// immutable per version, so its parse is reusable; a preview renders whatever
/// the author has in front of them at that moment, and the same text is a
/// different template a keystroke later.
/// </summary>
public sealed class ScribanParseProvenanceTests
{
    private const string Source = "Olá {{ user.name }}, o pedido {{ order.id }} foi atualizado.";

    [Fact]
    public async Task A_preview_render_leaves_the_memoization_untouched()
    {
        using var cache = new ScribanParseCache();
        ScribanTemplateEngine engine = Engine(cache);

        Result<string> rendered = await engine.RenderAsync(Source, Payload(), CancellationToken.None);

        rendered.IsSuccess.ShouldBeTrue();
        cache.Parses.ShouldBe(0, "o preview não consulta nem alimenta a memoização");
        cache.Hits.ShouldBe(0, "o preview não consulta nem alimenta a memoização");
        cache.ResidentEntries.ShouldBe(0, "nada do preview fica residente");
    }

    [Fact]
    public async Task A_published_render_memoizes_the_source_it_rendered()
    {
        using var cache = new ScribanParseCache();
        ScribanTemplateEngine engine = Engine(cache);

        Result<string> first = await Published(engine, Source);
        Result<string> second = await Published(engine, Source);

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        second.Value.ShouldBe(first.Value);

        // The falsification pair of the test above: without this one, refusing
        // to memoize the preview would read as proof on a memoization that
        // never holds anything at all.
        cache.Parses.ShouldBe(1, "a segunda entrega da mesma fonte publicada não reparseia");
        cache.Hits.ShouldBe(1, "a segunda entrega da mesma fonte publicada vem da memória");
        cache.ResidentEntries.ShouldBe(1, "a fonte publicada fica residente");
        cache.ResidentBytes.ShouldBe(
            ScribanParseCache.WeightOf(Source, Template.Parse(Source)),
            "a entrada pesa a árvore que ela guarda, e não o texto da fonte");
    }

    [Fact]
    public async Task A_render_the_engine_refused_for_size_never_reaches_the_memoization()
    {
        using var cache = new ScribanParseCache();
        ScribanTemplateEngine engine = Engine(cache, maxSizeChars: 200);
        var oversized = Source + new string('.', 300);

        Result<string> refused = await Published(engine, oversized);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.ShouldContain("character limit");
        cache.Parses.ShouldBe(0, "a fonte recusada por tamanho nem chega a ser parseada");
        cache.ResidentEntries.ShouldBe(0, "nada da fonte recusada fica residente");
    }

    [Fact]
    public async Task A_render_the_engine_refused_for_a_bad_source_never_reaches_the_memoization()
    {
        using var cache = new ScribanParseCache();
        ScribanTemplateEngine engine = Engine(cache);

        // Reads a variable the payload does not carry: the source parses and the
        // render is refused, which is the shape of an authoring mistake that
        // reaches the renderer.
        Result<string> refused = await Published(engine, "Olá {{ missing.name }}.");

        refused.IsFailure.ShouldBeTrue();
        cache.ResidentEntries.ShouldBe(
            0, "uma fonte que o motor recusou não pode ser respondida de memória depois");
    }

    private static ScribanTemplateEngine Engine(ScribanParseCache cache, int maxSizeChars = 131_072)
        => new(Options.Create(new TemplatingOptions { MaxTemplateSizeChars = maxSizeChars }), cache);

    private static Task<Result<string>> Published(ScribanTemplateEngine engine, string source)
    {
        ScribanTemplateEngine.FormRenderScope scope = engine.BeginForm();
        return engine.RenderAsync(scope, source, Payload(), CancellationToken.None);
    }

    private static JsonElement Payload()
    {
        using var document = JsonDocument.Parse(
            """{"user":{"name":"Ana"},"order":{"id":"A-1"}}""");
        return document.RootElement.Clone();
    }
}
