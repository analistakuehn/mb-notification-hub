using System.Net;
using System.Text;
using NotificationHub.Platform.GoLiveChecks;

namespace NotificationHub.UnitTests.GoLive;

public sealed class MicrosoftGraphOperationalRoleSourceTests
{
    private static readonly Guid TenantId = new("99cc1efd-3f10-43a9-bc5d-00e47fe0f347");
    private static readonly Guid WrongTenantId = new("36ef3a62-c2cb-47de-bcf6-bf8fec35f2e1");
    private static readonly Guid ApplicationId = new("fdc8b7f4-0956-478e-94d4-608d3f0ec244");
    private static readonly Guid WrongApplicationId = new("d6454b85-d3ae-4842-97ea-33341377e72f");
    private static readonly Guid ServicePrincipalId = new("738e728b-27e7-4ec9-a5d5-566acaf2022e");
    private static readonly Guid WrongServicePrincipalId = new("49716a53-42d5-493f-936a-7c6df56e27b9");
    private static readonly Guid RoleId = new("8dde5cb3-9a06-4e25-bf46-e70b1b5613f3");

    [Fact]
    public async Task Source_resolves_the_canonical_role_and_counts_every_assignment_page()
    {
        const string token = "secret-graph-token";
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler([
            JsonResponse($$"""
                {
                  "id": "{{ServicePrincipalId}}",
                  "appId": "{{ApplicationId}}",
                  "appOwnerOrganizationId": "{{TenantId}}",
                  "appRoles": [
                    {
                      "id": "{{RoleId}}",
                      "value": "Notifications.Send.Operational"
                    }
                  ]
                }
                """),
            JsonResponse($$"""
                {
                  "value": [
                    { "appRoleId": "{{RoleId}}" }
                  ],
                  "@odata.nextLink": "https://graph.microsoft.com/v1.0/servicePrincipals/{{ServicePrincipalId}}/appRoleAssignedTo?$select=appRoleId&$skiptoken=next"
                }
                """),
            JsonResponse($$"""
                {
                  "value": [
                    { "appRoleId": "{{RoleId}}" }
                  ]
                }
                """),
        ]);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"),
        };
        MicrosoftGraphOperationalRoleSource source = CreateSource(client, token);

        GoLiveSourceCheck result = await source.CheckAsync(cancellation.Token);

        result.Count.ShouldBe(2);
        result.VerifiedIdentity.ShouldBe(new GoLiveVerifiedIdentity(
            TenantId,
            ApplicationId,
            ServicePrincipalId,
            RoleId,
            MicrosoftGraphOperationalRoleSource.OperationalRole));
        handler.Requests.Select(request => request.Uri.AbsoluteUri).ShouldBe([
            $"https://graph.microsoft.com/v1.0/servicePrincipals/{ServicePrincipalId}?$select=id,appId,appOwnerOrganizationId,appRoles",
            $"https://graph.microsoft.com/v1.0/servicePrincipals/{ServicePrincipalId}/appRoleAssignedTo?$select=appRoleId",
            $"https://graph.microsoft.com/v1.0/servicePrincipals/{ServicePrincipalId}/appRoleAssignedTo?$select=appRoleId&$skiptoken=next",
        ]);
        handler.Requests.ShouldAllBe(request => request.AuthorizationScheme == "Bearer");
        handler.Requests.ShouldAllBe(request => request.AuthorizationParameter == token);
        handler.Requests.ShouldAllBe(request => request.CancellationToken.CanBeCanceled);
    }

    [Fact]
    public async Task Empty_app_roles_fails_closed()
    {
        var handler = new RecordingHandler([JsonResponse($$"""
            {
              "id": "{{ServicePrincipalId}}",
              "appId": "{{ApplicationId}}",
              "appOwnerOrganizationId": "{{TenantId}}",
              "appRoles": []
            }
            """)]);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"),
        };
        MicrosoftGraphOperationalRoleSource source = CreateSource(client);

        await Should.ThrowAsync<InvalidDataException>(
            () => source.CountAsync(CancellationToken.None).AsTask());
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Missing_canonical_role_fails_closed()
    {
        var handler = new RecordingHandler([JsonResponse($$"""
            {
              "id": "{{ServicePrincipalId}}",
              "appId": "{{ApplicationId}}",
              "appOwnerOrganizationId": "{{TenantId}}",
              "appRoles": [
                {
                  "id": "{{RoleId}}",
                  "value": "Notifications.Send.Transactional"
                }
              ]
            }
            """)]);
        using HttpClient client = CreateClient(handler);
        MicrosoftGraphOperationalRoleSource source = CreateSource(client);

        await Should.ThrowAsync<InvalidDataException>(
            () => source.CountAsync(CancellationToken.None).AsTask());

        handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(UnexpectedResourceIdentities))]
    public async Task Unexpected_resource_identity_fails_closed(
        Guid returnedTenantId,
        Guid returnedApplicationId,
        Guid returnedServicePrincipalId)
    {
        var handler = new RecordingHandler([
            JsonResponse($$"""
                {
                  "id": "{{returnedServicePrincipalId}}",
                  "appId": "{{returnedApplicationId}}",
                  "appOwnerOrganizationId": "{{returnedTenantId}}",
                  "appRoles": [
                    {
                      "id": "{{RoleId}}",
                      "value": "Notifications.Send.Operational"
                    }
                  ]
                }
                """),
            JsonResponse("""{ "value": [] }"""),
        ]);
        using HttpClient client = CreateClient(handler);
        MicrosoftGraphOperationalRoleSource source = CreateSource(client);

        await Should.ThrowAsync<InvalidDataException>(
            () => source.CountAsync(CancellationToken.None).AsTask());

        handler.Requests.Count.ShouldBe(1);
    }

    public static TheoryData<Guid, Guid, Guid> UnexpectedResourceIdentities
        => new()
        {
            { WrongTenantId, ApplicationId, ServicePrincipalId },
            { TenantId, WrongApplicationId, ServicePrincipalId },
            { TenantId, ApplicationId, WrongServicePrincipalId },
        };

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "appRoles": {} }""")]
    [InlineData("""{ "appRoles": [null] }""")]
    [InlineData("""{ "appRoles": [{ "value": "Notifications.Send.Operational" }] }""")]
    [InlineData("""{ "appRoles": [{ "id": 42, "value": "Notifications.Send.Operational" }] }""")]
    [InlineData("""{ "appRoles": [{ "id": "not-a-guid", "value": "Notifications.Send.Operational" }] }""")]
    [InlineData("""{ "appRoles": [{ "id": "00000000-0000-0000-0000-000000000000", "value": "Notifications.Send.Operational" }] }""")]
    [InlineData("""{ "appRoles": [{ "id": "8dde5cb3-9a06-4e25-bf46-e70b1b5613f3" }] }""")]
    [InlineData("""{ "appRoles": [{ "id": "8dde5cb3-9a06-4e25-bf46-e70b1b5613f3", "value": 42 }] }""")]
    public async Task Malformed_service_principal_payload_fails_closed(string payload)
    {
        var handler = new RecordingHandler([JsonResponse(payload)]);
        using HttpClient client = CreateClient(handler);
        MicrosoftGraphOperationalRoleSource source = CreateSource(client);

        await Should.ThrowAsync<InvalidDataException>(
            () => source.CountAsync(CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "value": {} }""")]
    [InlineData("""{ "value": [null] }""")]
    [InlineData("""{ "value": [{}] }""")]
    [InlineData("""{ "value": [{ "appRoleId": null }] }""")]
    [InlineData("""{ "value": [{ "appRoleId": 42 }] }""")]
    [InlineData("""{ "value": [{ "appRoleId": "not-a-guid" }] }""")]
    [InlineData("""{ "value": [{ "appRoleId": "00000000-0000-0000-0000-000000000000" }] }""")]
    public async Task Malformed_assignment_page_fails_closed(string payload)
    {
        var handler = new RecordingHandler([ValidRoleResponse(), JsonResponse(payload)]);
        using HttpClient client = CreateClient(handler);
        MicrosoftGraphOperationalRoleSource source = CreateSource(client);

        await Should.ThrowAsync<InvalidDataException>(
            () => source.CountAsync(CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"\"")]
    [InlineData("\"not a URL\"")]
    public async Task Malformed_next_link_fails_closed(string nextLink)
    {
        var handler = new RecordingHandler([
            ValidRoleResponse(),
            JsonResponse($$"""{ "value": [], "@odata.nextLink": {{nextLink}} }"""),
        ]);
        using HttpClient client = CreateClient(handler);
        MicrosoftGraphOperationalRoleSource source = CreateSource(client);

        await Should.ThrowAsync<InvalidDataException>(
            () => source.CountAsync(CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData("https://example.com/v1.0/servicePrincipals/738e728b-27e7-4ec9-a5d5-566acaf2022e/appRoleAssignedTo?$select=appRoleId")]
    [InlineData("https://graph.microsoft.com/v1.0/servicePrincipals/49716a53-42d5-493f-936a-7c6df56e27b9/appRoleAssignedTo?$select=appRoleId")]
    [InlineData("https://graph.microsoft.com:444/v1.0/servicePrincipals/738e728b-27e7-4ec9-a5d5-566acaf2022e/appRoleAssignedTo?$select=appRoleId")]
    [InlineData("https://user@graph.microsoft.com/v1.0/servicePrincipals/738e728b-27e7-4ec9-a5d5-566acaf2022e/appRoleAssignedTo?$select=appRoleId")]
    public async Task Assignment_pagination_outside_the_expected_graph_resource_fails_closed(
        string nextLink)
    {
        var handler = new RecordingHandler([
            ValidRoleResponse(),
            JsonResponse($$"""{ "value": [], "@odata.nextLink": "{{nextLink}}" }"""),
        ]);
        using HttpClient client = CreateClient(handler);
        MicrosoftGraphOperationalRoleSource source = CreateSource(client);

        await Should.ThrowAsync<InvalidDataException>(
            () => source.CountAsync(CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData("http://graph.microsoft.com/v1.0/")]
    [InlineData("https://example.com/v1.0/")]
    [InlineData("https://graph.microsoft.com:444/v1.0/")]
    [InlineData("https://user@graph.microsoft.com/v1.0/")]
    public void Noncanonical_graph_base_address_is_rejected(string baseAddress)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri(baseAddress),
        };

        Should.Throw<ArgumentException>(() => CreateSource(client));
    }

    [Fact]
    public async Task Assignment_page_cycle_fails_closed()
    {
        var handler = new RecordingHandler([
            ValidRoleResponse(),
            JsonResponse($$"""
                {
                  "value": [],
                  "@odata.nextLink": "https://graph.microsoft.com/v1.0/servicePrincipals/{{ServicePrincipalId}}/appRoleAssignedTo?$select=appRoleId"
                }
                """),
        ]);
        using HttpClient client = CreateClient(handler);
        MicrosoftGraphOperationalRoleSource source = CreateSource(client);

        await Should.ThrowAsync<InvalidDataException>(
            () => source.CountAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Explicitly_empty_assignment_array_has_no_assignments_to_count()
    {
        var handler = new RecordingHandler([
            ValidRoleResponse(),
            JsonResponse("""{ "value": [] }"""),
        ]);
        using HttpClient client = CreateClient(handler);
        MicrosoftGraphOperationalRoleSource source = CreateSource(client);

        var count = await source.CountAsync(CancellationToken.None);

        count.ShouldBe(0);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
        => new(handler)
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"),
        };

    private static MicrosoftGraphOperationalRoleSource CreateSource(
        HttpClient client,
        string accessToken = "secret")
        => new(client, accessToken, TenantId, ApplicationId, ServicePrincipalId);

    private static HttpResponseMessage ValidRoleResponse()
        => JsonResponse($$"""
            {
              "id": "{{ServicePrincipalId}}",
              "appId": "{{ApplicationId}}",
              "appOwnerOrganizationId": "{{TenantId}}",
              "appRoles": [
                {
                  "id": "{{RoleId}}",
                  "value": "Notifications.Send.Operational"
                }
              ]
            }
            """);

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler(IReadOnlyList<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.RequestUri!,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                cancellationToken));
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed record RecordedRequest(
        Uri Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        CancellationToken CancellationToken);
}
