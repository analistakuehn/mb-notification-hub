using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

/// <summary>
/// The lifecycle as a producer and as operations reach it, over HTTP.
/// <para>
/// What these oracles prove: that asking for a verdict and taking a release
/// back are reachable from production wiring; that repeating either one
/// answers what the first call answered and writes nothing more; that every
/// transition the state refuses ends with a named public reason; that the whole
/// family of content refusals leaves under one word and the fine detail leaves
/// only through the authorized reading; and that no answer on any of these
/// routes carries a coordinate of the bytes.
/// </para>
/// <para>
/// What they do not prove: that the principal allowed to take content back is
/// the right one, and anything at all about hostile content. The reading of the
/// content is still the shipped policy, which decides on a byte prefix and a
/// list and never opens a file.
/// </para>
/// </summary>
[Collection(AttachmentManagementApiCollectionDefinition.Name)]
public sealed class AttachmentLifecycleEndpointTests(AttachmentManagementApiFixture fixture)
{
    private const string PdfContent = "%PDF-1.7 sample attachment body";
    private const string Reason = "produtor-substituiu-o-arquivo";
    private const string RefusedDetail = "content-type-not-admitted";

    private static readonly TimeSpan Validity = TimeSpan.FromDays(30);

    /// <summary>
    /// The refusal a producer meets today: nothing is admitted, so the file
    /// that agrees with its own declaration is refused just the same. The
    /// answer names one reason for the whole family, and the repeat is the
    /// same answer over a state nothing reopened.
    /// </summary>
    [RequiresDockerFact]
    public async Task Asking_for_a_verdict_that_refuses_says_one_word_and_repeating_it_says_it_again()
    {
        var principal = $"lifecycle-refused-{Guid.NewGuid():N}";
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(fixture.Services, principal);
        using HttpClient client = fixture.CreateProducerClient(principal);
        var reference = await UploadedReferenceAsync(client);

        (HttpStatusCode firstStatus, var firstBody) = await ValidateAsync(client, reference);
        (HttpStatusCode repeatedStatus, var repeatedBody) = await ValidateAsync(client, reference);

        firstStatus.ShouldBe(HttpStatusCode.Conflict);
        Title(firstBody).ShouldBe(ErrorCodes.ContentRefused);
        repeatedStatus.ShouldBe(firstStatus);

        // Everything the answer says, held against the first answer. The
        // correlator of the request is dropped and nothing else is: a repeat
        // that reported another status, another reason or another sentence
        // would be a repeat that decided something.
        ProblemShape(repeatedBody).ShouldBe(ProblemShape(firstBody));

        // The fine detail is durable state and never a public answer. A
        // producer that could read it here would read a map of what to work
        // around.
        firstBody.ShouldNotContain(RefusedDetail, Case.Insensitive);

        Attachment settled = await fixture.QueryAttachmentAsync(reference);
        settled.State.ShouldBe(AttachmentStates.Rejected);
        settled.ValidationDetail.ShouldBe(RefusedDetail);
        (await ReleasesAsync(settled.Id)).ShouldBeEmpty();
    }

    [RequiresDockerFact]
    public async Task Asking_for_a_verdict_before_the_content_arrives_says_what_is_missing()
    {
        var principal = $"lifecycle-missing-{Guid.NewGuid():N}";
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(fixture.Services, principal);
        using HttpClient client = fixture.CreateProducerClient(principal);
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, ByteCount(PdfContent));
        registration.Dispose();

        (HttpStatusCode status, var body) = await ValidateAsync(client, registered.Reference);

        status.ShouldBe(HttpStatusCode.Conflict);
        Title(body).ShouldBe(ErrorCodes.ContentMissing);
        (await fixture.QueryAttachmentAsync(registered.Reference)).State
            .ShouldBe(AttachmentStates.AwaitingUpload);
    }

    /// <summary>
    /// The release, and the repeat that must not move it. The instant is read
    /// on both sides: a second grant, or a first grant redated, would be a
    /// clock a producer could restart by sending the same request twice.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_verdict_that_releases_answers_the_released_state_and_repeating_it_moves_nothing()
    {
        using WebApplicationFactory<Program> host = AdmittingHost();
        var principal = $"lifecycle-released-{Guid.NewGuid():N}";
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(fixture.Services, principal);
        using HttpClient client = fixture.CreateProducerClient(host, principal);
        var reference = await UploadedReferenceAsync(client);

        (HttpStatusCode firstStatus, var firstBody) = await ValidateAsync(client, reference);
        Attachment released = await fixture.QueryAttachmentAsync(reference);
        AttachmentRelease granted = (await ReleasesAsync(released.Id)).ShouldHaveSingleItem();
        (HttpStatusCode repeatedStatus, var repeatedBody) = await ValidateAsync(client, reference);

        firstStatus.ShouldBe(HttpStatusCode.OK);
        State(firstBody).ShouldBe(AttachmentStates.Released);
        repeatedStatus.ShouldBe(HttpStatusCode.OK);
        repeatedBody.ShouldBe(firstBody);

        AttachmentRelease unchanged = (await ReleasesAsync(released.Id)).ShouldHaveSingleItem();
        unchanged.Id.ShouldBe(granted.Id);
        unchanged.ReleasedAt.ShouldBe(granted.ReleasedAt);
        unchanged.ExpiresAt.ShouldBe(granted.ExpiresAt);
    }

    /// <summary>
    /// The withdrawal, and the repeat that must record nothing more. The second
    /// call carries a different reason on purpose: if a repeat wrote, the
    /// record would say why the retry was sent instead of why the content
    /// stopped being deliverable.
    /// </summary>
    [RequiresDockerFact]
    public async Task Taking_a_release_back_answers_the_revoked_state_and_repeating_it_records_nothing()
    {
        using WebApplicationFactory<Program> host = AdmittingHost();
        using HttpClient client = await ProducerWithGrantAsync(host, "lifecycle-revoked");
        var reference = await ReleasedReferenceAsync(client);

        (HttpStatusCode firstStatus, var firstBody) = await RevokeAsync(client, reference, Reason);
        Attachment revoked = await fixture.QueryAttachmentAsync(reference);
        AttachmentRevocation withdrawal =
            (await RevocationsAsync(revoked.Id)).ShouldHaveSingleItem();
        (HttpStatusCode repeatedStatus, var repeatedBody) = await RevokeAsync(
            client,
            reference,
            "motivo-diferente-do-primeiro");

        firstStatus.ShouldBe(HttpStatusCode.OK);
        State(firstBody).ShouldBe(AttachmentStates.Revoked);
        repeatedStatus.ShouldBe(HttpStatusCode.OK);
        repeatedBody.ShouldBe(firstBody);

        AttachmentRevocation unchanged =
            (await RevocationsAsync(revoked.Id)).ShouldHaveSingleItem();
        unchanged.Id.ShouldBe(withdrawal.Id);
        unchanged.Reason.ShouldBe(Reason);
        unchanged.RevokedAt.ShouldBe(withdrawal.RevokedAt);
        (await fixture.QueryAttachmentAsync(reference)).State.ShouldBe(AttachmentStates.Revoked);
    }

    /// <summary>
    /// The rule the task turns on, read from outside. A verdict asked over a
    /// withdrawn release is refused by a name of its own, because a producer
    /// that was told the content was refused would be told something no check
    /// ever decided.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_verdict_asked_over_a_withdrawn_release_is_refused_by_its_own_name()
    {
        using WebApplicationFactory<Program> host = AdmittingHost();
        using HttpClient client = await ProducerWithGrantAsync(host, "lifecycle-reopen");
        var reference = await ReleasedReferenceAsync(client);
        Attachment released = await fixture.QueryAttachmentAsync(reference);
        (HttpStatusCode revocation, _) = await RevokeAsync(client, reference, Reason);
        revocation.ShouldBe(HttpStatusCode.OK);

        (HttpStatusCode status, var body) = await ValidateAsync(client, reference);

        status.ShouldBe(HttpStatusCode.Conflict);
        Title(body).ShouldBe(ErrorCodes.Revoked);
        Title(body).ShouldNotBe(ErrorCodes.ContentRefused);
        (await fixture.QueryAttachmentAsync(reference)).State.ShouldBe(AttachmentStates.Revoked);
        (await ReleasesAsync(released.Id)).Length.ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task Taking_back_something_nobody_released_is_refused_by_its_own_name()
    {
        using HttpClient client = await ProducerWithGrantAsync(fixture, "lifecycle-unreleased");
        var reference = await UploadedReferenceAsync(client);

        (HttpStatusCode status, var body) = await RevokeAsync(client, reference, Reason);

        status.ShouldBe(HttpStatusCode.Conflict);
        Title(body).ShouldBe(ErrorCodes.NotReleased);
        (await fixture.QueryAttachmentAsync(reference)).State.ShouldBe(AttachmentStates.Received);
    }

    [RequiresDockerTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("um motivo longo demais para o estado durável comportar sem cortar")]
    public async Task A_withdrawal_without_a_usable_reason_leaves_the_release_in_force(
        string reason)
    {
        using WebApplicationFactory<Program> host = AdmittingHost();
        using HttpClient client = await ProducerWithGrantAsync(host, "lifecycle-bad-reason");
        var reference = await ReleasedReferenceAsync(client);

        (HttpStatusCode status, var body) = await RevokeAsync(client, reference, reason);

        status.ShouldBe(HttpStatusCode.BadRequest);

        // Which of the two guards answered, named. The request never reaches
        // the act, so the answer names the member of the body: an answer that
        // came from the guard inside the act would carry this module's own
        // refusal code instead, and the caller would learn nothing about which
        // member it has to fix.
        JsonDocument.Parse(body).RootElement
            .GetProperty("errors")
            .EnumerateObject()
            .Select(member => member.Name)
            .ShouldContain("Reason");

        Attachment attachment = await fixture.QueryAttachmentAsync(reference);
        attachment.State.ShouldBe(AttachmentStates.Released);
        (await RevocationsAsync(attachment.Id)).ShouldBeEmpty();
    }

    /// <summary>
    /// The reading that tells the checks apart is a different job from
    /// producing. A producer holding the grant over the very attachment being
    /// read is still refused, which is the only arrangement that measures the
    /// separation: a caller with no grant at all would be refused by anything.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_operations_reading_is_closed_to_a_producer_and_to_a_caller_with_no_token()
    {
        using HttpClient producer = await ProducerWithGrantAsync(fixture, "lifecycle-ops-denied");
        var reference = await UploadedReferenceAsync(producer);
        using HttpClient anonymous = fixture.CreateClient();
        using HttpClient operations = fixture.CreateOperationsClient(
            fixture,
            $"lifecycle-operator-{Guid.NewGuid():N}",
            AuthorizationSetup.OperationsRole);

        using HttpResponseMessage byProducer = await producer.GetAsync(Lifecycle(reference));
        using HttpResponseMessage byAnonymous = await anonymous.GetAsync(Lifecycle(reference));
        using HttpResponseMessage byOperations = await operations.GetAsync(Lifecycle(reference));

        byProducer.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        byAnonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        byOperations.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The whole of the single public reason. The same refusal is read twice,
    /// once by the producer and once by operations, and only one of the two
    /// readings names the check. Without both halves the rule is a claim: a
    /// producer answer that hides the detail proves nothing if nobody can read
    /// it either.
    /// </summary>
    [RequiresDockerFact]
    public async Task Only_the_operations_reading_names_the_check_that_refused()
    {
        using HttpClient producer = await ProducerWithGrantAsync(fixture, "lifecycle-detail");
        var reference = await UploadedReferenceAsync(producer);
        (HttpStatusCode status, var refusal) = await ValidateAsync(producer, reference);
        status.ShouldBe(HttpStatusCode.Conflict);

        JsonElement lifecycle = await ReadOperationsAsync(fixture, reference);

        refusal.ShouldNotContain(RefusedDetail, Case.Insensitive);
        lifecycle.GetProperty("validationDetail").GetString().ShouldBe(RefusedDetail);
        lifecycle.GetProperty("state").GetString().ShouldBe(AttachmentStates.Rejected);
        lifecycle.GetProperty("releasedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        lifecycle.GetProperty("revokedAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// The deadline no column holds. Nothing writes an expiry into the state,
    /// so this reading is the only place the mechanism can be observed before a
    /// message is on its way out, and the withdrawal that ended the grant is
    /// read beside it.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_operations_reading_shows_the_deadline_and_the_withdrawal_that_ended_it()
    {
        using WebApplicationFactory<Program> host = AdmittingHost();
        using HttpClient client = await ProducerWithGrantAsync(host, "lifecycle-ops-release");
        var reference = await ReleasedReferenceAsync(client);
        Attachment released = await fixture.QueryAttachmentAsync(reference);
        AttachmentRelease granted = (await ReleasesAsync(released.Id)).ShouldHaveSingleItem();

        JsonElement beforeWithdrawal = await ReadOperationsAsync(host, reference);
        (HttpStatusCode revocation, _) = await RevokeAsync(client, reference, Reason);
        revocation.ShouldBe(HttpStatusCode.OK);
        JsonElement afterWithdrawal = await ReadOperationsAsync(host, reference);

        beforeWithdrawal.GetProperty("state").GetString().ShouldBe(AttachmentStates.Released);
        beforeWithdrawal.GetProperty("releasedAt").GetDateTimeOffset()
            .ShouldBe(granted.ReleasedAt);
        beforeWithdrawal.GetProperty("releaseExpiresAt").GetDateTimeOffset()
            .ShouldBe(granted.ReleasedAt + Validity);
        beforeWithdrawal.GetProperty("revokedAt").ValueKind.ShouldBe(JsonValueKind.Null);

        afterWithdrawal.GetProperty("state").GetString().ShouldBe(AttachmentStates.Revoked);
        afterWithdrawal.GetProperty("revocationReason").GetString().ShouldBe(Reason);
        afterWithdrawal.GetProperty("revokedAt").ValueKind.ShouldBe(JsonValueKind.String);

        // The grant is still the grant. A withdrawal that had revised the
        // release line would show up right here, as a deadline that moved.
        afterWithdrawal.GetProperty("releasedAt").GetDateTimeOffset()
            .ShouldBe(granted.ReleasedAt);
        afterWithdrawal.GetProperty("releaseExpiresAt").GetDateTimeOffset()
            .ShouldBe(granted.ReleasedAt + Validity);
    }

    /// <summary>
    /// Every answer of the three new routes, and everything they wrote to the
    /// log, held against what this module never publishes. The reference is the
    /// correlator and has to be there; the coordinate, the content identity,
    /// the name, the declared type and the proof of the bytes must not be.
    /// </summary>
    [RequiresDockerFact]
    public async Task No_answer_on_the_lifecycle_routes_carries_a_coordinate_of_the_bytes()
    {
        using WebApplicationFactory<Program> host = AdmittingHost();
        using HttpClient client = await ProducerWithGrantAsync(host, "lifecycle-leak");
        var reference = await UploadedReferenceAsync(client);
        Attachment uploaded = await fixture.QueryAttachmentAsync(reference);
        AttachmentObjectGeneration generation = await SingleGenerationAsync(uploaded.Id);
        fixture.Logs.Events.Clear();

        (_, var validation) = await ValidateAsync(client, reference);
        (_, var withdrawal) = await RevokeAsync(client, reference, Reason);
        JsonElement lifecycle = await ReadOperationsAsync(host, reference);

        string[] answers =
        [
            validation,
            withdrawal,
            lifecycle.GetRawText(),
            .. fixture.Logs.Events.SelectMany(AttachmentApi.LogFragments),
        ];
        answers.ShouldContain(
            fragment => fragment.Contains(reference, StringComparison.Ordinal),
            "alguma resposta ou linha de registro tem de nomear o anexo.");

        string[] prohibited =
        [
            AttachmentManagementApiFixture.Bucket,
            AttachmentManagementApiFixture.AccessKey,
            AttachmentManagementApiFixture.SecretKey,
            fixture.AwsEndpoint,
            generation.Store,
            generation.Key,
            generation.Version,
            uploaded.ContentId.ToString("N"),
            PdfContent,
            AttachmentApi.FileName,
            AttachmentApi.ContentType,
            Convert.ToHexString(generation.Digest),
            Convert.ToHexString(generation.Digest).ToLowerInvariant(),
            Convert.ToBase64String(generation.Digest),
        ];
        foreach (var value in prohibited)
        {
            answers.ShouldAllBe(fragment =>
                !fragment.Contains(value, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The two transitions answer for the grant over the exact reference, not
    /// for the fact that the caller holds some grant somewhere. The principal
    /// here holds a real grant over another application, which is the only
    /// arrangement that measures the boundary: a caller with no grant at all
    /// would be refused by the bare requirement before the resource is read.
    /// <para>
    /// A miss and a denial are the same answer on purpose. Telling them apart
    /// would let a caller enumerate references it may not touch.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task A_grant_over_another_application_reaches_neither_transition()
    {
        using WebApplicationFactory<Program> host = AdmittingHost();
        using HttpClient owner = await ProducerWithGrantAsync(host, "lifecycle-owner");
        var reference = await ReleasedReferenceAsync(owner);

        var stranger = $"lifecycle-stranger-{Guid.NewGuid():N}";
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            stranger,
            application: "outra-aplicacao");
        using HttpClient other = fixture.CreateProducerClient(host, stranger);

        (HttpStatusCode validation, var validationBody) = await ValidateAsync(other, reference);
        (HttpStatusCode revocation, var revocationBody) = await RevokeAsync(
            other,
            reference,
            Reason);

        validation.ShouldBe(HttpStatusCode.NotFound);
        Title(validationBody).ShouldBe(ErrorCodes.NotFound);
        revocation.ShouldBe(HttpStatusCode.NotFound);
        Title(revocationBody).ShouldBe(ErrorCodes.NotFound);

        Attachment untouched = await fixture.QueryAttachmentAsync(reference);
        untouched.State.ShouldBe(AttachmentStates.Released);
        (await RevocationsAsync(untouched.Id)).ShouldBeEmpty();
    }

    /// <summary>
    /// A host that admits one type, so the shipped policy has something to
    /// approve. Everything else is the module's own wiring: the policy, the
    /// machine and the act all come out of the composed host.
    /// </summary>
    private WebApplicationFactory<Program> AdmittingHost()
        => fixture.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{AttachmentValidationOptions.SectionName}:AdmittedContentTypes:0"] =
                        AttachmentApi.ContentType,
                })));

    private static string Lifecycle(string reference)
        => $"/v1/attachment-operations/{reference}";

    private async Task<HttpClient> ProducerWithGrantAsync(
        WebApplicationFactory<Program> host,
        string prefix)
    {
        var principal = $"{prefix}-{Guid.NewGuid():N}";
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(fixture.Services, principal);
        return fixture.CreateProducerClient(host, principal);
    }

    private async Task<JsonElement> ReadOperationsAsync(
        WebApplicationFactory<Program> host,
        string reference)
    {
        using HttpClient operations = fixture.CreateOperationsClient(
            host,
            $"lifecycle-operator-{Guid.NewGuid():N}",
            AuthorizationSetup.OperationsRole);
        using HttpResponseMessage response = await operations.GetAsync(Lifecycle(reference));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<(HttpStatusCode Status, string Body)> ValidateAsync(
        HttpClient client,
        string reference)
    {
        using HttpResponseMessage response = await client.PostAsync(
            $"/v1/attachments/{reference}/validation",
            content: null);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<(HttpStatusCode Status, string Body)> RevokeAsync(
        HttpClient client,
        string reference,
        string reason)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/attachments/{reference}/revocation",
            new { reason });
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> UploadedReferenceAsync(HttpClient client)
    {
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, ByteCount(PdfContent));
        registration.Dispose();
        using HttpResponseMessage upload = await AttachmentApi.PutContentAsync(
            client,
            registered.Reference,
            PdfContent);
        upload.StatusCode.ShouldBe(HttpStatusCode.OK);
        return registered.Reference;
    }

    private static async Task<string> ReleasedReferenceAsync(HttpClient client)
    {
        var reference = await UploadedReferenceAsync(client);
        (HttpStatusCode status, var body) = await ValidateAsync(client, reference);
        status.ShouldBe(HttpStatusCode.OK);
        State(body).ShouldBe(AttachmentStates.Released);
        return reference;
    }

    /// <summary>
    /// A problem answer without the correlator of the request. Every other
    /// member stays, so two answers that differ in status, reason or sentence
    /// still differ here.
    /// </summary>
    private static string ProblemShape(string body)
    {
        JsonObject problem = JsonNode.Parse(body).ShouldNotBeNull().AsObject();
        problem.Remove("traceId");
        return problem.ToJsonString();
    }

    private static string? Title(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("title").GetString();

    private static string? State(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("state").GetString();

    private static long ByteCount(string content) => Encoding.UTF8.GetByteCount(content);

    private static AttachmentManagementDbContext Context(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<AttachmentManagementDbContext>();

    private async Task<AttachmentRelease[]> ReleasesAsync(Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await Context(scope)
            .Releases
            .AsNoTracking()
            .Where(release => release.AttachmentId == attachmentId)
            .ToArrayAsync();
    }

    private async Task<AttachmentRevocation[]> RevocationsAsync(Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await Context(scope)
            .Revocations
            .AsNoTracking()
            .Where(revocation => revocation.AttachmentId == attachmentId)
            .ToArrayAsync();
    }

    private async Task<AttachmentObjectGeneration> SingleGenerationAsync(Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await Context(scope)
            .ObjectGenerations
            .AsNoTracking()
            .SingleAsync(generation => generation.AttachmentId == attachmentId);
    }
}
