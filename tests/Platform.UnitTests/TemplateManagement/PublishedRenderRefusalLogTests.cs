using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// What a refused published render leaves behind on the dispatch path. The
/// engine knows which ceiling refused and never which template it was
/// rendering, and the two refusals the renderer words for itself carry no
/// identity on the result axis at all: the log line is the only place a render
/// and the reason it stopped ever meet.
/// </summary>
public sealed class PublishedRenderRefusalLogTests
{
    private const string Application = "araia-cambio";
    private const string Key = "auth.otp.login";
    private const int Version = 7;

    /// <summary>Loops past a ceiling tightened so the message names a small number.</summary>
    private const string RunawayLoop = "{{ for i in 1..100000 }}x{{ end }}";

    /// <summary>
    /// A host the template itself allows. The destination guard runs over the
    /// variables payload before anything renders, so a host from outside the
    /// allowlist is refused there and the ban below never answers: the case
    /// that reaches the ban is an approved domain reaching an authentication
    /// SMS, which is the shape the alarm exists for.
    /// </summary>
    private const string AllowedDomain = "banco.exemplo.br";

    private const string LayoutKeyValue = "marca.transacional";
    private const int PinnedLayoutVersion = 3;

    private const char FieldSeparator = (char)0x1F;

    private static readonly DateTimeOffset Start = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly Channel Email = Channel.Create("email").Value!;
    private static readonly Channel Sms = Channel.Create("sms").Value!;
    private static readonly Locale PtBr = Locale.Create("pt-BR").Value!;

    /// <summary>
    /// Vocabulary that only the engine writes. None of it may reach a log line:
    /// it is English against a module whose log dialect is not, the audit trail
    /// already bars the same text by an executable scan, and it can carry a
    /// caller value that redaction did not reach.
    /// </summary>
    private static readonly string[] EngineVocabulary =
    [
        "LoopLimit",
        "iteration limit",
        "recursive depth limit",
        "null object",
        "<input>(",
        "error :",
    ];

    [Fact]
    public async Task A_refused_field_leaves_one_event_that_names_the_render_and_the_mode()
    {
        var logger = new RecordingLogger<PublishedTemplateRenderer>();
        Result<PublishedTemplateRender> rendered = await RenderAsync(logger, body: RunawayLoop);

        rendered.IsFailure.ShouldBeTrue();

        RecordedLog entry = logger.Entries.ShouldHaveSingleItem();
        entry.EventId.Id.ShouldBe(2112);
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Value("Application").ShouldBe(Application);
        entry.Value("TemplateKey").ShouldBe(Key);
        entry.Value("Version").ShouldBe(Version);
        entry.Value("Channel").ShouldBe(Email.Value);
        entry.Value("ResolvedLocale").ShouldBe(PtBr.Value);
        entry.Value("Field").ShouldBe(TemplateContentFields.Body);
        entry.Value("Mode").ShouldBe(TemplateRefusal.Unclassified);
    }

    [Fact]
    public async Task The_event_text_carries_no_word_the_engine_wrote()
    {
        var logger = new RecordingLogger<PublishedTemplateRenderer>();
        await RenderAsync(logger, body: RunawayLoop);

        var message = logger.Entries.ShouldHaveSingleItem().Message;
        foreach (var vocabulary in EngineVocabulary)
        {
            message.Contains(vocabulary, StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
                $"a linha de log repetiu o vocabulário do motor '{vocabulary}': {message}");
        }
    }

    [Fact]
    public async Task A_render_that_finishes_leaves_no_event_at_all()
    {
        var logger = new RecordingLogger<PublishedTemplateRenderer>();
        Result<PublishedTemplateRender> rendered = await RenderAsync(logger, body: "Olá {{ name }}");

        rendered.IsSuccess.ShouldBeTrue(rendered.Error);
        rendered.Value!.Full.Body.ShouldBe("Olá Ana");
        logger.Entries.ShouldBeEmpty();
    }

    /// <summary>
    /// The carve-out. A sibling module compares the whole error string for
    /// equality, so the refusal that travels on the result axis may not shift by
    /// a character while a second axis is added beside it. Spelled out in full
    /// rather than matched loosely, because a loose match is exactly what would
    /// let it shift.
    /// </summary>
    [Fact]
    public async Task The_refusal_that_reaches_the_consuming_module_is_unchanged()
    {
        var logger = new RecordingLogger<PublishedTemplateRenderer>();
        Result<PublishedTemplateRender> rendered = await RenderAsync(logger, body: RunawayLoop);

        rendered.IsFailure.ShouldBeTrue();
        rendered.ErrorKind.ShouldBe(ResultErrorKind.Validation);
        rendered.Error.ShouldBe(
            "template-render-failed"
            + FieldSeparator
            + "Field 'body': <input>(1,14) : error : Range expression exceeds LoopLimit `10`.");
    }

    /// <summary>
    /// The alarm the security refusal owes. Publication already refuses a
    /// template whose source offers a link, so a render that produces one means
    /// the link arrived through a variable value at request time, and the
    /// message never leaves. What the alarm carries is the only identity there
    /// is: the refusal travels to the consuming module as a bare word, with no
    /// application, no key and no version anywhere on it.
    /// </summary>
    [Fact]
    public async Task The_security_refusal_of_an_authentication_sms_raises_an_alarm_that_names_the_render()
    {
        var logger = new RecordingLogger<PublishedTemplateRenderer>();
        Result<PublishedTemplateRender> refused = await RenderAuthenticationSmsAsync(logger);

        // The refusal identity first, because the destination guard runs
        // earlier over the same payload: reaching the ban at all is what says
        // the arrangement measured the rule this test is about.
        refused.IsFailure.ShouldBeTrue();
        refused.Error.ShouldBe(TemplateValidation.AuthenticationSmsLinkCode);

        RecordedLog entry = logger.Entries.ShouldHaveSingleItem();
        entry.EventId.Id.ShouldBe(2110);
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Value("Application").ShouldBe(Application);
        entry.Value("TemplateKey").ShouldBe(Key);
        entry.Value("Version").ShouldBe(Version);
    }

    /// <summary>
    /// The event a disabled layout owes. Every template that pins the layout
    /// stops at once and each of them refuses with the same bare word, so
    /// without this line an operator reading the refusals cannot tell which
    /// wrapper was taken out of service, nor that one act stopped all of them.
    /// </summary>
    [Fact]
    public async Task The_layout_refusal_leaves_an_event_that_names_the_layout_taken_out_of_service()
    {
        var logger = new RecordingLogger<PublishedTemplateRenderer>();
        Result<PublishedTemplateRender> refused = await RenderWithDisabledLayoutAsync(logger);

        refused.IsFailure.ShouldBeTrue();
        refused.Error.ShouldBe(LayoutRejectionReasons.Disabled);

        RecordedLog entry = logger.Entries.ShouldHaveSingleItem();
        entry.EventId.Id.ShouldBe(2111);
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Value("LayoutKey").ShouldBe(LayoutKeyValue);
        entry.Value("LayoutVersion").ShouldBe(PinnedLayoutVersion);
    }

    private static async Task<Result<PublishedTemplateRender>> RenderAsync(
        ILogger<PublishedTemplateRenderer> logger,
        string body)
    {
        using var cache = new PublishedReadCache(TimeProvider.System);
        using TemplateManagementDbContext store = StoreOutOfReach();
        PublishedTemplateRenderer renderer = RendererOver(store, cache, logger);

        // The store is deliberately out of reach: everything the render needs is
        // in memory, so a read that went past it would raise instead of quietly
        // answering from a database this test never provided.
        cache.SetPointer($"render-context:{Application}:{Key}", ContextWith(body));

        return await renderer.RenderAsync(
            new PublishedRenderRequest
            {
                Application = Application,
                TemplateKey = Key,
                Channel = Email.Value,
                Locale = PtBr.Value,
                Variables = VariablesPayload(),
            },
            CancellationToken.None);
    }

    private static async Task<Result<PublishedTemplateRender>> RenderAuthenticationSmsAsync(
        ILogger<PublishedTemplateRenderer> logger)
    {
        using var cache = new PublishedReadCache(TimeProvider.System);
        using TemplateManagementDbContext store = StoreOutOfReach();
        PublishedTemplateRenderer renderer = RendererOver(store, cache, logger);
        cache.SetPointer($"render-context:{Application}:{Key}", AuthenticationSmsContext());

        return await renderer.RenderAsync(
            new PublishedRenderRequest
            {
                Application = Application,
                TemplateKey = Key,
                Channel = Sms.Value,
                Locale = PtBr.Value,
                Variables = AllowedLinkVariablesPayload(),
            },
            CancellationToken.None);
    }

    private static async Task<Result<PublishedTemplateRender>> RenderWithDisabledLayoutAsync(
        ILogger<PublishedTemplateRenderer> logger)
    {
        using var cache = new PublishedReadCache(TimeProvider.System);
        using TemplateManagementDbContext store = StoreOutOfReach();
        PublishedTemplateRenderer renderer = RendererOver(store, cache, logger);
        cache.SetPointer($"render-context:{Application}:{Key}", PinnedLayoutContext());

        // Only the identity is in memory, and the store is out of reach: the
        // refusal has to come from the status alone, without a read of the
        // version the template pins.
        cache.SetPointer(
            $"layout-identity:{LayoutKeyValue}",
            new LayoutIdentity(LayoutStatus.Disabled, PtBr));

        return await renderer.RenderAsync(
            new PublishedRenderRequest
            {
                Application = Application,
                TemplateKey = Key,
                Channel = Email.Value,
                Locale = PtBr.Value,
                Variables = VariablesPayload(),
            },
            CancellationToken.None);
    }

    private static PublishedTemplateRenderer RendererOver(
        TemplateManagementDbContext store,
        PublishedReadCache cache,
        ILogger<PublishedTemplateRenderer> logger)
        => new(
            store,
            new ScribanTemplateEngine(
                Options.Create(new TemplatingOptions { LoopLimit = 10 }),
                new ScribanParseCache()),
            cache,
            new PublishedContextLoader(store, cache),
            logger);

    private static JsonElement VariablesPayload()
    {
        using var document = JsonDocument.Parse("""{"name":"Ana"}""");
        return document.RootElement.Clone();
    }

    /// <summary>
    /// A payload whose link points at a domain the template approved, so the
    /// destination guard that runs first has nothing to refuse and the render
    /// reaches the ban.
    /// </summary>
    private static JsonElement AllowedLinkVariablesPayload()
    {
        using var document = JsonDocument.Parse(
            $$"""{"code":"834192","link":"https://{{AllowedDomain}}/ajuda"}""");
        return document.RootElement.Clone();
    }

    /// <summary>
    /// An authentication template whose SMS content offers no link of its own:
    /// what makes the render produce one is a variable value, which is the only
    /// way this content can reach a recipient at all, because publication
    /// refuses a source that carries the link itself.
    /// </summary>
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
                new ContentEdit(Sms, PtBr, null, "Seu código é {{ code }}. Detalhes em {{ link }}", null),
                "autora")
            .IsSuccess.ShouldBeTrue();
        return new PublishedTemplateContext(template, version);
    }

    private static PublishedTemplateContext PinnedLayoutContext()
    {
        PublishedTemplateContext context = ContextWith("Olá {{ name }}");
        context.Version
            .SetLayoutReference(LayoutKey.Trusted(LayoutKeyValue), PinnedLayoutVersion, "autora")
            .IsSuccess.ShouldBeTrue();
        return context;
    }

    private static PublishedTemplateContext ContextWith(string body)
    {
        TemplateKey key = TemplateKey.Create(Key).Value!;
        Template template = Template.Create(key, new TemplateMetadata
        {
            Application = Application,
            Class = NotificationClass.Critical,
            OwnerTeam = "identity-squad",
            Purpose = "authentication",
            LegalBasis = "execucao-de-contrato",
            DefaultLocale = PtBr,
        }).Value!;

        var version = TemplateVersion.CreateDraft(key, Version, "autora", Start);
        version.SetContent(new ContentEdit(Email, PtBr, "Assunto", body, null), "autora")
            .IsSuccess.ShouldBeTrue();
        return new PublishedTemplateContext(template, version);
    }

    private static TemplateManagementDbContext StoreOutOfReach()
        => new(new DbContextOptionsBuilder<TemplateManagementDbContext>().UseNpgsql().Options);

    /// <summary>One log line, with the structure a formatter never shows.</summary>
    private sealed record RecordedLog(
        LogLevel Level,
        EventId EventId,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> State)
    {
        internal object? Value(string name)
            => State.Single(pair => string.Equals(pair.Key, name, StringComparison.Ordinal)).Value;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        internal List<RecordedLog> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            // The tags are copied here and not kept by reference: the generated
            // call site writes them into a state object it reuses per thread and
            // clears the moment this returns, so a reference read afterwards is
            // an empty list every time.
            => Entries.Add(new RecordedLog(
                logLevel,
                eventId,
                formatter(state, exception),
                state is IReadOnlyList<KeyValuePair<string, object?>> tags ? [.. tags] : []));
    }
}
