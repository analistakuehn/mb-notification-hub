using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.TemplateManagement;

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
    [InlineData("billing-app", "invoice.pdf", "application/pdf", 30_000_001)]
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
