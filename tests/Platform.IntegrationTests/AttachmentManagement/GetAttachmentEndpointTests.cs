using System.Net;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

[Collection(AttachmentManagementApiCollectionDefinition.Name)]
public sealed class GetAttachmentEndpointTests(AttachmentManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Query_returns_only_the_public_reference_and_state()
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            "attachment-reader");
        using HttpClient client = fixture.CreateProducerClient("attachment-reader");
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, sizeBytes: 4);
        using (registration)
        {
            using HttpResponseMessage response = await client.GetAsync(
                $"/v1/attachments/{registered.Reference}");

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            AttachmentApi.ApiResponse body = await AttachmentApi.ReadMinimalResponseAsync(response);
            body.Reference.ShouldBe(registered.Reference);
            body.State.ShouldBe(AttachmentStates.AwaitingUpload);
        }
    }

    [RequiresDockerFact]
    public async Task Invalid_and_unknown_references_share_the_same_non_enumerating_404()
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            "attachment-reader");
        using HttpClient client = fixture.CreateProducerClient("attachment-reader");
        var unknown = AttachmentReference.Generate().Value;

        using HttpResponseMessage malformedResponse = await client.GetAsync(
            "/v1/attachments/not-a-reference");
        using HttpResponseMessage unknownResponse = await client.GetAsync(
            $"/v1/attachments/{unknown}");
        malformedResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unknownResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await AttachmentAuthorizationAssertions.ShouldBeIntegralNotFoundAsync(
            malformedResponse,
            unknownResponse);
    }

    [RequiresDockerFact]
    public async Task Query_without_a_bearer_token_returns_401()
    {
        using HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            $"/v1/attachments/{AttachmentReference.Generate().Value}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
