namespace NotificationHub.IntegrationTests;

public sealed class HealthCheckSmokeTests(TestApplicationFactory factory)
    : IClassFixture<TestApplicationFactory>
{
    [Fact]
    public async Task Health_endpoint_returns_success()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health");

        response.IsSuccessStatusCode.ShouldBeTrue();
    }
}
