namespace NotificationHub.IntegrationTests;

public sealed class HealthCheckSmokeTests(TestApplicationFactory factory)
    : IClassFixture<TestApplicationFactory>
{
    [Fact]
    public async Task Health_endpoint_returns_success()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.IsSuccessStatusCode.ShouldBeTrue();
    }
}
