using System.Net;
using System.Text;
using NotificationHub.Platform.GoLiveChecks;

namespace NotificationHub.UnitTests.GoLive;

public sealed class GoLiveConfigurationTests
{
    [Theory]
    [InlineData(GoLiveEnvironmentVariables.GraphTenantId, null)]
    [InlineData(GoLiveEnvironmentVariables.GraphTenantId, "not-a-guid")]
    [InlineData(GoLiveEnvironmentVariables.GraphTenantId, "00000000-0000-0000-0000-000000000000")]
    [InlineData(GoLiveEnvironmentVariables.GraphApplicationId, null)]
    [InlineData(GoLiveEnvironmentVariables.GraphApplicationId, "not-a-guid")]
    [InlineData(GoLiveEnvironmentVariables.GraphApplicationId, "00000000-0000-0000-0000-000000000000")]
    [InlineData(GoLiveEnvironmentVariables.GraphServicePrincipalId, null)]
    [InlineData(GoLiveEnvironmentVariables.GraphServicePrincipalId, "not-a-guid")]
    [InlineData(GoLiveEnvironmentVariables.GraphServicePrincipalId, "00000000-0000-0000-0000-000000000000")]
    public void Missing_or_invalid_expected_graph_identity_fails_configuration(
        string variable,
        string? invalidValue)
    {
        Dictionary<string, string?> environment = CompleteEnvironment(
            "receipt.json",
            "Host=localhost;Database=templates",
            "graph-token");
        environment[variable] = invalidValue;

        GateConfiguration? configuration = GateConfiguration.Load(
            name => environment.GetValueOrDefault(name));

        configuration.ShouldBeNull();
    }

    [Fact]
    public async Task Missing_source_configuration_persists_an_error_receipt_and_does_not_execute_sources()
    {
        var receiptWriter = new RecordingReceiptWriter();
        var gateFactoryCalled = false;
        var environment = new Dictionary<string, string?>
        {
            [GoLiveEnvironmentVariables.ReceiptPath] = "receipt.json",
        };

        var exitCode = await GoLiveCheckApplication.RunAsync(
            name => environment.GetValueOrDefault(name),
            (_, _) =>
            {
                gateFactoryCalled = true;
                throw new InvalidOperationException("Sources must not be composed.");
            },
            receiptWriter,
            TimeProvider.System,
            CancellationToken.None);

        exitCode.ShouldBe(GoLiveExitCodes.Error);
        gateFactoryCalled.ShouldBeFalse();
        receiptWriter.Path.ShouldBe("receipt.json");
        receiptWriter.Receipt!.Status.ShouldBe(GoLiveStatuses.Error);
        receiptWriter.Receipt.Reasons.ShouldBe([GoLiveReasons.ConfigurationUnavailable]);
    }

    [Fact]
    public async Task Secrets_from_configuration_never_reach_the_receipt()
    {
        const string connectionString = "Host=db.example;Password=do-not-write";
        const string token = "graph-token-do-not-write";
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var receiptPath = Path.Combine(directory, "receipt.json");
        Dictionary<string, string?> environment = CompleteEnvironment(receiptPath, connectionString, token);

        try
        {
            var exitCode = await GoLiveCheckApplication.RunAsync(
                name => environment.GetValueOrDefault(name),
                (_, timeProvider) => new GoLiveGate(
                    new StubSource(GoLiveSourceIdentifiers.TemplateManagement, 1),
                    new StubSource(
                        GoLiveSourceIdentifiers.MicrosoftGraph,
                        0,
                        VerifiedGraphIdentity()),
                    timeProvider),
                new FileReceiptWriter(),
                new FixedTimeProvider(new DateTimeOffset(2026, 8, 24, 15, 30, 0, TimeSpan.Zero)),
                CancellationToken.None);

            var json = await File.ReadAllTextAsync(receiptPath, CancellationToken.None);

            exitCode.ShouldBe(GoLiveExitCodes.Violation);
            json.ShouldNotContain(connectionString);
            json.ShouldNotContain(token);
            json.ShouldNotContain("do-not-write");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Verified_graph_identity_reaches_the_receipt_without_the_access_token()
    {
        const string token = "graph-token-do-not-write";
        const string tenantId = "99cc1efd-3f10-43a9-bc5d-00e47fe0f347";
        const string applicationId = "fdc8b7f4-0956-478e-94d4-608d3f0ec244";
        const string servicePrincipalId = "738e728b-27e7-4ec9-a5d5-566acaf2022e";
        const string roleId = "8dde5cb3-9a06-4e25-bf46-e70b1b5613f3";
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var receiptPath = Path.Combine(directory, "receipt.json");
        Dictionary<string, string?> environment = CompleteEnvironment(
            receiptPath,
            "Host=localhost;Database=templates",
            token);
        var handler = new StubGraphHandler([
            JsonResponse($$"""
                {
                  "id": "{{servicePrincipalId}}",
                  "appId": "{{applicationId}}",
                  "appOwnerOrganizationId": "{{tenantId}}",
                  "appRoles": [
                    {
                      "id": "{{roleId}}",
                      "value": "Notifications.Send.Operational"
                    }
                  ]
                }
                """),
            JsonResponse("""{ "value": [] }"""),
        ]);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"),
        };

        try
        {
            var exitCode = await GoLiveCheckApplication.RunAsync(
                name => environment.GetValueOrDefault(name),
                (configuration, timeProvider) => new GoLiveGate(
                    new StubSource(GoLiveSourceIdentifiers.TemplateManagement, 0),
                    new MicrosoftGraphOperationalRoleSource(
                        client,
                        configuration.GraphAccessToken,
                        configuration.GraphTenantId,
                        configuration.GraphApplicationId,
                        configuration.GraphServicePrincipalId),
                    timeProvider),
                new FileReceiptWriter(),
                new FixedTimeProvider(new DateTimeOffset(2026, 8, 24, 15, 30, 0, TimeSpan.Zero)),
                CancellationToken.None);

            var json = await File.ReadAllTextAsync(receiptPath, CancellationToken.None);

            exitCode.ShouldBe(GoLiveExitCodes.Pass);
            json.ShouldContain($"\"tenantId\": \"{tenantId}\"");
            json.ShouldContain($"\"applicationId\": \"{applicationId}\"");
            json.ShouldContain($"\"servicePrincipalId\": \"{servicePrincipalId}\"");
            json.ShouldContain($"\"roleId\": \"{roleId}\"");
            json.ShouldContain("\"roleValue\": \"Notifications.Send.Operational\"");
            json.ShouldNotContain(token);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static Dictionary<string, string?> CompleteEnvironment(
        string receiptPath,
        string connectionString,
        string token)
        => new()
        {
            [GoLiveEnvironmentVariables.TemplateManagementConnectionString] = connectionString,
            [GoLiveEnvironmentVariables.GraphAccessToken] = token,
            [GoLiveEnvironmentVariables.GraphTenantId] = "99cc1efd-3f10-43a9-bc5d-00e47fe0f347",
            [GoLiveEnvironmentVariables.GraphApplicationId] = "fdc8b7f4-0956-478e-94d4-608d3f0ec244",
            [GoLiveEnvironmentVariables.GraphServicePrincipalId] = "738e728b-27e7-4ec9-a5d5-566acaf2022e",
            [GoLiveEnvironmentVariables.ReceiptPath] = receiptPath,
        };

    private sealed class RecordingReceiptWriter : IReceiptWriter
    {
        public string? Path { get; private set; }

        public GoLiveReceipt? Receipt { get; private set; }

        public ValueTask WriteAsync(
            string path,
            GoLiveReceipt receipt,
            CancellationToken cancellationToken)
        {
            Path = path;
            Receipt = receipt;
            return ValueTask.CompletedTask;
        }
    }

    private static GoLiveVerifiedIdentity VerifiedGraphIdentity()
        => new(
            new Guid("99cc1efd-3f10-43a9-bc5d-00e47fe0f347"),
            new Guid("fdc8b7f4-0956-478e-94d4-608d3f0ec244"),
            new Guid("738e728b-27e7-4ec9-a5d5-566acaf2022e"),
            new Guid("8dde5cb3-9a06-4e25-bf46-e70b1b5613f3"),
            MicrosoftGraphOperationalRoleSource.OperationalRole);

    private sealed class StubSource(
        string identifier,
        int count,
        GoLiveVerifiedIdentity? verifiedIdentity = null) : IGoLiveCheckSource
    {
        public string Identifier => identifier;

        public ValueTask<GoLiveSourceCheck> CheckAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new GoLiveSourceCheck(count, verifiedIdentity));
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class StubGraphHandler(IReadOnlyList<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_responses.Dequeue());
    }
}
