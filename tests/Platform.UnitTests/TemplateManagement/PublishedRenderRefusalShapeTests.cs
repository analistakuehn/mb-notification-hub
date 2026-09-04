using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// The shape the published render hands its own refusals over in, asked of the
/// render path rather than of the policy that words them.
/// <para>
/// The policy takes the shape as an argument, and every test of it passes one
/// explicitly. Those tests therefore prove what each shape produces and never
/// which one this caller asks for, so swapping the argument at the call site is
/// invisible to all of them. That is the whole defect this pins: a consuming
/// module recognizes these two refusals by comparing the entire error text
/// against the word, so a code carrying a sentence stops being recognized and
/// collapses into an ordinary render failure, which tells the producer its
/// template is broken when what refused the message was a security rule or a
/// capacity rule acting on content the template rendered correctly.
/// </para>
/// </summary>
public sealed class PublishedRenderRefusalShapeTests
{
    private const string Application = "araia-cambio";
    private const string Key = "auth.otp.login";
    private const int Version = 4;

    /// <summary>A host the template itself allows, so only the ban can refuse it.</summary>
    private const string AllowedDomain = "banco.exemplo.br";

    private static readonly DateTimeOffset Start = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly Channel Sms = Channel.Create("sms").Value!;
    private static readonly Locale PtBr = Locale.Create("pt-BR").Value!;

    [Fact]
    public async Task The_security_refusal_leaves_the_render_as_the_bare_word()
    {
        Result<PublishedTemplateRender> refused = await RenderAsync(LinkVariables());

        refused.IsFailure.ShouldBeTrue();
        refused.Error.ShouldBe(RenderedContentRejectionReasons.AuthenticationSmsLink);
    }

    [Fact]
    public async Task The_size_refusal_leaves_the_render_as_the_bare_word()
    {
        Result<PublishedTemplateRender> refused = await RenderAsync(OversizedVariables());

        refused.IsFailure.ShouldBeTrue();
        refused.Error.ShouldBe(RenderedContentRejectionReasons.TooLarge);
    }

    private static async Task<Result<PublishedTemplateRender>> RenderAsync(JsonElement variables)
    {
        using var cache = new PublishedReadCache(TimeProvider.System);
        using TemplateManagementDbContext store = StoreOutOfReach();
        var renderer = new PublishedTemplateRenderer(
            store,
            new ScribanTemplateEngine(
                Options.Create(new TemplatingOptions()),
                new ScribanParseCache()),
            cache,
            new PublishedContextLoader(store, cache),
            NullLogger<PublishedTemplateRenderer>.Instance);

        // Everything the render needs is in memory and the store is out of
        // reach, so a read that went past it would raise instead of quietly
        // answering from a database this test never provided.
        cache.SetPointer($"render-context:{Application}:{Key}", AuthenticationSmsContext());

        return await renderer.RenderAsync(
            new PublishedRenderRequest
            {
                Application = Application,
                TemplateKey = Key,
                Channel = Sms.Value,
                Locale = PtBr.Value,
                Variables = variables,
            },
            CancellationToken.None);
    }

    /// <summary>
    /// A link on a domain the template approved. The destination guard runs
    /// over the payload before anything renders, so a host from outside the
    /// allowlist would be refused there and the ban would never answer.
    /// </summary>
    private static JsonElement LinkVariables()
        => Payload($$"""{"code":"834192","link":"https://{{AllowedDomain}}/ajuda"}""");

    /// <summary>
    /// A value that offers nothing clickable and grows the rendered body past
    /// what the channel carries, so the ban passes and the ceiling answers.
    /// </summary>
    private static JsonElement OversizedVariables()
        => Payload($$"""{"code":"{{new string('a', SmsSegmentCeiling.NeverWithin + 1)}}","link":""}""");

    private static JsonElement Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static PublishedTemplateContext AuthenticationSmsContext()
    {
        TemplateKey key = TemplateKey.Create(Key).Value!;
        Template template = Template.Create(key, new TemplateMetadata
        {
            Application = Application,
            Class = NotificationClass.Critical,
            OwnerTeam = "identity-squad",
            Purpose = TemplateValidation.AuthenticationPurpose,
            LegalBasis = "execucao-de-contrato",
            DefaultLocale = PtBr,
            LinkDomainsAllowed = [AllowedDomain],
        }).Value!;

        var version = TemplateVersion.CreateDraft(key, Version, "autora", Start);
        version.SetContent(
                new ContentEdit(Sms, PtBr, null, "Código {{ code }} {{ link }}", null),
                "autora")
            .IsSuccess.ShouldBeTrue();
        return new PublishedTemplateContext(template, version);
    }

    private static TemplateManagementDbContext StoreOutOfReach()
        => new(new DbContextOptionsBuilder<TemplateManagementDbContext>().UseNpgsql().Options);
}
