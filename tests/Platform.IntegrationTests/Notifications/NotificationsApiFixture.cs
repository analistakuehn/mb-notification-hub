using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace NotificationHub.IntegrationTests.Notifications;

/// <summary>
/// Boots the API against disposable Postgres and Redis containers, applies
/// every migration history in dependency order, and issues signed producer
/// and template-governance tokens accepted by the bearer scheme the host
/// binds from configuration.
/// </summary>
public sealed class NotificationsApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string Issuer = "integration-tests";
    private const string Audience = "notification-hub";

    /// <summary>Key prefix of every Redis entry the module writes in these tests.</summary>
    public const string RedisKeyPrefix = "it-notifications:";

    private readonly byte[] _signingKey = RandomNumberGenerator.GetBytes(32);
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public string RedisConnectionString => _redis.GetConnectionString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.ConfigureAppConfiguration((_, configuration)
            => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:Audit:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Modules:Notifications:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Modules:Notifications:Redis:ConnectionString"] = _redis.GetConnectionString(),
                ["Modules:Notifications:Redis:KeyPrefix"] = RedisKeyPrefix,
                ["Platform:Messaging:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Authentication:Schemes:Bearer:ValidIssuer"] = Issuer,
                ["Authentication:Schemes:Bearer:ValidAudiences:0"] = Audience,
                ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = Issuer,
                ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Convert.ToBase64String(_signingKey),
            }));

    /// <summary>Client authenticated as a producer carrying the given send roles.</summary>
    public HttpClient CreateProducerClient(string subject, params string[] sendRoles)
        => CreateClientWithToken(subject, sendRoles);

    /// <summary>Producer client for a host derived with <c>WithWebHostBuilder</c>.</summary>
    public HttpClient CreateProducerClient(
        WebApplicationFactory<Program> host,
        string subject,
        params string[] sendRoles)
    {
        HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken(subject, sendRoles));
        return client;
    }

    /// <summary>Client authenticated as support or internal tooling: the read role, nothing else.</summary>
    public HttpClient CreateReaderClient(string subject)
        => CreateClientWithToken(subject, [NotificationsAuthorizationSetup.ReadRole]);

    /// <summary>Reader client for a host derived with <c>WithWebHostBuilder</c>.</summary>
    public HttpClient CreateReaderClient(WebApplicationFactory<Program> host, string subject)
    {
        HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateToken(subject, [NotificationsAuthorizationSetup.ReadRole]));
        return client;
    }

    public HttpClient CreateAuthorClient(string subject)
        => CreateClientWithToken(subject, [AuthorizationSetup.AuthorRole]);

    public HttpClient CreatePublisherClient(string subject)
        => CreateClientWithToken(subject, [AuthorizationSetup.PublisherRole]);

    public HttpClient CreatePlatformAdminClient(string subject, string? objectId = null)
        => CreateClientWithToken(subject, ["Platform.Admin"], objectId);

    public HttpClient CreatePlatformAdminClient(
        WebApplicationFactory<Program> host,
        string subject,
        string? objectId = null)
    {
        HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(subject, ["Platform.Admin"], objectId));
        return client;
    }

    public HttpClient CreatePlatformAdminClientWithoutActor()
        => CreateClientWithToken(subject: null, ["Platform.Admin"]);

    public async Task<T> QueryNotificationsDbAsync<T>(Func<NotificationsDbContext, Task<T>> query)
    {
        using IServiceScope scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>());
    }

    public async Task ExecuteNotificationsDbAsync(Func<NotificationsDbContext, Task> action)
    {
        using IServiceScope scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>());
    }

    public async Task<T> QueryAuditDbAsync<T>(Func<AuditDbContext, Task<T>> query)
    {
        using IServiceScope scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuditDbContext>());
    }

    public async Task<T> QueryPlatformDbAsync<T>(Func<PlatformMessagingDbContext, Task<T>> query)
    {
        using IServiceScope scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<PlatformMessagingDbContext>());
    }

    public async Task<T> UsingScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using IServiceScope scope = Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (!DockerEnvironment.IsAvailable)
        {
            return;
        }

        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        using IServiceScope scope = Services.CreateScope();

        // TemplateManagement first on purpose: its history creates the audit
        // trail tables the Audit adoption migration takes over. Notifications
        // and the platform outbox have independent histories.
        await scope.ServiceProvider
            .GetRequiredService<TemplateManagementDbContext>()
            .Database.MigrateAsync();
        await scope.ServiceProvider
            .GetRequiredService<AuditDbContext>()
            .Database.MigrateAsync();
        await scope.ServiceProvider
            .GetRequiredService<NotificationsDbContext>()
            .Database.MigrateAsync();
        await scope.ServiceProvider
            .GetRequiredService<PlatformMessagingDbContext>()
            .Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    private HttpClient CreateClientWithToken(
        string? subject,
        IReadOnlyList<string> roles,
        string? objectId = null)
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken(subject, roles, objectId));
        return client;
    }

    private string CreateToken(
        string? subject,
        IReadOnlyList<string> roles,
        string? objectId = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["role"] = roles,
        };
        if (subject is not null)
        {
            claims["sub"] = subject;
        }

        if (objectId is not null)
        {
            claims["oid"] = objectId;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(_signingKey),
                SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}

[CollectionDefinition(Name)]
public sealed class NotificationsApiCollectionDefinition : ICollectionFixture<NotificationsApiFixture>
{
    public const string Name = "notifications-api";
}
