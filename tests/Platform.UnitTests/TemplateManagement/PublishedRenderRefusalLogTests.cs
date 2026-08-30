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
/// What a sandbox refusal leaves behind on the dispatch path. The engine knows
/// which ceiling refused and never which template it was rendering; this is the
/// only place the two meet.
/// </summary>
public sealed class PublishedRenderRefusalLogTests
{
    private const string Application = "araia-cambio";
    private const string Key = "auth.otp.login";
    private const int Version = 7;

    /// <summary>Loops past a ceiling tightened so the message names a small number.</summary>
    private const string RunawayLoop = "{{ for i in 1..100000 }}x{{ end }}";

    private const char FieldSeparator = (char)0x1F;

    private static readonly DateTimeOffset Start = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly Channel Email = Channel.Create("email").Value!;
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

    private static async Task<Result<PublishedTemplateRender>> RenderAsync(
        ILogger<PublishedTemplateRenderer> logger,
        string body)
    {
        using var cache = new PublishedReadCache(TimeProvider.System);
        using TemplateManagementDbContext store = StoreOutOfReach();
        var renderer = new PublishedTemplateRenderer(
            store,
            new ScribanTemplateEngine(
                Options.Create(new TemplatingOptions { LoopLimit = 10 }),
                new ScribanParseCache()),
            cache,
            new PublishedContextLoader(store, cache),
            logger);

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

    private static JsonElement VariablesPayload()
    {
        using var document = JsonDocument.Parse("""{"name":"Ana"}""");
        return document.RootElement.Clone();
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

        TemplateVersion version = TemplateVersion.CreateDraft(key, Version, "autora", Start);
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
