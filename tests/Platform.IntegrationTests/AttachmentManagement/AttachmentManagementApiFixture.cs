using System.Net.Http.Headers;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.LocalStack;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

public sealed class AttachmentManagementApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    internal const string Bucket = "notification-hub-attachment-tests";
    internal const string AccessKey = "attachment-test-access";
    internal const string SecretKey = "attachment-test-secret";

    private const string Issuer = "attachment-integration-tests";
    private const string Audience = "notification-hub";

    private readonly byte[] _signingKey = RandomNumberGenerator.GetBytes(32);
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();
    private readonly LocalStackContainer _localStack = new LocalStackBuilder()
        .WithImage("localstack/localstack:4.4")
        .Build();
    private AmazonS3Client? _s3;

    internal SentinelLogCaptureProvider Logs { get; } = new();

    internal string AwsEndpoint => _localStack.GetConnectionString();

    internal IAmazonS3 S3
        => _s3 ?? throw new InvalidOperationException("LocalStack has not started.");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{AttachmentManagementEfOptions.SectionName}:ConnectionString"] =
                    _postgres.GetConnectionString(),
                [$"{AttachmentObjectStoreOptions.SectionName}:Bucket"] = Bucket,
                [$"{AttachmentObjectStoreOptions.SectionName}:ServiceUrl"] = AwsEndpoint,
                [$"{AttachmentObjectStoreOptions.SectionName}:Region"] = "us-east-1",
                [$"{AttachmentObjectStoreOptions.SectionName}:AccessKey"] = AccessKey,
                [$"{AttachmentObjectStoreOptions.SectionName}:SecretKey"] = SecretKey,
                [$"{AttachmentObjectStoreOptions.SectionName}:ForcePathStyle"] = "true",
                ["Authentication:Schemes:Bearer:ValidIssuer"] = Issuer,
                ["Authentication:Schemes:Bearer:ValidAudiences:0"] = Audience,
                ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = Issuer,
                ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] =
                    Convert.ToBase64String(_signingKey),
            }));
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
    }

    internal HttpClient CreateProducerClient(string subject)
        => CreateProducerClient(this, subject);

    internal HttpClient CreateProducerClient(
        WebApplicationFactory<Program> host,
        string subject)
    {
        HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(subject));
        return client;
    }

    /// <summary>
    /// A caller of the operations surface. It carries a role and no grant,
    /// which is the whole point: the reading that tells the checks apart is a
    /// different job from producing, and a token good for one is not good for
    /// the other.
    /// </summary>
    internal HttpClient CreateOperationsClient(
        WebApplicationFactory<Program> host,
        string subject,
        params string[] roles)
    {
        HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(subject, roles));
        return client;
    }

    internal async Task<Attachment> QueryAttachmentAsync(string reference)
    {
        AttachmentReference parsed = AttachmentReference.Create(reference).Value.ShouldNotBeNull();
        using IServiceScope scope = Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .Attachments
            .AsNoTracking()
            .SingleAsync(attachment => attachment.Reference == parsed);
    }

    /// <summary>
    /// Every generation the store holds under the prefix, delete markers
    /// included. Listing current objects would hide a second generation
    /// written under a key that already existed, which is exactly the state a
    /// versioned store makes possible.
    /// <para>
    /// The bucket is a parameter because tests that build a store of their own
    /// need to look at that store. Pinned to the fixture bucket, this helper
    /// left every oracle over an ad hoc bucket unable to see whether bytes had
    /// been placed there at all.
    /// </para>
    /// </summary>
    internal async Task<AttachmentObjectVersion[]> ObjectVersionsAsync(
        string? prefix = null,
        string? bucket = null)
    {
        var versions = new List<AttachmentObjectVersion>();
        string? keyMarker = null;
        string? versionIdMarker = null;
        do
        {
            ListVersionsResponse response = await S3.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = bucket ?? Bucket,
                Prefix = prefix,
                KeyMarker = keyMarker,
                VersionIdMarker = versionIdMarker,
                MaxKeys = 100,
            });
            versions.AddRange((response.Versions ?? []).Select(item => new AttachmentObjectVersion(
                item.Key,
                item.VersionId,
                item.IsDeleteMarker ?? false)));
            var truncated = response.IsTruncated ?? false;
            keyMarker = truncated ? response.NextKeyMarker : null;
            versionIdMarker = truncated ? response.NextVersionIdMarker : null;
        }
        while (keyMarker is not null);

        return [.. versions];
    }

    internal async Task<string> ReadObjectAsync(
        AttachmentObjectVersion version,
        string? bucket = null)
    {
        using GetObjectResponse response = await S3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket ?? Bucket,
            Key = version.Key,
            VersionId = version.VersionId,
        });
        using var reader = new StreamReader(response.ResponseStream);
        return await reader.ReadToEndAsync();
    }

    internal static async Task EnableVersioningAsync(IAmazonS3 s3, string bucket)
        => await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (!DockerEnvironment.IsAvailable)
        {
            return;
        }

        await Task.WhenAll(_postgres.StartAsync(), _localStack.StartAsync());
        var credentials = new BasicAWSCredentials(AccessKey, SecretKey);
        _s3 = new AmazonS3Client(credentials, new AmazonS3Config
        {
            ServiceURL = AwsEndpoint,
            AuthenticationRegion = "us-east-1",
            ForcePathStyle = true,
        });
        await _s3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
        await EnableVersioningAsync(_s3, Bucket);

        using IServiceScope scope = Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        IRelationalDatabaseCreator databaseCreator = dbContext.Database
            .GetService<IRelationalDatabaseCreator>();
        await databaseCreator.CreateTablesAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        _s3?.Dispose();
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _localStack.DisposeAsync();
    }

    private string CreateToken(string subject, IReadOnlyList<string>? roles = null)
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject };
        if (roles is { Count: > 0 })
        {
            claims["role"] = roles;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(_signingKey),
                SecurityAlgorithms.HmacSha256),
        });
    }
}

/// <summary>One generation the store holds, named by key and generation id.</summary>
internal sealed record AttachmentObjectVersion(string Key, string VersionId, bool IsDeleteMarker);

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AttachmentManagementApiCollectionDefinition
    : ICollectionFixture<AttachmentManagementApiFixture>
{
    public const string Name = "attachment-management-api";
}
