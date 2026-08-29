using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Retention;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>
/// Where the words of a lifecycle note end up, and where they do not. The
/// trail is append-only by trigger and hash-chained per partition, and the
/// WORM export writes the stored canonical text byte for byte, so a value
/// absent from the details and from the canonical text is absent from the
/// export too.
/// </summary>
[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class LifecycleNoteTrailTests(TemplateManagementApiFixture fixture)
{
    /// <summary>
    /// The three shapes an operator most plausibly types into a note while
    /// stopping traffic, and a token that could only have arrived by way of
    /// the note, because no other field of a lifecycle transition carries free
    /// text. The identifier and the address are synthetic: the digits fail
    /// their own check and the domain is a reserved one that cannot be
    /// registered.
    /// </summary>
    private const string Identifier = "000.000.000-00";

    private const string Address = "ana.silva@exemplo.test";

    private const string LeakProbe = "zxqvortex";

    private const string Note =
        $"cliente {Identifier} pediu remocao, contato {Address}, chamado {LeakProbe}";

    [RequiresDockerFact]
    public async Task The_words_of_a_lifecycle_note_never_reach_the_trail()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lnt-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-lnt-1");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());

        HttpResponseMessage response = await publisher.PostAsJsonAsync(
            $"/v1/templates/{key}/disable",
            new { reason = LifecycleReasons.Other, note = Note });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            AuditEvent audit = await SingleAsync(db, "template.disabled", key);
            foreach (var raw in new[] { Identifier, Address, LeakProbe })
            {
                audit.DetailsJson.Contains(raw, StringComparison.Ordinal).ShouldBeFalse(
                    "the details are the document an evidence read walks");
                audit.Canonical!.Contains(raw, StringComparison.Ordinal).ShouldBeFalse(
                    "the canonical text is what the hash covers and what the export ships");
            }

            // And a code an auditor can still act on, which is the half of the
            // claim that an empty details document would also satisfy.
            audit.DetailsJson.ShouldContain(LifecycleReasons.Other);
        });

        // The other half of the claim: the words were kept, in the one place
        // that can be made to forget them.
        await fixture.ExecuteDbAsync(async db =>
            (await db.LifecycleNotes.AsNoTracking().SingleAsync(note => note.SubjectKey == key))
                .Text.ShouldBe(Note));
    }

    [RequiresDockerFact]
    public async Task Erasing_a_note_removes_the_words_and_records_that_it_removed_them()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lnt-2");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-lnt-2");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());
        (await publisher.PostAsJsonAsync(
                $"/v1/templates/{key}/disable",
                new { reason = LifecycleReasons.Other, note = Note }))
            .EnsureSuccessStatusCode();
        Guid noteRef = await ReferenceOfAsync(key);

        LifecycleNoteErasure outcome = await EraseAsync(noteRef, "privacy-lnt-2");

        outcome.ShouldBe(LifecycleNoteErasure.Erased);
        await fixture.ExecuteDbAsync(async db =>
            (await db.LifecycleNotes.AsNoTracking().AnyAsync(note => note.Id == noteRef))
                .ShouldBeFalse());
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            // Without this event the reference of the transition would point at
            // nothing, and "no note was ever written" would read exactly like
            // "a note was written and then removed".
            AuditEvent erased = await SingleAsync(db, "template.lifecycle_note.erased", key);
            erased.ActorId.ShouldBe("privacy-lnt-2");
            erased.DetailsJson.ShouldContain(noteRef.ToString());
            erased.DetailsJson.Contains(LeakProbe, StringComparison.Ordinal).ShouldBeFalse(
                "an erasure that quotes what it erased has erased nothing");
            erased.Canonical!.Contains(LeakProbe, StringComparison.Ordinal).ShouldBeFalse(
                "the canonical text of the erasure is covered by the same chain");
        });
    }

    /// <summary>
    /// The pair that makes the assertion above mean something. If an erasure
    /// recorded an event whether or not it found prose, then the event proves
    /// only that the method ran, and the transition of a template that never
    /// carried a note would gain a record of a removal that never happened.
    /// </summary>
    [RequiresDockerFact]
    public async Task Erasing_a_reference_that_holds_no_words_records_nothing()
    {
        Guid absent = Guid.CreateVersion7();
        var before = await ErasureEventCountAsync();

        LifecycleNoteErasure outcome = await EraseAsync(absent, "privacy-lnt-3");

        outcome.ShouldBe(LifecycleNoteErasure.AlreadyAbsent);

        // Counted rather than searched by content: the details column is
        // jsonb, which the database re-serializes on read and offers no
        // pattern match over, so the question has to be asked of the action.
        (await ErasureEventCountAsync()).ShouldBe(before);
    }

    private async Task<int> ErasureEventCountAsync()
    {
        var count = 0;
        await fixture.ExecuteAuditDbAsync(async db =>
            count = await db.AuditEvents.AsNoTracking().CountAsync(candidate =>
                candidate.Action == "template.lifecycle_note.erased"
                || candidate.Action == "layout.lifecycle_note.erased"));
        return count;
    }

    private async Task<LifecycleNoteErasure> EraseAsync(Guid noteRef, string actor)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<LifecycleNoteEraser>()
            .EraseAsync(noteRef, actor, CancellationToken.None);
    }

    private async Task<Guid> ReferenceOfAsync(string key)
    {
        Guid noteRef = Guid.Empty;
        await fixture.ExecuteDbAsync(async db =>
            noteRef = (await db.LifecycleNotes.AsNoTracking()
                .SingleAsync(note => note.SubjectKey == key)).Id);
        return noteRef;
    }

    private static Task<AuditEvent> SingleAsync(AuditDbContext db, string action, string entityId)
        => db.AuditEvents.AsNoTracking().SingleAsync(candidate =>
            candidate.Action == action && candidate.EntityId == entityId);
}
