using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>
/// Boots the API against a disposable Postgres container, applies the module
/// migrations, and issues signed test tokens accepted by the bearer scheme the
/// host binds from configuration.
/// </summary>
public sealed class TemplateManagementApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string Issuer = "integration-tests";
    private const string Audience = "notification-hub";

    private readonly byte[] _signingKey = RandomNumberGenerator.GetBytes(32);
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.ConfigureAppConfiguration((_, configuration)
            => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Modules:TemplateManagement:Cache:Redis:ConnectionString"] = "localhost:6379",
                ["Modules:TemplateManagement:Cache:Redis:InstanceName"] = "integration-tests:",
                ["Authentication:Schemes:Bearer:ValidIssuer"] = Issuer,
                ["Authentication:Schemes:Bearer:ValidAudiences:0"] = Audience,
                ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = Issuer,
                ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Convert.ToBase64String(_signingKey),
            }));

    /// <summary>Client authenticated as an author with the given stable subject id.</summary>
    public HttpClient CreateAuthorClient(string subject)
        => CreateClientWithToken(subject, AuthorizationSetup.AuthorRole);

    public HttpClient CreateClientWithToken(string subject, params string[] roles)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken(subject, roles));
        return client;
    }

    public async Task ExecuteDbAsync(Func<TemplateManagementDbContext, Task> action)
    {
        using IServiceScope scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<TemplateManagementDbContext>());
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (!DockerEnvironment.IsAvailable)
        {
            return;
        }

        await _postgres.StartAsync();
        using IServiceScope scope = Services.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<TemplateManagementDbContext>()
            .Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
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
public sealed class TemplateManagementApiCollectionDefinition : ICollectionFixture<TemplateManagementApiFixture>
{
    public const string Name = "template-management-api";
}
