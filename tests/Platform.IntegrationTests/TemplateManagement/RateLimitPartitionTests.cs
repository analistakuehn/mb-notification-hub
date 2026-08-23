using System.Net;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>
/// The rate-limit budget is partitioned per authenticated principal. The probe
/// request fails its input validation inside the handler before any database
/// access, so exhausting a window stays cheap; every probe still consumes one
/// permit because the limiter runs before the endpoint.
/// </summary>
[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class RateLimitPartitionTests(TemplateManagementApiFixture fixture)
{
    private const int PermitLimit = 1000;
    private const string ProbeUrl = "/v1/templates?limit=0";

    [RequiresDockerFact]
    public async Task One_client_exhausting_its_window_does_not_throttle_another_client()
    {
        HttpClient greedy = fixture.CreateAuthorClient("rate-limit-greedy");
        HttpClient bystander = fixture.CreateAuthorClient("rate-limit-bystander");

        for (var i = 0; i < PermitLimit; i++)
        {
            HttpResponseMessage probe = await greedy.GetAsync(ProbeUrl);
            probe.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        HttpResponseMessage throttled = await greedy.GetAsync(ProbeUrl);
        throttled.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        HttpResponseMessage bystanderResponse = await bystander.GetAsync(ProbeUrl);
        bystanderResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // An anonymous request never lands in an authenticated partition: it
        // is challenged for credentials instead of rejected by the exhausted
        // window of the greedy client.
        HttpClient anonymous = fixture.CreateClient();
        HttpResponseMessage anonymousResponse = await anonymous.GetAsync(ProbeUrl);
        anonymousResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
