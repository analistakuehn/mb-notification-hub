using System.Net.Http.Headers;
using System.Text.Json;

namespace NotificationHub.Platform.GoLiveChecks;

internal sealed class MicrosoftGraphOperationalRoleSource : IGoLiveCheckSource
{
    private const string GraphHost = "graph.microsoft.com";
    private const int MaximumAssignmentPages = 1_000;
    internal const string OperationalRole = "Notifications.Send.Operational";

    private readonly HttpClient _httpClient;
    private readonly string _accessToken;
    private readonly Guid _tenantId;
    private readonly Guid _applicationId;
    private readonly Guid _servicePrincipalId;

    public MicrosoftGraphOperationalRoleSource(
        HttpClient httpClient,
        string accessToken,
        Guid tenantId,
        Guid applicationId,
        Guid servicePrincipalId)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentOutOfRangeException.ThrowIfEqual(tenantId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(applicationId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(servicePrincipalId, Guid.Empty);
        Uri baseAddress = httpClient.BaseAddress
            ?? throw new ArgumentException("Microsoft Graph base address is required.", nameof(httpClient));
        if (!string.Equals(baseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(baseAddress.Host, GraphHost, StringComparison.OrdinalIgnoreCase)
            || !baseAddress.IsDefaultPort
            || !string.IsNullOrEmpty(baseAddress.UserInfo))
        {
            throw new ArgumentException("Microsoft Graph base address must use https://graph.microsoft.com.", nameof(httpClient));
        }

        _httpClient = httpClient;
        _accessToken = accessToken;
        _tenantId = tenantId;
        _applicationId = applicationId;
        _servicePrincipalId = servicePrincipalId;
    }

    public string Identifier => GoLiveSourceIdentifiers.MicrosoftGraph;

    public async ValueTask<int> CountAsync(CancellationToken cancellationToken)
    {
        GoLiveSourceCheck result = await CheckAsync(cancellationToken);
        return result.Count;
    }

    public async ValueTask<GoLiveSourceCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var servicePrincipalPath = $"servicePrincipals/{_servicePrincipalId}";
        using JsonDocument servicePrincipal = await GetAsync(
            new Uri(
                $"{servicePrincipalPath}?$select=id,appId,appOwnerOrganizationId,appRoles",
                UriKind.Relative),
            cancellationToken);
        GoLiveVerifiedIdentity verifiedIdentity = ResolveIdentity(servicePrincipal.RootElement);

        Uri? nextPage = new($"{servicePrincipalPath}/appRoleAssignedTo?$select=appRoleId", UriKind.Relative);
        HashSet<string> visitedPages = new(StringComparer.Ordinal);
        var count = 0;
        var pageCount = 0;
        while (nextPage is not null)
        {
            var absolutePage = new Uri(_httpClient.BaseAddress!, nextPage).AbsoluteUri;
            if (!visitedPages.Add(absolutePage) || pageCount++ >= MaximumAssignmentPages)
            {
                throw new InvalidDataException("Microsoft Graph returned an unsafe assignment pagination sequence.");
            }

            using JsonDocument assignments = await GetAsync(nextPage, cancellationToken);
            JsonElement values = ReadRequiredArray(
                assignments.RootElement,
                "value",
                "assignment page");
            foreach (JsonElement assignment in values.EnumerateArray())
            {
                Guid assignmentRoleId = ReadRequiredGuid(assignment, "appRoleId", "assignment");
                if (assignmentRoleId == verifiedIdentity.RoleId)
                {
                    count++;
                }
            }

            nextPage = ReadNextPage(assignments.RootElement, servicePrincipalPath);
        }

        return new GoLiveSourceCheck(count, verifiedIdentity);
    }

    private async Task<JsonDocument> GetAsync(Uri requestUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
    }

    private GoLiveVerifiedIdentity ResolveIdentity(JsonElement servicePrincipal)
    {
        Guid servicePrincipalId = ReadRequiredGuid(servicePrincipal, "id", "service principal");
        Guid applicationId = ReadRequiredGuid(servicePrincipal, "appId", "service principal");
        Guid tenantId = ReadRequiredGuid(
            servicePrincipal,
            "appOwnerOrganizationId",
            "service principal");
        if (servicePrincipalId != _servicePrincipalId
            || applicationId != _applicationId
            || tenantId != _tenantId)
        {
            throw new InvalidDataException("Microsoft Graph returned an unexpected service principal identity.");
        }

        JsonElement appRoles = ReadRequiredArray(
            servicePrincipal,
            "appRoles",
            "service principal");
        Guid? operationalRoleId = null;
        foreach (JsonElement role in appRoles.EnumerateArray())
        {
            var value = ReadRequiredString(role, "value", "app role");
            Guid appRoleId = ReadRequiredGuid(role, "id", "app role");
            if (string.Equals(value, OperationalRole, StringComparison.Ordinal))
            {
                if (operationalRoleId is not null)
                {
                    throw new InvalidDataException("Microsoft Graph returned duplicate canonical operational app roles.");
                }

                operationalRoleId = appRoleId;
            }
        }

        Guid roleId = operationalRoleId
            ?? throw new InvalidDataException("Microsoft Graph did not return the canonical operational app role.");
        return new GoLiveVerifiedIdentity(
            tenantId,
            applicationId,
            servicePrincipalId,
            roleId,
            OperationalRole);
    }

    private Uri? ReadNextPage(JsonElement response, string servicePrincipalPath)
    {
        if (!response.TryGetProperty("@odata.nextLink", out JsonElement nextLink))
        {
            return null;
        }

        if (nextLink.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Microsoft Graph returned an invalid assignment page URI.");
        }

        var nextLinkValue = nextLink.GetString();
        if (string.IsNullOrWhiteSpace(nextLinkValue)
            || !Uri.TryCreate(nextLinkValue, UriKind.Absolute, out Uri? absolute))
        {
            throw new InvalidDataException("Microsoft Graph returned an invalid assignment page URI.");
        }

        var expectedPrefix = new Uri(_httpClient.BaseAddress!, $"{servicePrincipalPath}/appRoleAssignedTo");
        if (!string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(absolute.Host, GraphHost, StringComparison.OrdinalIgnoreCase)
            || !absolute.IsDefaultPort
            || !string.IsNullOrEmpty(absolute.UserInfo)
            || !string.IsNullOrEmpty(absolute.Fragment)
            || !string.Equals(absolute.AbsolutePath, expectedPrefix.AbsolutePath, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Microsoft Graph returned an invalid assignment page URI.");
        }

        return absolute;
    }

    private static JsonElement ReadRequiredArray(
        JsonElement container,
        string propertyName,
        string payloadName)
    {
        if (container.ValueKind != JsonValueKind.Object
            || !container.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Microsoft Graph returned an invalid {payloadName} payload.");
        }

        return value;
    }

    private static string ReadRequiredString(
        JsonElement container,
        string propertyName,
        string payloadName)
    {
        if (container.ValueKind != JsonValueKind.Object
            || !container.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Microsoft Graph returned an invalid {payloadName} payload.");
        }

        var result = value.GetString();
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidDataException($"Microsoft Graph returned an invalid {payloadName} payload.");
        }

        return result;
    }

    private static Guid ReadRequiredGuid(
        JsonElement container,
        string propertyName,
        string payloadName)
    {
        var value = ReadRequiredString(container, propertyName, payloadName);
        if (!Guid.TryParse(value, out Guid id) || id == Guid.Empty)
        {
            throw new InvalidDataException($"Microsoft Graph returned an invalid {payloadName} payload.");
        }

        return id;
    }
}
