using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class PublishedReadMemoizationTests
{
    private const string Application = "araia-cambio";
    private const string Key = "auth.otp.login";

    private static readonly DateTimeOffset Start = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

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
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
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
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        cache.SetImmutable("layout:footer:3", "layout-body");

        clock.Now = clock.Now.AddDays(30);

        cache.TryGetImmutable("layout:footer:3", out string value).ShouldBeTrue();
        value.ShouldBe("layout-body");
    }

    [Fact]
    public async Task The_catalog_answers_a_padded_template_identity_from_the_canonical_entry()
    {
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        using TemplateManagementDbContext store = StoreOutOfReach();
        var catalog = new PublishedCatalog(store, cache);

        // The first lookup has nothing in memory and reaches for a store this
        // test deliberately does not provide. That refusal is the tripwire: any
        // later lookup that fails to land on the entry below raises it again.
        await Assert.ThrowsAnyAsync<Exception>(
            () => catalog.FindTemplateAsync($"  {Application} ", $" {Key}  ", CancellationToken.None));

        cache.SetPointer<PublishedTemplateLookup>(
            $"template:{Application}:{Key}", new PublishedTemplateLookup.Published(PublishedTemplateOf()));

        Result<PublishedTemplateLookup> answered = await catalog.FindTemplateAsync(
            $" {Application}", $"{Key} ", CancellationToken.None);

        answered.IsSuccess.ShouldBeTrue();
        ((PublishedTemplateLookup.Published)answered.Value!).Template.TemplateKey.ShouldBe(Key);
        cache.PointerLoads.ShouldBe(1);
        cache.PointerHits.ShouldBe(1);
    }

    [Fact]
    public async Task The_catalog_answers_a_padded_class_policy_identity_from_the_canonical_entry()
    {
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        using TemplateManagementDbContext store = StoreOutOfReach();
        var catalog = new PublishedCatalog(store, cache);

        await Assert.ThrowsAnyAsync<Exception>(
            () => catalog.FindClassPolicyAsync(
                $" {Application}  ", $"  {NotificationClasses.Critical} ", CancellationToken.None));

        cache.SetPointer($"policy:{Application}:{NotificationClasses.Critical}", PublishedPolicyOf());

        Result<PublishedClassPolicy> answered = await catalog.FindClassPolicyAsync(
            $"{Application} ", $" {NotificationClasses.Critical}", CancellationToken.None);

        answered.IsSuccess.ShouldBeTrue();
        answered.Value!.Class.ShouldBe(NotificationClasses.Critical);
        cache.PointerLoads.ShouldBe(1);
        cache.PointerHits.ShouldBe(1);
    }

    [Fact]
    public async Task The_renderer_answers_a_padded_template_identity_from_the_canonical_entry()
    {
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        using TemplateManagementDbContext store = StoreOutOfReach();
        PublishedTemplateRenderer renderer = RendererOver(store, cache, new PublishedContextLoader(store, cache));

        await Assert.ThrowsAnyAsync<Exception>(
            () => renderer.RenderAsync(
                RenderRequest($"  {Application} ", $" {Key}  "), CancellationToken.None));

        cache.SetPointer($"render-context:{Application}:{Key}", PublishedContextOf());

        // The render itself stops on the version having no content, which is a
        // decision taken after the context is in hand: reaching it at all proves
        // the padded identity landed on the entry above.
        Result<PublishedTemplateRender> rendered = await renderer.RenderAsync(
            RenderRequest($" {Application}", $"{Key} "), CancellationToken.None);

        rendered.IsFailure.ShouldBeTrue();
        cache.PointerLoads.ShouldBe(1);
        cache.PointerHits.ShouldBe(1);
    }

    [Fact]
    public async Task The_validator_answers_a_padded_template_identity_from_the_canonical_entry()
    {
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        using TemplateManagementDbContext store = StoreOutOfReach();
        var validator = new PublishedVariablesValidator(new PublishedContextLoader(store, cache));

        // Same tripwire as the sibling contracts: the first call has nothing in
        // memory and reaches for a store this test deliberately does not
        // provide, so a later call that misses the entry raises it again.
        await Assert.ThrowsAnyAsync<Exception>(
            () => validator.ValidateAsync(
                $"  {Application} ", $" {Key}  ", Variables("""{"orderId":"42"}"""), CancellationToken.None));

        cache.SetPointer($"render-context:{Application}:{Key}", PublishedContextOf());

        Result<VariablesValidationReport> report = await validator.ValidateAsync(
            $" {Application}", $"{Key} ", Variables("""{"cupom":"MB10"}"""), CancellationToken.None);

        // The report is the one the memoized version's schema produces: it names
        // the variable that schema never declared.
        report.IsSuccess.ShouldBeTrue(report.Error);
        VariablesValidationCheck declared = report.Value!.Checks.Single(
            check => check.Name == ValidationCheckNames.VariablesDeclared);
        declared.Status.ShouldBe(VariablesValidationStatuses.Failed);
        declared.Message.ShouldContain("'cupom'");
        cache.PointerLoads.ShouldBe(1);
        cache.PointerHits.ShouldBe(1);
    }

    [Fact]
    public async Task The_validator_and_the_renderer_read_one_memoized_published_context()
    {
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        using TemplateManagementDbContext store = StoreOutOfReach();
        var loader = new PublishedContextLoader(store, cache);
        var validator = new PublishedVariablesValidator(loader);
        PublishedTemplateRenderer renderer = RendererOver(store, cache, loader);
        cache.SetPointer($"render-context:{Application}:{Key}", PublishedContextOf());

        Result<VariablesValidationReport> report = await validator.ValidateAsync(
            Application, Key, Variables("""{"orderId":"42"}"""), CancellationToken.None);
        Result<PublishedTemplateRender> rendered = await renderer.RenderAsync(
            RenderRequest(Application, Key), CancellationToken.None);

        // One entry answered both, and neither reached a store this test does
        // not provide: the render stops on the version having no content, which
        // is a decision taken after the context is already in hand.
        report.IsSuccess.ShouldBeTrue(report.Error);
        rendered.ErrorKind.ShouldBe(ResultErrorKind.NotFound);
        cache.PointerHits.ShouldBe(2);
        cache.PointerLoads.ShouldBe(0);
    }

    [Fact]
    public async Task The_validator_goes_back_to_the_store_once_the_pointer_window_closes()
    {
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        using TemplateManagementDbContext store = StoreOutOfReach();
        var validator = new PublishedVariablesValidator(new PublishedContextLoader(store, cache));
        cache.SetPointer($"render-context:{Application}:{Key}", PublishedContextOf());

        Result<VariablesValidationReport> inside = await validator.ValidateAsync(
            Application, Key, Variables("""{"orderId":"42"}"""), CancellationToken.None);

        // Reading the published context in memory means reading it as stale as
        // the pointer window allows, exactly like the sibling contracts: once
        // the window closes the next validation pays for the store again.
        clock.Now = clock.Now.Add(PublishedReadCache.PointerLifetime).AddSeconds(1);
        await Assert.ThrowsAnyAsync<Exception>(
            () => validator.ValidateAsync(
                Application, Key, Variables("""{"orderId":"42"}"""), CancellationToken.None));

        inside.IsSuccess.ShouldBeTrue(inside.Error);
        cache.PointerHits.ShouldBe(1);
        cache.PointerLoads.ShouldBe(1);
    }

    [Fact]
    public async Task The_validator_refuses_a_template_key_the_domain_rejects_without_reaching_the_store()
    {
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        using TemplateManagementDbContext store = StoreOutOfReach();
        var validator = new PublishedVariablesValidator(new PublishedContextLoader(store, cache));

        Result<VariablesValidationReport> refused = await validator.ValidateAsync(
            Application, "Auth OTP", Variables("""{"orderId":"42"}"""), CancellationToken.None);

        refused.IsFailure.ShouldBeTrue();
        refused.ErrorKind.ShouldBe(ResultErrorKind.Validation);
        refused.Error.ShouldBe(DomainError.Format(
            ErrorCodes.InvalidRequest,
            $"A template key must be 1-{TemplateKey.MaxLength} lowercase alphanumeric characters "
            + "in segments separated by '.', '-' or '_'."));
        cache.PointerLoads.ShouldBe(0);
        cache.PointerCount.ShouldBe(0);
    }

    [Fact]
    public async Task An_application_the_domain_refuses_never_reaches_the_store_and_takes_no_slot()
    {
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        using TemplateManagementDbContext store = StoreOutOfReach();
        var catalog = new PublishedCatalog(store, cache);

        Result<PublishedTemplateLookup> refused =
            await catalog.FindTemplateAsync("Araia Cambio", Key, CancellationToken.None);

        refused.IsFailure.ShouldBeTrue();
        refused.ErrorKind.ShouldBe(ResultErrorKind.Validation);
        cache.PointerLoads.ShouldBe(0);
        cache.PointerHits.ShouldBe(0);
        cache.PointerCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_class_the_domain_refuses_never_reaches_the_store_and_takes_no_slot()
    {
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        using TemplateManagementDbContext store = StoreOutOfReach();
        var catalog = new PublishedCatalog(store, cache);

        Result<PublishedClassPolicy> refused =
            await catalog.FindClassPolicyAsync(Application, "urgentissimo", CancellationToken.None);

        refused.IsFailure.ShouldBeTrue();
        refused.ErrorKind.ShouldBe(ResultErrorKind.Validation);
        cache.PointerLoads.ShouldBe(0);
        cache.PointerHits.ShouldBe(0);
        cache.PointerCount.ShouldBe(0);
    }

    [Fact]
    public void Crossing_the_pointer_ceiling_frees_a_slice_instead_of_the_whole_working_set()
    {
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        var ceiling = PublishedReadCache.MaxEntries;

        for (var index = 0; index <= ceiling; index++)
        {
            cache.SetPointer($"template:{Application}:key-{index}", $"value-{index}");
        }

        // The budget is an admission gate, so the resident set is bounded the
        // instant the last write returns, before anything is freed.
        cache.PointerCount.ShouldBeLessThanOrEqualTo(ceiling);
        SettleCompaction(cache);

        var survivors = 0;
        for (var index = 0; index <= ceiling; index++)
        {
            if (cache.TryGetPointer($"template:{Application}:key-{index}", out string _))
            {
                survivors++;
            }
        }

        // One pass frees the declared share of the budget. Two passes' worth of
        // slack keeps the bound honest on a busy host and still sits an order of
        // magnitude above the one entry a wipe of the whole family would leave.
        var slack = (int)Math.Ceiling(ceiling * PublishedReadCache.CompactionShare) * 2;
        survivors.ShouldBeGreaterThan(ceiling - slack);
        cache.PointerCount.ShouldBeLessThan(ceiling);
    }

    [Fact]
    public void Pointers_whose_window_closed_give_their_slots_back_at_the_ceiling()
    {
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        var ceiling = PublishedReadCache.MaxEntries;

        for (var index = 0; index < ceiling; index++)
        {
            cache.SetPointer($"template:{Application}:key-{index}", $"value-{index}");
        }

        cache.PointerCount.ShouldBe(ceiling);
        clock.Now = clock.Now.Add(PublishedReadCache.PointerLifetime).AddSeconds(1);

        // Nothing in the family can answer any more, yet every entry still holds
        // the slot it took. Arriving at a full budget is what hands them back.
        cache.SetPointer($"template:{Application}:after-the-window", "fresh");
        cache.ReclaimExpiredPointers();

        cache.PointerCount.ShouldBeLessThan(ceiling);
        cache.SetPointer($"template:{Application}:after-the-window", "fresh");
        cache.TryGetPointer($"template:{Application}:after-the-window", out string value).ShouldBeTrue();
        value.ShouldBe("fresh");
    }

    [Fact]
    public void Pointer_pressure_leaves_the_pinned_layouts_alone()
    {
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        cache.SetImmutable("layout:footer:3", "layout-body");

        for (var index = 0; index <= PublishedReadCache.MaxEntries; index++)
        {
            cache.SetPointer($"template:{Application}:key-{index}", $"value-{index}");
        }

        SettleCompaction(cache);

        cache.TryGetImmutable("layout:footer:3", out string layout).ShouldBeTrue();
        layout.ShouldBe("layout-body");
        cache.ImmutableCount.ShouldBe(1);
    }

    [Fact]
    public void The_first_value_of_an_immutable_key_is_the_one_that_stays()
    {
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        var first = new string[] { "first" };
        var second = new string[] { "second" };

        cache.SetImmutable("layout:footer:3", first);
        cache.SetImmutable("layout:footer:3", second);

        cache.TryGetImmutable("layout:footer:3", out string[] stored).ShouldBeTrue();
        stored.ShouldBeSameAs(first);
    }

    [Fact]
    public void The_two_registrations_of_the_cache_compose_one_instance_the_container_owns()
    {
        // The order the solution composes: the module registers the memoization
        // outright, and the read surface a worker role composes only fills the
        // gap when the module is absent from that host.
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<PublishedReadCache>();
        services.TryAddSingleton<PublishedReadCache>();

        PublishedReadCache resolved;
        using (ServiceProvider provider = services.BuildServiceProvider())
        {
            resolved = provider.GetRequiredService<PublishedReadCache>();
            resolved.ShouldBeSameAs(provider.GetRequiredService<PublishedReadCache>());
            provider.GetServices<PublishedReadCache>().Count().ShouldBe(1);
        }

        // Each family owns a store the container has to release, so the single
        // instance has to be the one the container disposes.
        Should.Throw<ObjectDisposedException>(
            () => resolved.TryGetPointer("template:app:key", out string _));
    }

    private static void SettleCompaction(PublishedReadCache cache)
    {
        // The store schedules the pass rather than running it inline, so the
        // resident set keeps shrinking for a moment after the write that filled
        // the budget returned.
        var settled = SpinWait.SpinUntil(
            () => cache.PointerCount < PublishedReadCache.MaxEntries, TimeSpan.FromSeconds(10));
        settled.ShouldBeTrue("a compactação agendada não devolveu orçamento dentro da espera");
    }

    private static PublishedTemplateRenderer RendererOver(
        TemplateManagementDbContext store,
        PublishedReadCache cache,
        PublishedContextLoader loader)
        => new(
            store,
            new ScribanTemplateEngine(Options.Create(new TemplatingOptions()), new ScribanParseCache()),
            cache,
            loader,
            NullLogger<PublishedTemplateRenderer>.Instance);

    private static TemplateManagementDbContext StoreOutOfReach()
        => new(new DbContextOptionsBuilder<TemplateManagementDbContext>().UseNpgsql().Options);

    private static PublishedTemplate PublishedTemplateOf() => new()
    {
        Application = Application,
        TemplateKey = Key,
        Class = NotificationClasses.Critical,
        OwnerTeam = "identity-squad",
        Purpose = "authentication",
        LegalBasis = "execucao-de-contrato",
        SensitiveVariables = [],
        ChannelsWithContent = [Channel.Email],
        Version = 3,
        ContentHash = "content-hash",
    };

    private static PublishedClassPolicy PublishedPolicyOf() => new()
    {
        Application = Application,
        Class = NotificationClasses.Critical,
        Version = 2,
        ContentHash = "content-hash",
        Definition = new ClassPolicyDefinition
        {
            SchemaVersion = 1,
            ChannelsAllowed = [Channel.Email],
            DeliveryPlan = [new DeliveryPlanStep(Channel.Email, null)],
            DefaultTtl = TimeSpan.FromHours(1),
            DedupeWindow = TimeSpan.FromMinutes(10),
        },
    };

    private static PublishedTemplateContext PublishedContextOf()
    {
        TemplateKey key = TemplateKey.Create(Key).Value!;
        Template template = Template.Create(key, new TemplateMetadata
        {
            Application = Application,
            Class = NotificationClass.Critical,
            OwnerTeam = "identity-squad",
            Purpose = "authentication",
            LegalBasis = "execucao-de-contrato",
        }).Value!;
        TemplateVersion version = TemplateVersion.CreateDraft(key, 1, "autora", Start);

        // The schema is what a validation report is computed against, so the
        // memoized context has to carry one for the report to say anything.
        version.SetVariablesSchema(
            """{"type":"object","properties":{"orderId":{"type":"string"}}}""",
            "autora").IsSuccess.ShouldBeTrue();
        return new PublishedTemplateContext(template, version);
    }

    private static PublishedRenderRequest RenderRequest(string application, string templateKey) => new()
    {
        Application = application,
        TemplateKey = templateKey,
        Channel = Channel.Email.Value,
        Locale = "pt-BR",
    };

    private static JsonElement Variables(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
