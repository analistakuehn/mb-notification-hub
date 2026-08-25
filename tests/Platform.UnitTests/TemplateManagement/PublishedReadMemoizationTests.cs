using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class PublishedReadMemoizationTests
{
    private sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Rendering_the_same_source_twice_parses_once()
    {
        var cache = new ScribanParseCache();
        var engine = new ScribanTemplateEngine(Options.Create(new TemplatingOptions()), cache);

        Result<string> first = await engine.RenderAsync(
            "Olá {{ name }}", Variables("""{"name":"Ana"}"""), CancellationToken.None);
        Result<string> second = await engine.RenderAsync(
            "Olá {{ name }}", Variables("""{"name":"Bia"}"""), CancellationToken.None);

        first.Value.ShouldBe("Olá Ana");
        second.Value.ShouldBe("Olá Bia");
        cache.Parses.ShouldBe(1);
        cache.Hits.ShouldBe(1);
    }

    [Fact]
    public async Task A_source_that_fails_to_parse_is_never_cached()
    {
        var cache = new ScribanParseCache();
        var engine = new ScribanTemplateEngine(Options.Create(new TemplatingOptions()), cache);

        Result<string> first = await engine.RenderAsync("{{ 1 + }}", null, CancellationToken.None);
        Result<string> second = await engine.RenderAsync("{{ 1 + }}", null, CancellationToken.None);

        first.IsFailure.ShouldBeTrue();
        second.IsFailure.ShouldBeTrue();
        cache.Parses.ShouldBe(2);
        cache.Hits.ShouldBe(0);
    }

    [Fact]
    public void A_pointer_entry_answers_inside_the_window_and_expires_after_it()
    {
        var clock = new SteppingClock(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));
        var cache = new PublishedReadCache(clock);
        cache.SetPointer("template:app:key", "value-1");

        clock.Now = clock.Now.AddSeconds(59);
        cache.TryGetPointer("template:app:key", out string inside).ShouldBeTrue();
        inside.ShouldBe("value-1");

        clock.Now = clock.Now.AddSeconds(2);
        cache.TryGetPointer("template:app:key", out string _).ShouldBeFalse();
    }

    [Fact]
    public void An_immutable_entry_never_expires()
    {
        var clock = new SteppingClock(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));
        var cache = new PublishedReadCache(clock);
        cache.SetImmutable("layout:footer:3", "layout-body");

        clock.Now = clock.Now.AddDays(30);

        cache.TryGetImmutable("layout:footer:3", out string value).ShouldBeTrue();
        value.ShouldBe("layout-body");
    }

    private static JsonElement Variables(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
