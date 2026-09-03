using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capacity;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

[Collection(AttachmentManagementApiCollectionDefinition.Name)]
public sealed class RegisterAttachmentEndpointTests(AttachmentManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Registration_returns_201_location_and_only_the_public_state()
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            "attachment-producer");
        fixture.Logs.Events.Clear();
        using HttpClient client = fixture.CreateProducerClient("attachment-producer");

        (HttpResponseMessage response, AttachmentApi.ApiResponse body) =
            await AttachmentApi.RegisterAsync(client, sizeBytes: 128);
        using (response)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            response.Headers.Location!.ToString()
                .ShouldBe($"/v1/attachments/{body.Reference}");
            body.State.ShouldBe(AttachmentStates.AwaitingUpload);
            AttachmentReference.Create(body.Reference).IsSuccess.ShouldBeTrue();

            Attachment persisted = await fixture.QueryAttachmentAsync(body.Reference);
            persisted.Application.ShouldBe(AttachmentApi.Application);
            persisted.FileName.ShouldBe(AttachmentApi.FileName);
            persisted.ContentType.ShouldBe(AttachmentApi.ContentType);
            persisted.SizeBytes.ShouldBe(128);
            persisted.ContentId.ShouldNotBe(Guid.Empty);

            AssertDoesNotLeak(
                AttachmentApi.ResponseFragments(response, body.Body),
                persisted.ContentId.ToString("N"));
        }
    }

    [RequiresDockerFact]
    public async Task Registration_without_a_bearer_token_returns_401()
    {
        using HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/attachments",
            AttachmentApi.Registration(sizeBytes: 4));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [RequiresDockerFact]
    public async Task Registration_without_a_matching_grant_returns_403_and_writes_nothing()
    {
        var saveOperation = new CountingAttachmentSaveOperation();
        using WebApplicationFactory<Program> host = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAttachmentSaveOperation>();
                services.AddSingleton<IAttachmentSaveOperation>(saveOperation);
            }));
        using HttpClient client = fixture.CreateProducerClient(host, "unregistered-producer");
        var before = await CountAttachmentsAsync();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/attachments",
            AttachmentApi.Registration(sizeBytes: 4));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        saveOperation.CallCount.ShouldBe(0);
        (await CountAttachmentsAsync()).ShouldBe(before);
    }

    [RequiresDockerFact]
    public async Task Authorization_precedes_non_application_validation_for_an_unregistered_principal()
    {
        var saveOperation = new CountingAttachmentSaveOperation();
        using WebApplicationFactory<Program> host = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAttachmentSaveOperation>();
                services.AddSingleton<IAttachmentSaveOperation>(saveOperation);
            }));
        using HttpClient client = fixture.CreateProducerClient(
            host,
            "invalid-unregistered-producer");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/attachments",
            AttachmentApi.Registration(sizeBytes: 4, fileName: string.Empty));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        saveOperation.CallCount.ShouldBe(0);
    }

    [RequiresDockerTheory]
    [InlineData("billing-app", "", "application/pdf", 4)]
    [InlineData("billing-app", "invoice.pdf", "not a media type", 4)]
    [InlineData("billing-app", "invoice.pdf", "application/pdf", 0)]
    public async Task Structurally_invalid_metadata_returns_400(
        string application,
        string fileName,
        string contentType,
        long sizeBytes)
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            "attachment-producer");
        using HttpClient client = fixture.CreateProducerClient("attachment-producer");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/attachments",
            AttachmentApi.Registration(sizeBytes, application, fileName, contentType));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The endpoint answers about the capacity the module was configured with.
    /// The ceiling is taken from the running host rather than restated here, so
    /// this stays a reading about the wiring and not about a number it does not
    /// measure, and the pair is what makes it one: refusing the ceiling itself
    /// and admitting the byte past it both look like a working rule from one
    /// side only.
    /// <para>
    /// The refusal has to name the field, because that is what says the request
    /// validator turned it away rather than the use case behind it. Both answer
    /// 400, and without the field the two are the same green.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task Registration_accepts_the_configured_ceiling_and_refuses_the_byte_above_it()
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            "attachment-producer");
        using HttpClient client = fixture.CreateProducerClient("attachment-producer");
        var ceiling = ConfiguredCeiling();

        (HttpResponseMessage accepted, AttachmentApi.ApiResponse body) =
            await AttachmentApi.RegisterAsync(client, ceiling);
        using (accepted)
        {
            accepted.StatusCode.ShouldBe(HttpStatusCode.Created);
            (await fixture.QueryAttachmentAsync(body.Reference)).SizeBytes.ShouldBe(ceiling);
        }

        using HttpResponseMessage refused = await client.PostAsJsonAsync(
            "/v1/attachments",
            AttachmentApi.Registration(ceiling + 1));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await refused.Content.ReadAsStringAsync();
        problem.Contains(nameof(RegisterAttachment.Command.SizeBytes), StringComparison.Ordinal)
            .ShouldBeTrue("A recusa do tamanho tem de vir do validador, que nomeia o campo.");
    }

    /// <summary>
    /// The use case refuses the same size with the request validator out of the
    /// way. Through the endpoint the two guards are indistinguishable, so a use
    /// case that had kept a ceiling of its own would sit behind a green suite
    /// until the day the validator was the one to drift, which is the day the
    /// second guard is the only thing left.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_registration_use_case_refuses_the_byte_above_the_configured_ceiling()
    {
        var ceiling = ConfiguredCeiling();
        var before = await CountAttachmentsAsync();
        using IServiceScope scope = fixture.Services.CreateScope();

        Result<RegisterAttachment.Response> result = await scope.ServiceProvider
            .GetRequiredService<RegisterAttachment.Handler>()
            .HandleAsync(
                new RegisterAttachment.Command(
                    AttachmentApi.Application,
                    AttachmentApi.FileName,
                    AttachmentApi.ContentType,
                    ceiling + 1),
                CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ErrorCodes.InvalidMetadata);
        (await CountAttachmentsAsync()).ShouldBe(before);
    }

    /// <summary>The capacity the running host was configured with.</summary>
    private long ConfiguredCeiling()
        => fixture.Services
            .GetRequiredService<IOptions<AttachmentCapacityOptions>>()
            .Value
            .MaxAttachmentBytes;

    private void AssertDoesNotLeak(IEnumerable<string> responseFragments, string contentId)
    {
        string[] prohibited =
        [
            AttachmentApi.FileName,
            AttachmentApi.ContentType,
            contentId,
            AttachmentManagementApiFixture.Bucket,
            AttachmentManagementApiFixture.AccessKey,
            AttachmentManagementApiFixture.SecretKey,
            fixture.AwsEndpoint,
        ];
        string[] logs = [.. fixture.Logs.Events.SelectMany(AttachmentApi.LogFragments)];
        foreach (var value in prohibited)
        {
            responseFragments.ShouldAllBe(fragment =>
                !fragment.Contains(value, StringComparison.Ordinal));
            logs.ShouldAllBe(fragment =>
                !fragment.Contains(value, StringComparison.Ordinal));
        }
    }

    private async Task<int> CountAttachmentsAsync()
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .Attachments
            .AsNoTracking()
            .CountAsync();
    }
}
