using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;
using Npgsql;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

[Collection(AttachmentManagementApiCollectionDefinition.Name)]
public sealed class ProducerApplicationAuthorizationTests(AttachmentManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Claim_kind_is_part_of_the_grant_when_oid_and_sub_values_collide()
    {
        const string principalId = "shared-principal-value";
        const string issuer = "claim-kind-collision-tests";
        await AttachmentAuthorizationTestData.SeedGrantAsync(
            fixture.Services,
            issuer,
            "sub",
            principalId,
            AttachmentApi.Application);
        using AuthorizationTestHost host = AttachmentAuthorizationTestData.CreateHost(
            fixture,
            issuer,
            new Dictionary<string, object>
            {
                ["oid"] = principalId,
                ["sub"] = principalId,
            });

        using HttpResponseMessage denied = await host.Client.PostAsJsonAsync(
            "/v1/attachments",
            AttachmentApi.Registration(sizeBytes: 4));

        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await AttachmentAuthorizationTestData.SeedGrantAsync(
            fixture.Services,
            issuer,
            "oid",
            principalId,
            AttachmentApi.Application);
        using HttpResponseMessage allowed = await host.Client.PostAsJsonAsync(
            "/v1/attachments",
            AttachmentApi.Registration(sizeBytes: 4));

        allowed.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [RequiresDockerFact]
    public async Task Issuer_is_part_of_the_grant_key_without_cross_issuer_fallback()
    {
        const string grantedIssuer = "granted-issuer-tests";
        const string tokenIssuer = "different-token-issuer-tests";
        const string principalId = "issuer-isolated-principal";
        await AttachmentAuthorizationTestData.SeedGrantAsync(
            fixture.Services,
            grantedIssuer,
            "sub",
            principalId,
            AttachmentApi.Application);
        using AuthorizationTestHost host = AttachmentAuthorizationTestData.CreateHost(
            fixture,
            tokenIssuer,
            new Dictionary<string, object> { ["sub"] = principalId });

        using HttpResponseMessage denied = await host.Client.PostAsJsonAsync(
            "/v1/attachments",
            AttachmentApi.Registration(sizeBytes: 4));

        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await AttachmentAuthorizationTestData.SeedGrantAsync(
            fixture.Services,
            tokenIssuer,
            "sub",
            principalId,
            AttachmentApi.Application);
        using HttpResponseMessage allowed = await host.Client.PostAsJsonAsync(
            "/v1/attachments",
            AttachmentApi.Registration(sizeBytes: 4));

        allowed.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [RequiresDockerFact]
    public async Task Application_claim_does_not_create_or_replace_a_registry_grant()
    {
        const string issuer = "application-claim-tests";
        using AuthorizationTestHost host = AttachmentAuthorizationTestData.CreateHost(
            fixture,
            issuer,
            new Dictionary<string, object>
            {
                ["sub"] = "application-claim-principal",
                ["application"] = AttachmentApi.Application,
            });

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            "/v1/attachments",
            AttachmentApi.Registration(sizeBytes: 4));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [RequiresDockerFact]
    public async Task Readable_empty_registry_denies_without_creating_a_grant()
    {
        using AuthorizationTestHost host = await AttachmentAuthorizationTestData
            .CreateEmptyRegistryHostAsync(fixture, "empty-registry-principal");
        var reference = AttachmentReference.Generate().Value;

        using HttpResponseMessage post = await host.Client.PostAsJsonAsync(
            "/v1/attachments",
            AttachmentApi.Registration(sizeBytes: 4));
        using HttpResponseMessage get = await host.Client.GetAsync(
            $"/v1/attachments/{reference}");
        using HttpResponseMessage put = await AttachmentApi.PutContentAsync(
            host.Client,
            reference,
            "test");

        post.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        put.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using IServiceScope scope = host.Services.CreateScope();
        (await scope.ServiceProvider
                .GetRequiredService<AttachmentManagementDbContext>()
                .ProducerApplicationGrants
                .AsNoTracking()
                .CountAsync())
            .ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task Principal_application_and_reference_matrix_has_no_cross_access()
    {
        const string applicationA = "matrix-application-a";
        const string applicationB = "matrix-application-b";
        const string principalA = "matrix-principal-a";
        const string principalB = "matrix-principal-b";
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            principalA,
            applicationA);
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            principalB,
            applicationB);
        using HttpClient clientA = fixture.CreateProducerClient(principalA);
        using HttpClient clientB = fixture.CreateProducerClient(principalB);
        AttachmentApi.ApiResponse attachmentA = await RegisterAsync(clientA, applicationA);
        AttachmentApi.ApiResponse attachmentB = await RegisterAsync(clientB, applicationB);
        var attachmentCount = await CountAttachmentsAsync(fixture.Services);
        AttachmentObjectVersion[] objectVersions = await fixture.ObjectVersionsAsync();

        using HttpResponseMessage deniedPost = await clientA.PostAsJsonAsync(
            "/v1/attachments",
            AttachmentApi.Registration(sizeBytes: 4, application: applicationB));
        using HttpResponseMessage deniedGetA = await clientA.GetAsync(
            $"/v1/attachments/{attachmentB.Reference}");
        using HttpResponseMessage deniedGetB = await clientB.GetAsync(
            $"/v1/attachments/{attachmentA.Reference}");
        using HttpResponseMessage deniedPutA = await AttachmentApi.PutContentAsync(
            clientA,
            attachmentB.Reference,
            "test");
        using HttpResponseMessage deniedPutB = await AttachmentApi.PutContentAsync(
            clientB,
            attachmentA.Reference,
            "test");
        using HttpResponseMessage absentGet = await clientA.GetAsync(
            "/v1/attachments/not-a-reference");
        using HttpResponseMessage absentPut = await AttachmentApi.PutContentAsync(
            clientA,
            "not-a-reference",
            "test");
        var unknownReference = AttachmentReference.Generate().Value;
        using HttpResponseMessage unknownGet = await clientA.GetAsync(
            $"/v1/attachments/{unknownReference}");
        using HttpResponseMessage unknownPut = await AttachmentApi.PutContentAsync(
            clientA,
            unknownReference,
            "test");

        deniedPost.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        deniedGetA.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        deniedGetB.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        deniedPutA.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        deniedPutB.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        absentGet.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        absentPut.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unknownGet.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unknownPut.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await AttachmentAuthorizationAssertions.ShouldBeIntegralNotFoundAsync(
            absentGet,
            unknownGet,
            deniedGetA,
            deniedGetB);
        await AttachmentAuthorizationAssertions.ShouldBeIntegralNotFoundAsync(
            absentPut,
            unknownPut,
            deniedPutA,
            deniedPutB);
        (await CountAttachmentsAsync(fixture.Services)).ShouldBe(attachmentCount);
        (await fixture.ObjectVersionsAsync()).ShouldBe(objectVersions, ignoreOrder: true);
        (await fixture.QueryAttachmentAsync(attachmentA.Reference)).State
            .ShouldBe(AttachmentStates.AwaitingUpload);
        (await fixture.QueryAttachmentAsync(attachmentB.Reference)).State
            .ShouldBe(AttachmentStates.AwaitingUpload);
    }

    [RequiresDockerFact]
    public async Task Missing_registry_table_returns_503_for_application_and_reference_resources()
    {
        const string principal = "missing-registry-principal";
        using AuthorizationTestHost host = await AttachmentAuthorizationTestData.CreateMissingTableHostAsync(
            fixture,
            principal);
        var reference = host.KnownReference.ShouldNotBeNull();
        fixture.Logs.Events.Clear();

        using HttpResponseMessage post = await host.Client.PostAsJsonAsync(
            "/v1/attachments",
            AttachmentApi.Registration(sizeBytes: 4));
        using HttpResponseMessage get = await host.Client.GetAsync(
            $"/v1/attachments/{reference}");
        using HttpResponseMessage put = await AttachmentApi.PutContentAsync(
            host.Client,
            reference,
            "test");

        post.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        get.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        put.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        SentinelCapturedLogEvent[] registryLogs =
        [
            .. fixture.Logs.Events.Where(log => string.Equals(
                log.Message,
                "Registro de autorização de anexos indisponível.",
                StringComparison.Ordinal)),
        ];
        registryLogs.ShouldNotBeEmpty();
        registryLogs.ShouldAllBe(log => !string.IsNullOrWhiteSpace(log.Exception));
        string[] sensitiveValues =
        [
            AttachmentAuthorizationTestData.StandardIssuer,
            "sub",
            principal,
            AttachmentApi.Application,
            reference,
        ];
        string[] fragments =
        [
            .. registryLogs.Select(log => log.Message),
            .. registryLogs.Select(log => log.Exception ?? string.Empty),
            .. registryLogs.SelectMany(log => log.State.Select(value => value.Value)),
        ];
        foreach (var value in sensitiveValues)
        {
            fragments.ShouldAllBe(fragment =>
                !fragment.Contains(value, StringComparison.Ordinal));
        }

        using IServiceScope scope = host.Services.CreateScope();
        AttachmentReference parsed = AttachmentReference.Create(reference).Value.ShouldNotBeNull();
        (await scope.ServiceProvider
                .GetRequiredService<AttachmentManagementDbContext>()
                .Attachments
                .AsNoTracking()
                .AnyAsync(attachment => attachment.Reference == parsed))
            .ShouldBeTrue();
    }

    [RequiresDockerFact]
    public async Task Denied_upload_never_invokes_persistence_or_object_storage()
    {
        const string owner = "denied-upload-owner";
        const string caller = "denied-upload-caller";
        const string ownerApplication = "denied-upload-owner-app";
        const string callerApplication = "denied-upload-caller-app";
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            owner,
            ownerApplication);
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            caller,
            callerApplication);
        using HttpClient ownerClient = fixture.CreateProducerClient(owner);
        AttachmentApi.ApiResponse attachment = await RegisterAsync(
            ownerClient,
            ownerApplication);
        var saveOperation = new CountingAttachmentSaveOperation();
        var objectStore = new CountingAttachmentObjectStore();
        var bodyProbe = new ServerRequestBodyProbe();
        using WebApplicationFactory<Program> host = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAttachmentSaveOperation>();
                services.RemoveAll<IAttachmentObjectStore>();
                services.AddSingleton<IAttachmentSaveOperation>(saveOperation);
                services.AddSingleton<IAttachmentObjectStore>(objectStore);
                services.AddSingleton(bodyProbe);
                services.AddTransient<IStartupFilter, RequestBodyProbeStartupFilter>();
            }));
        using HttpClient callerClient = fixture.CreateProducerClient(host, caller);

        using HttpResponseMessage response = await AttachmentApi.PutContentAsync(
            callerClient,
            attachment.Reference,
            "test");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        bodyProbe.InstallCount.ShouldBe(1);
        bodyProbe.ReadCount.ShouldBe(0);
        saveOperation.CallCount.ShouldBe(0);
        objectStore.PutCallCount.ShouldBe(0);
        objectStore.DiscardCallCount.ShouldBe(0);
        (await fixture.QueryAttachmentAsync(attachment.Reference)).State
            .ShouldBe(AttachmentStates.AwaitingUpload);
    }

    private static async Task<AttachmentApi.ApiResponse> RegisterAsync(
        HttpClient client,
        string application)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/attachments",
            AttachmentApi.Registration(sizeBytes: 4, application: application));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await AttachmentApi.ReadMinimalResponseAsync(response);
    }

    private static async Task<int> CountAttachmentsAsync(IServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .Attachments
            .AsNoTracking()
            .CountAsync();
    }
}

internal static class AttachmentAuthorizationTestData
{
    internal const string StandardIssuer = "attachment-integration-tests";

    internal static Task SeedStandardGrantAsync(
        IServiceProvider services,
        string principalId,
        string application = AttachmentApi.Application)
        => SeedGrantAsync(services, StandardIssuer, "sub", principalId, application);

    internal static async Task SeedGrantAsync(
        IServiceProvider services,
        string issuer,
        string claimKind,
        string principalId,
        string application)
    {
        using IServiceScope scope = services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        var exists = await dbContext.ProducerApplicationGrants
            .AsNoTracking()
            .AnyAsync(grant => grant.Issuer == issuer
                && grant.ClaimKind == claimKind
                && grant.PrincipalId == principalId
                && grant.Application == application);
        if (exists)
        {
            return;
        }

        Result<ProducerApplicationGrant> created = ProducerApplicationGrant.Create(
            issuer,
            claimKind,
            principalId,
            application);
        dbContext.ProducerApplicationGrants.Add(created.Value.ShouldNotBeNull());
        await dbContext.SaveChangesAsync();
    }

    internal static AuthorizationTestHost CreateHost(
        AttachmentManagementApiFixture fixture,
        string issuer,
        IReadOnlyDictionary<string, object> claims)
    {
        var signingKey = RandomNumberGenerator.GetBytes(32);
        WebApplicationFactory<Program> host = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(AuthenticationSettings(
                    issuer,
                    signingKey))));
        HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(issuer, signingKey, claims));
        return new AuthorizationTestHost(host, client);
    }

    internal static async Task<AuthorizationTestHost> CreateMissingTableHostAsync(
        AttachmentManagementApiFixture fixture,
        string principal)
    {
        AuthorizationTestHost host = await CreateIsolatedRegistryHostAsync(
            fixture,
            principal,
            createTables: true);
        try
        {
            using IServiceScope scope = host.Services.CreateScope();
            AttachmentManagementDbContext dbContext = scope.ServiceProvider
                .GetRequiredService<AttachmentManagementDbContext>();
            Result<Attachment> registered = Attachment.Register(
                AttachmentApi.Application,
                AttachmentApi.FileName,
                AttachmentApi.ContentType,
                4,
                AttachmentApi.SeedSizeCeiling,
                DateTimeOffset.UtcNow);
            Result<ProducerApplicationGrant> grant = ProducerApplicationGrant.Create(
                StandardIssuer,
                "sub",
                principal,
                AttachmentApi.Application);
            Attachment attachment = registered.Value.ShouldNotBeNull();
            dbContext.Attachments.Add(attachment);
            dbContext.ProducerApplicationGrants.Add(grant.Value.ShouldNotBeNull());
            await dbContext.SaveChangesAsync();
            await dbContext.Database.ExecuteSqlRawAsync(
                "DROP TABLE attachmentmanagement.producer_application_grant");
            dbContext.ChangeTracker.Clear();
            (await dbContext.Attachments
                    .AsNoTracking()
                    .AnyAsync(candidate => candidate.Reference == attachment.Reference))
                .ShouldBeTrue();
            host.KnownReference = attachment.Reference.Value;
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    internal static async Task<AuthorizationTestHost> CreateEmptyRegistryHostAsync(
        AttachmentManagementApiFixture fixture,
        string principal)
        => await CreateIsolatedRegistryHostAsync(
            fixture,
            principal,
            createTables: true);

    private static async Task<AuthorizationTestHost> CreateIsolatedRegistryHostAsync(
        AttachmentManagementApiFixture fixture,
        string principal,
        bool createTables)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        var connectionString = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .Database
            .GetConnectionString()
            .ShouldNotBeNull();
        var administrationConnection = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
        };
        var emptyDatabase = $"attachment_authorization_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(
            administrationConnection.ConnectionString))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{emptyDatabase}\"";
            await command.ExecuteNonQueryAsync();
        }

        var missingTableConnection = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = emptyDatabase,
        };
        WebApplicationFactory<Program> host = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{AttachmentManagementEfOptions.SectionName}:ConnectionString"] =
                        missingTableConnection.ConnectionString,
                })));
        HttpClient client = fixture.CreateProducerClient(host, principal);
        if (createTables)
        {
            using IServiceScope isolatedScope = host.Services.CreateScope();
            AttachmentManagementDbContext dbContext = isolatedScope.ServiceProvider
                .GetRequiredService<AttachmentManagementDbContext>();

            // Migrated, never created from the model: the arrangement runs no
            // schema statement production does not run.
            await dbContext.Database.MigrateAsync();
        }

        return new AuthorizationTestHost(
            host,
            client,
            () => DropEmptyDatabase(administrationConnection.ConnectionString, emptyDatabase));
    }

    private static void DropEmptyDatabase(
        string administrationConnectionString,
        string database)
    {
        const string expectedPrefix = "attachment_authorization_";
        if (!database.StartsWith(expectedPrefix, StringComparison.Ordinal)
            || database.Length != expectedPrefix.Length + 32)
        {
            throw new InvalidOperationException("Refusing to drop an unexpected database name.");
        }

        var suffix = database[expectedPrefix.Length..];
        if (suffix.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("Refusing to drop an unexpected database name.");
        }

        using var connection = new NpgsqlConnection(administrationConnectionString);
        connection.Open();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE \"{database}\" WITH (FORCE)";
        command.ExecuteNonQuery();
    }

    private static Dictionary<string, string?> AuthenticationSettings(
        string issuer,
        byte[] signingKey)
        => new()
        {
            ["Authentication:Schemes:Bearer:ValidIssuer"] = issuer,
            ["Authentication:Schemes:Bearer:ValidAudiences:0"] = "notification-hub",
            ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = issuer,
            ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] =
                Convert.ToBase64String(signingKey),
        };

    private static string CreateToken(
        string issuer,
        byte[] signingKey,
        IReadOnlyDictionary<string, object> claims)
        => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = "notification-hub",
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object>(claims),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(signingKey),
                SecurityAlgorithms.HmacSha256),
        });
}

internal static class AttachmentAuthorizationAssertions
{
    internal static async Task ShouldBeIntegralNotFoundAsync(
        params HttpResponseMessage[] responses)
    {
        responses.Length.ShouldBeGreaterThan(1);
        Dictionary<string, string>? expectedBody = null;
        string? expectedContentType = null;
        foreach (HttpResponseMessage response in responses)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            var contentType = response.Content.Headers.ContentType
                .ShouldNotBeNull()
                .ToString();
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            root.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ShouldBe(["detail", "status", "title", "traceId", "type"]);
            root.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
            var comparableBody = root.EnumerateObject()
                .Where(property => property.Name != "traceId")
                .ToDictionary(
                    property => property.Name,
                    property => property.Value.GetRawText(),
                    StringComparer.Ordinal);
            if (expectedBody is null)
            {
                expectedBody = comparableBody;
                expectedContentType = contentType;
                continue;
            }

            comparableBody.ShouldBe(expectedBody);
            contentType.ShouldBe(expectedContentType);
        }
    }
}

internal sealed class AuthorizationTestHost(
    WebApplicationFactory<Program> host,
    HttpClient client,
    Action? cleanup = null) : IDisposable
{
    internal HttpClient Client { get; } = client;

    internal IServiceProvider Services => host.Services;

    internal string? KnownReference { get; set; }

    public void Dispose()
    {
        Client.Dispose();
        host.Dispose();
        cleanup?.Invoke();
    }
}

internal sealed class CountingAttachmentSaveOperation : IAttachmentSaveOperation
{
    internal int CallCount { get; private set; }

    public Task SaveChangesAsync(
        AttachmentManagementDbContext dbContext,
        CancellationToken cancellationToken)
    {
        _ = dbContext;
        _ = cancellationToken;
        CallCount++;
        return Task.CompletedTask;
    }
}

internal sealed class CountingAttachmentObjectStore : IAttachmentObjectStore
{
    internal int PutCallCount { get; private set; }

    internal int DiscardCallCount { get; private set; }

    public Task<AttachmentObjectCapture> PutAsync(
        AttachmentObjectRequest request,
        Stream content,
        CancellationToken cancellationToken)
    {
        _ = request;
        _ = content;
        _ = cancellationToken;
        PutCallCount++;
        return Task.FromResult(AttachmentObjectCapture.Unavailable());
    }

    public Task<AttachmentStoreOpen> OpenAsync(
        AttachmentObjectLocator locator,
        CancellationToken cancellationToken)
    {
        _ = locator;
        _ = cancellationToken;
        return Task.FromResult(AttachmentStoreOpen.Unavailable());
    }

    public Task<AttachmentObjectDiscard> DiscardAsync(
        AttachmentObjectLocator locator,
        CancellationToken cancellationToken)
    {
        _ = locator;
        _ = cancellationToken;
        DiscardCallCount++;
        return Task.FromResult(AttachmentObjectDiscard.Removed);
    }
}

internal sealed class ServerRequestBodyProbe
{
    internal int InstallCount { get; set; }

    internal int ReadCount { get; set; }
}

internal sealed class RequestBodyProbeStartupFilter(ServerRequestBodyProbe probe)
    : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => application =>
        {
            application.Use(async (context, pipelineNext) =>
            {
                if (string.Equals(context.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase)
                    && context.Request.Path.StartsWithSegments(
                        "/v1/attachments",
                        StringComparison.Ordinal))
                {
                    probe.InstallCount++;
                    context.Request.Body = new ThrowOnReadStream(
                        context.Request.Body,
                        probe);
                }

                await pipelineNext(context);
            });
            next(application);
        };
}

internal sealed class ThrowOnReadStream(
    Stream inner,
    ServerRequestBodyProbe probe) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        _ = buffer;
        _ = offset;
        _ = count;
        return RefuseRead();
    }

    public override int Read(Span<byte> buffer)
    {
        _ = buffer;
        return RefuseRead();
    }

    public override int ReadByte() => RefuseRead();

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        _ = buffer;
        _ = offset;
        _ = count;
        _ = cancellationToken;
        return Task.FromResult(RefuseRead());
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        _ = buffer;
        _ = cancellationToken;
        return ValueTask.FromResult(RefuseRead());
    }

    public override long Seek(long offset, SeekOrigin origin)
        => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    private int RefuseRead()
    {
        probe.ReadCount++;
        throw new InvalidOperationException("The denied request body must not be read.");
    }
}
