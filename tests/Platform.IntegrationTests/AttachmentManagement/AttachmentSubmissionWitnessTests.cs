using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

/// <summary>
/// The witness that relates the bytes one attempt submitted to the bytes that
/// were released, settled against the record this module actually writes.
/// <para>
/// The two sides of every comparison here are produced by two different
/// passes. The released side is the digest the module measured while capturing
/// the upload, read back off the generation row; the submitted side is
/// measured at send time over the bytes that were written out. That is what
/// every arm below turns on: the divergent cases carry content of exactly the
/// released length, só nothing but the digest can refuse them, and a witness
/// that recomputed the released side from the record would answer that they
/// agree.
/// </para>
/// </summary>
[Collection(AttachmentManagementApiCollectionDefinition.Name)]
public sealed class AttachmentSubmissionWitnessTests(AttachmentManagementApiFixture fixture)
{
    private const string Content = "conteudo-submetido-ao-provedor-sob-a-testemunha-de-bytes";

    private const string SenderAddress = "no-reply@example.com";

    private static readonly EmailDeliveryTarget Target = new("person@example.com");

    private static readonly EmailMessage Message = new(
        "Confirme sua operação", "Aguardando confirmação", "<p>Ola</p>", "Ola");

    /// <summary>
    /// A submission carrying the released bytes settles as matched, and one
    /// carrying other bytes of the very same length settles as divergent.
    /// <para>
    /// The two arms share one arrangement and one recorded generation, só the
    /// only thing that changes between them is the content the submission was
    /// measured over. Equal lengths are the point: a witness that compared
    /// sizes, and a witness that compared nothing at all, both answer matched
    /// on the second arm.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task A_submission_of_other_bytes_of_the_released_length_settles_as_divergent()
    {
        AttachmentObjectGeneration generation = await UploadedAsync("witness-divergence-producer");
        var handle = AttachmentContentIdentity.For(generation);
        var released = Encoding.UTF8.GetBytes(Content);
        var other = Tampered(released);
        IAttachmentSubmissionWitness witness = Witness();

        (await witness.SettleAsync(
            [Measured(handle, released)], CancellationToken.None))
            .ShouldBe(AttachmentSubmissionVerdict.Matched);

        other.LongLength.ShouldBe(released.LongLength);
        (await witness.SettleAsync(
            [Measured(handle, other)], CancellationToken.None))
            .ShouldBe(AttachmentSubmissionVerdict.Divergent);
    }

    /// <summary>
    /// A submission of the released bytes under another length settles as
    /// divergent too. The length is the other half of what the release was
    /// granted over, and a comparison that only weighed the digest would let a
    /// member through that claims to have sent more or less than it did.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_submission_that_states_another_length_settles_as_divergent()
    {
        AttachmentObjectGeneration generation = await UploadedAsync("witness-length-producer");
        var handle = AttachmentContentIdentity.For(generation);
        var released = Encoding.UTF8.GetBytes(Content);
        IAttachmentSubmissionWitness witness = Witness();

        (await witness.SettleAsync(
            [Measured(handle, released)], CancellationToken.None))
            .ShouldBe(AttachmentSubmissionVerdict.Matched);

        var overstated = new SubmittedAttachmentBytes
        {
            ContentIdentity = handle,
            Length = released.LongLength + 1,
            Digest = SHA256.HashData(released),
        };
        (await witness.SettleAsync([overstated], CancellationToken.None))
            .ShouldBe(AttachmentSubmissionVerdict.Divergent);
    }

    /// <summary>
    /// A handle this module never minted, and a handle whose generation is no
    /// longer recorded, settle nothing at all. Neither is a statement that the
    /// bytes diverged: a comparison with one side missing has not shown
    /// anything to be wrong, and reporting it as divergence would accuse a send
    /// that may have been perfect.
    /// <para>
    /// A recorded generation is arranged first and settled first, which is what
    /// makes the two refusals mean anything. Against an empty record every
    /// handle refuses, só a witness that ignored the handle altogether would
    /// pass the refusals and fail only here.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task A_handle_that_names_no_recorded_generation_settles_nothing()
    {
        AttachmentObjectGeneration generation = await UploadedAsync("witness-refusals-producer");
        var released = Encoding.UTF8.GetBytes(Content);
        IAttachmentSubmissionWitness witness = Witness();

        (await witness.SettleAsync(
            [Measured(AttachmentContentIdentity.For(generation), released)],
            CancellationToken.None))
            .ShouldBe(AttachmentSubmissionVerdict.Matched);

        foreach (var handle in new[]
        {
            "not-a-handle",
            "att_" + Guid.NewGuid().ToString("N"),
            AttachmentContentIdentity.For(Guid.NewGuid()),
        })
        {
            (await witness.SettleAsync([Measured(handle, released)], CancellationToken.None))
                .ShouldBe(AttachmentSubmissionVerdict.Unavailable);
        }
    }

    /// <summary>
    /// A submission the contract cannot hold is refused rather than settled. A
    /// submission of nothing would report that every member of an empty set
    /// agreed, which is the one answer this must never be able to give.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_submission_the_contract_cannot_hold_is_refused()
    {
        AttachmentObjectGeneration generation = await UploadedAsync("witness-shape-producer");
        var handle = AttachmentContentIdentity.For(generation);
        var released = Encoding.UTF8.GetBytes(Content);
        IAttachmentSubmissionWitness witness = Witness();
        SubmittedAttachmentBytes member = Measured(handle, released);

        await Should.ThrowAsync<ArgumentException>(
            async () => await witness.SettleAsync([], CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(
            async () => await witness.SettleAsync(
                [member with { ContentIdentity = " " }], CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(
            async () => await witness.SettleAsync(
                [member with { Length = -1 }], CancellationToken.None));

        // The neighbour of the refusals, in the same arrangement, só that a
        // witness which simply threw at everything could not pass them.
        (await witness.SettleAsync([member], CancellationToken.None))
            .ShouldBe(AttachmentSubmissionVerdict.Matched);
    }

    /// <summary>
    /// The whole path, end to end: the adapter composes and writes the body,
    /// measures the bytes as they go, and the module that owns the proof of
    /// those bytes settles what it measured against the generation it recorded.
    /// <para>
    /// This is the arm that says the witness is not the record compared with
    /// itself. The custody hands back content of exactly the released length,
    /// and in the second arm it is not the released content: everything the
    /// adapter was told about the set stays identical, the message composes,
    /// the provider accepts, and the verdict flips. Nothing derived from the
    /// accepted set or from the generation row can produce that difference,
    /// because only the bytes differ.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task A_send_that_writes_other_bytes_than_the_released_ones_settles_as_divergent()
    {
        AttachmentObjectGeneration generation = await UploadedAsync("witness-end-to-end-producer");
        var handle = AttachmentContentIdentity.For(generation);
        var released = Encoding.UTF8.GetBytes(Content);
        var other = Tampered(released);

        SettledSend honest = await SendAsync(handle, released);
        honest.Verdict.ShouldBe(AttachmentSubmissionVerdict.Matched);

        // The owning module records the settlement of the attempt that agreed,
        // and records it as an agreement: a line that only ever appeared on
        // divergence would leave the evidence of a delivery that kept its
        // promise nowhere at all.
        Recorded(honest.Owner, "MemberCount").ShouldBe(["1"]);
        Recorded(honest.Owner, "DivergentCount").ShouldBeEmpty();
        Recorded(honest.Owner, "GenerationId").ShouldBeEmpty();

        SettledSend tampered = await SendAsync(handle, other);

        tampered.Verdict.ShouldBe(AttachmentSubmissionVerdict.Divergent);

        // The owning module names the generation of the member that did not
        // hold, which is what an investigation starts from and the only
        // identifier either line carries.
        Recorded(tampered.Owner, "DivergentCount").ShouldBe(["1"]);
        Recorded(tampered.Owner, "GenerationId").ShouldBe([generation.Id.ToString()]);
        tampered.Adapter.ShouldContain(
            fragment => fragment.Contains(
                nameof(AttachmentSubmissionVerdict.Divergent), StringComparison.Ordinal),
            "a linha do adaptador precisa nomear o veredito da tentativa.");

        string[] prohibited =
        [
            AttachmentManagementApiFixture.Bucket,
            generation.Store,
            generation.Key,
            generation.Version,
            handle,
            Content,
            AttachmentApi.FileName,
            AttachmentApi.ContentType,
            Convert.ToHexString(generation.Digest),
            Convert.ToHexString(generation.Digest).ToLowerInvariant(),
            Convert.ToBase64String(generation.Digest),
        ];
        string[] surfaces =
        [
            .. tampered.Adapter,
            .. tampered.Owner.SelectMany(AttachmentApi.LogFragments),
            .. honest.Adapter,
            .. honest.Owner.SelectMany(AttachmentApi.LogFragments),
        ];
        foreach (var value in prohibited)
        {
            surfaces.ShouldAllBe(fragment =>
                !fragment.Contains(value, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// One send through the real adapter, with the real witness of the module
    /// behind it and a custody that hands back exactly what this test planted.
    /// <para>
    /// The line of the owning module is captured on the fixture host, because
    /// that is where the witness this resolves was composed, and it is cleared
    /// first so that what comes back belongs to this send alone.
    /// </para>
    /// </summary>
    private async Task<SettledSend> SendAsync(string handle, byte[] content)
    {
        fixture.Logs.Events.Clear();
        AttachmentCustodyDouble custody = new AttachmentCustodyDouble().Plant(handle, content);
        var adapterLogs = new SentinelLogCaptureProvider();
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = _ => Task.FromResult(new FakeProviderResponse(202, null, null));
        await using ServiceProvider services = DispatchTestServices.BuildProviderHost(
            new Dictionary<string, string?>
            {
                ["Modules:Dispatch:Providers:SendGrid:BaseAddress"] = server.BaseAddress.ToString(),
                ["Modules:Dispatch:Providers:SendGrid:ApiKey"] = "sg-test-key",
                ["Modules:Dispatch:Providers:SendGrid:SenderEmail"] = SenderAddress,
                ["Modules:Dispatch:Providers:SendGrid:TimeoutSeconds"] = "30",
            },
            consumed =>
            {
                consumed.AddSingleton<IAcceptedAttachmentContent>(custody);
                consumed.AddSingleton(Witness());
                consumed.AddLogging(logging => logging.AddProvider(adapterLogs));
            });

        var accepted = AcceptedAttachmentSet.Of(
        [
            new AcceptedAttachment
            {
                Reference = "att_" + Guid.NewGuid().ToString("N"),
                ContentIdentity = handle,
                Name = AttachmentApi.FileName,
                MediaType = AttachmentApi.ContentType,
                Length = content.LongLength,
            },
        ]);

        ProviderResult result = await DispatchTestServices
            .ResolveProviderByKey(services, "sendgrid")
            .SendAsync(
                new DispatchRequest(
                    Target,
                    Message,
                    new DispatchCorrelation(Guid.NewGuid(), Guid.NewGuid()),
                    Attachments: accepted),
                CancellationToken.None);

        result.Outcome.ShouldBe(ProviderOutcome.Accepted);
        server.RequestCount.ShouldBe(1);
        custody.Opened.ShouldBe([handle]);

        string[] adapter = [.. adapterLogs.Events.SelectMany(AttachmentApi.LogFragments)];
        SentinelCapturedLogEvent[] owner = [.. fixture.Logs.Events];
        return new SettledSend(VerdictOf(adapter), adapter, owner);
    }

    /// <summary>
    /// Every value the owning module recorded under one name of its structured
    /// line. It reads the names and not the prose, so a message reworded stays
    /// green and a value that stopped being recorded does not.
    /// </summary>
    private static string[] Recorded(
        IEnumerable<SentinelCapturedLogEvent> events,
        string name)
        => [.. events
            .SelectMany(log => log.State)
            .Where(value => string.Equals(value.Key, name, StringComparison.Ordinal))
            .Select(value => value.Value)];

    /// <summary>One send and the two lines it left behind.</summary>
    private sealed record SettledSend(
        AttachmentSubmissionVerdict Verdict,
        string[] Adapter,
        SentinelCapturedLogEvent[] Owner);

    /// <summary>
    /// The verdict the adapter recorded for the attempt, read off the
    /// structured state of its own line rather than off the prose of it.
    /// </summary>
    private static AttachmentSubmissionVerdict VerdictOf(IEnumerable<string> adapterFragments)
        => Enum.GetValues<AttachmentSubmissionVerdict>()
            .Single(verdict => adapterFragments.Any(fragment =>
                string.Equals(fragment, verdict.ToString(), StringComparison.Ordinal)));

    private IAttachmentSubmissionWitness Witness()
        => fixture.Services.GetRequiredService<IAttachmentSubmissionWitness>();

    private static SubmittedAttachmentBytes Measured(string handle, byte[] content)
        => new()
        {
            ContentIdentity = handle,
            Length = content.LongLength,
            Digest = SHA256.HashData(content),
        };

    /// <summary>
    /// The same bytes with one of them changed, which keeps the length and
    /// changes the digest. It is the only mutation that leaves the length out
    /// of the comparison entirely.
    /// </summary>
    private static byte[] Tampered(byte[] content)
    {
        byte[] other = [.. content];
        other[^1] ^= 0xFF;
        return other;
    }

    /// <summary>
    /// One attachment through the module's own endpoints, só the object and
    /// the generation row are the ones production writes rather than rows a
    /// test composed.
    /// </summary>
    private async Task<AttachmentObjectGeneration> UploadedAsync(string producer)
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(fixture.Services, producer);
        using HttpClient client = fixture.CreateProducerClient(producer);
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, Content.Length);
        using (registration)
        {
            using HttpResponseMessage upload = await AttachmentApi.PutContentAsync(
                client, registered.Reference, Content);
            upload.EnsureSuccessStatusCode();
        }

        Attachment attachment = await fixture.QueryAttachmentAsync(registered.Reference);
        using IServiceScope scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .ObjectGenerations
            .AsNoTracking()
            .SingleAsync(row => row.AttachmentId == attachment.Id);
    }
}
