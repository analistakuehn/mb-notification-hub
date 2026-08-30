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
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace NotificationHub.IntegrationTests.ContactConsent;

/// <summary>
/// Boots the API against disposable Postgres and Redis containers, applies
/// every migration history in dependency order, and issues signed tokens
/// accepted by the bearer scheme the host binds from configuration. Redis only
/// exists because sibling modules require it to boot; nothing in this module
/// touches it.
/// </summary>
public sealed class ContactConsentApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string Issuer = "integration-tests";
    private const string Audience = "notification-hub";

    private readonly byte[] _signingKey = RandomNumberGenerator.GetBytes(32);
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.ConfigureAppConfiguration((_, configuration)
            => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:Audit:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Modules:Notifications:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Modules:Notifications:Redis:ConnectionString"] = _redis.GetConnectionString(),
                ["Modules:Notifications:Redis:KeyPrefix"] = "it-contact-consent:",
                ["Modules:ContactConsent:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Platform:Messaging:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Authentication:Schemes:Bearer:ValidIssuer"] = Issuer,
                ["Authentication:Schemes:Bearer:ValidAudiences:0"] = Audience,
                ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = Issuer,
                ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Convert.ToBase64String(_signingKey),
            }));

    /// <summary>Client authenticated with the given roles under a stable subject.</summary>
    public HttpClient CreateClientWithRoles(string subject, params string[] roles)
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken(subject, roles));
        return client;
    }

    /// <summary>Client for a host derived with <c>WithWebHostBuilder</c>.</summary>
    public HttpClient CreateClientWithRoles(
        WebApplicationFactory<Program> host,
        string subject,
        params string[] roles)
    {
        HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken(subject, roles));
        return client;
    }

    public async Task<T> QueryContactConsentDbAsync<T>(Func<ContactConsentDbContext, Task<T>> query)
    {
        using IServiceScope scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<ContactConsentDbContext>());
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
        // trail tables the Audit adoption migration takes over. The remaining
        // histories are independent.
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
        await scope.ServiceProvider
            .GetRequiredService<ContactConsentDbContext>()
            .Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    private string CreateToken(string subject, IReadOnlyList<string> roles)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["role"] = roles,
            },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(_signingKey),
                SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}

[CollectionDefinition(Name)]
public sealed class ContactConsentApiCollectionDefinition : ICollectionFixture<ContactConsentApiFixture>
{
    public const string Name = "contact-consent-api";
}
