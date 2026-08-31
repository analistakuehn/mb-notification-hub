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
    private const string LayoutKeyValue = "email.base";
    private const int PinnedVersion = 3;

    private static readonly DateTimeOffset Start = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly Channel Email = Channel.Create("email").Value!;
    private static readonly Locale PtBr = Locale.Create("pt-BR").Value!;

    private sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// A clock that runs one armed action the first time it is read. The store
    /// reads the clock while it writes a pointer entry, to stamp the entry's
    /// expiration, and that read is the one deterministic seam this surface
    /// offers inside a write: it lets a single-threaded test place an
    /// invalidation in the middle of one, instead of racing threads and hoping
    /// for the losing order.
    /// </summary>
    private sealed class InterleavingClock(DateTimeOffset start) : TimeProvider
    {
        private Action? _armed;

        public DateTimeOffset Now { get; set; } = start;

        /// <summary>How many times an armed action ran. Zero means the seam is gone.</summary>
        public int Fired { get; private set; }

        public void ArmOnce(Action action) => _armed = action;

        public override DateTimeOffset GetUtcNow()
        {
            // Disarmed before running, so an action that reads the clock again
            // through the store does not re-enter this branch.
            Action? armed = _armed;
            if (armed is not null)
            {
                _armed = null;
                Fired++;
                armed();
            }

            return Now;
        }
    }

    [Fact]
    public async Task Rendering_the_same_source_twice_parses_once()
    {
        var cache = new ScribanParseCache();
        var engine = new ScribanTemplateEngine(Options.Create(new TemplatingOptions()), cache);

        // Through the published path on purpose: it is the one whose sources
        // are immutable per version, and the only one that writes to the
        // memoization.
        Result<string> first = await engine.RenderAsync(
            engine.BeginForm(), "Olá {{ name }}", Variables("""{"name":"Ana"}"""), CancellationToken.None);
        Result<string> second = await engine.RenderAsync(
            engine.BeginForm(), "Olá {{ name }}", Variables("""{"name":"Bia"}"""), CancellationToken.None);

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

        Result<string> first = await engine.RenderAsync(
            engine.BeginForm(), "{{ 1 + }}", null, CancellationToken.None);
        Result<string> second = await engine.RenderAsync(
            engine.BeginForm(), "{{ 1 + }}", null, CancellationToken.None);

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
    public async Task The_status_of_a_pinned_layout_is_re_read_once_the_pointer_window_closes()
    {
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        using TemplateManagementDbContext store = StoreOutOfReach();
        PublishedTemplateRenderer renderer = RendererOver(store, cache, new PublishedContextLoader(store, cache));
        cache.SetPointer($"render-context:{Application}:{Key}", PublishedContextWithLayoutOf());
        cache.SetImmutable($"layout-version:{LayoutKeyValue}:{PinnedVersion}", PinnedLayoutVersionOf());
        cache.SetPointer($"layout-identity:{LayoutKeyValue}", new LayoutIdentity(LayoutStatus.Active, PtBr));

        Result<PublishedTemplateRender> inside = await renderer.RenderAsync(
            RenderRequest(Application, Key), CancellationToken.None);

        // How far the refusal reaches back is a window, not an instant: a
        // layout disabled after a render already framed a message keeps
        // framing it until the pointer expires, which is the staleness the
        // published reads already accept for the template itself. What no
        // window may cover is the per-version entry, which never expires, and
        // that is why the status cannot live in it. Only a clock a test owns
        // can say this: with a real one the assertion would cost a minute of
        // sleeping, which is why it is made here and not against the store.
        clock.Now = clock.Now.Add(PublishedReadCache.PointerLifetime).AddSeconds(1);
        cache.SetPointer($"render-context:{Application}:{Key}", PublishedContextWithLayoutOf());
        await Assert.ThrowsAnyAsync<Exception>(
            () => renderer.RenderAsync(RenderRequest(Application, Key), CancellationToken.None));

        inside.IsSuccess.ShouldBeTrue(inside.Error);
        inside.Value!.Full.Body.ShouldBe("<html>corpo</html>");

        // The version the render just refused to answer for is still resident:
        // what expired, and what the second render went to the store for, is
        // the status alone.
        cache.TryGetImmutable($"layout-version:{LayoutKeyValue}:{PinnedVersion}", out LayoutVersion pinned)
            .ShouldBeTrue();
        pinned.Version.ShouldBe(PinnedVersion);
    }

    [Fact]
    public async Task A_pinned_layout_whose_identity_is_disabled_refuses_the_render_from_memory()
    {
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        using TemplateManagementDbContext store = StoreOutOfReach();
        PublishedTemplateRenderer renderer = RendererOver(store, cache, new PublishedContextLoader(store, cache));
        cache.SetPointer($"render-context:{Application}:{Key}", PublishedContextWithLayoutOf());
        cache.SetPointer($"layout-identity:{LayoutKeyValue}", new LayoutIdentity(LayoutStatus.Disabled, PtBr));

        Result<PublishedTemplateRender> refused = await renderer.RenderAsync(
            RenderRequest(Application, Key), CancellationToken.None);

        // Nothing about the pinned version is in memory and the store is out
        // of reach, so arriving at the refusal at all proves the identity
        // answered first: refusing costs no read of the version, and leaves
        // behind no entry that never expires for a layout just refused.
        refused.IsFailure.ShouldBeTrue();
        refused.ErrorKind.ShouldBe(ResultErrorKind.BusinessRule);
        refused.Error.ShouldBe(LayoutRejectionReasons.Disabled);
        cache.ImmutableCount.ShouldBe(0);
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
    public void Invalidating_a_pointer_key_drops_only_that_key()
    {
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        var dropped = PublishedPointerKeys.Template(Application, Key);
        var kept = PublishedPointerKeys.Template(Application, "other.template");
        cache.SetPointer(dropped, "published-v1");
        cache.SetPointer(kept, "untouched");

        cache.InvalidatePointer(dropped);

        cache.TryGetPointer(dropped, out string _).ShouldBeFalse();
        cache.TryGetPointer(kept, out string surviving).ShouldBeTrue();
        surviving.ShouldBe("untouched");
    }

    [Fact]
    public void Invalidating_a_pointer_key_leaves_the_per_version_family_untouched()
    {
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        var identity = PublishedPointerKeys.LayoutIdentity(LayoutKeyValue);
        var pinned = PublishedPointerKeys.LayoutVersion(LayoutKeyValue, PinnedVersion);
        cache.SetPointer(identity, "active");
        cache.SetImmutable(pinned, "layout-body");

        cache.InvalidatePointer(identity);

        cache.TryGetImmutable(pinned, out string body).ShouldBeTrue();
        body.ShouldBe("layout-body");
        cache.TryGetPointer(identity, out string _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_load_that_started_before_an_invalidation_does_not_repopulate_the_pointer()
    {
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        var key = PublishedPointerKeys.Template(Application, Key);

        // The order of a lost race, forced by the mechanism rather than by the
        // test: the fence is captured before the loader runs, so a transition
        // that commits inside the load refuses the write that follows it. A
        // reader that captured the fence after its query would land here.
        Result<string> loaded = await cache.ReadPointerAsync(key, () =>
        {
            cache.InvalidatePointer(key);
            return Task.FromResult(Result.Success("published-v1"));
        });

        loaded.Value.ShouldBe("published-v1");
        cache.TryGetPointer(key, out string _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_load_that_started_after_an_invalidation_repopulates_the_pointer()
    {
        // Falsification pair of the fence above: without it, refusing every
        // write would satisfy that assertion and leave the surface with no
        // memoization at all.
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        var key = PublishedPointerKeys.Template(Application, Key);
        cache.SetPointer(key, "published-v1");
        cache.InvalidatePointer(key);

        Result<string> loaded = await cache.ReadPointerAsync(
            key, () => Task.FromResult(Result.Success("published-v2")));

        loaded.Value.ShouldBe("published-v2");
        cache.TryGetPointer(key, out string value).ShouldBeTrue();
        value.ShouldBe("published-v2");
    }

    [Fact]
    public async Task An_invalidation_that_lands_while_the_write_is_in_flight_leaves_no_stale_pointer()
    {
        // The losing order of the race the fence exists for, forced instead of
        // raced: the loader returns, the write passes the first reading, and
        // the transition commits and drops the key while that write is still
        // in flight. The first reading alone cannot see it, so the write has
        // to end by looking again.
        var clock = new InterleavingClock(Start);
        using var cache = new PublishedReadCache(clock);
        var key = PublishedPointerKeys.Template(Application, Key);

        Result<string> loaded = await cache.ReadPointerAsync(key, () =>
        {
            // Armed from inside the load, so the seam it takes is the clock
            // read of the write and never one the lookup that missed took
            // before it.
            clock.ArmOnce(() => cache.InvalidatePointer(key));
            return Task.FromResult(Result.Success("published-v1"));
        });

        clock.Fired.ShouldBe(1, "sem a costura do relógio o teste não intercala nada e não prova nada");
        loaded.Value.ShouldBe("published-v1");
        cache.TryGetPointer(key, out string _).ShouldBeFalse();
    }

    [Fact]
    public async Task Invalidating_a_key_nobody_memoized_keeps_the_resident_and_drops_a_write_in_flight()
    {
        var clock = new SteppingClock(Start);
        using var cache = new PublishedReadCache(clock);
        var resident = PublishedPointerKeys.Template(Application, Key);
        var loading = PublishedPointerKeys.Template(Application, "other.template");
        cache.SetPointer(resident, "published-v1");

        // The fence moves all the same, which is the declared price of one
        // counter for every key: a write in flight for a key the invalidation
        // never named reloads instead of landing. What that costs is asserted
        // here, because the counter itself only restates the implementation.
        Result<string> loaded = await cache.ReadPointerAsync(loading, () =>
        {
            cache.InvalidatePointer(PublishedPointerKeys.Template(Application, "never.memoized"));
            return Task.FromResult(Result.Success("published-v2"));
        });

        loaded.Value.ShouldBe("published-v2");
        cache.TryGetPointer(loading, out string _).ShouldBeFalse();
        cache.TryGetPointer(resident, out string value).ShouldBeTrue();
        value.ShouldBe("published-v1");
        cache.PointerCount.ShouldBe(1);
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

    /// <summary>
    /// The published context of a version that ships email content and pins a
    /// layout version, which is what puts the layout resolution on the path
    /// the render walks.
    /// </summary>
    private static PublishedTemplateContext PublishedContextWithLayoutOf()
    {
        PublishedTemplateContext context = PublishedContextOf();
        context.Version.SetContent(new ContentEdit(Email, PtBr, "Assunto", "corpo", null), "autora")
            .IsSuccess.ShouldBeTrue();
        context.Version.SetLayoutReference(LayoutKey.Trusted(LayoutKeyValue), PinnedVersion, "autora")
            .IsSuccess.ShouldBeTrue();
        return context;
    }

    private static LayoutVersion PinnedLayoutVersionOf()
    {
        LayoutVersion version = LayoutVersion.CreateDraft(
            LayoutKey.Trusted(LayoutKeyValue), PinnedVersion, "autora", Start);
        version.SetContent(new LayoutContentEdit(Email, PtBr, "<html>{{ content }}</html>", null), "autora")
            .IsSuccess.ShouldBeTrue();
        return version;
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
