using System.Data.Common;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NotificationHub.Api.Modules.Audit.Infrastructure.AuditTrail;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.LocalStack;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.Audit;

/// <summary>
/// A database and an object store of its own, on purpose. The maintenance
/// round revokes grants and detaches partitions, which no other test suite can
/// survive sharing, and the export tests need a trail whose sequence ranges
/// nobody else writes into.
/// </summary>
public sealed class AuditMaintenanceFixture : IAsyncLifetime, IDisposable
{
    /// <summary>Bucket the tests export into; created with Object Lock enabled.</summary>
    public const string Bucket = "notification-hub-audit-worm-tests";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    private readonly LocalStackContainer _localStack = new LocalStackBuilder()
        .WithImage("localstack/localstack:4.4")
        .Build();

    private AmazonS3Client? _s3;
    private AmazonKeyManagementServiceClient? _kms;

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public string AwsEndpoint => _localStack.GetConnectionString();

    /// <summary>Managed signing key created for the tests that exercise the KMS provider.</summary>
    public string KmsKeyId { get; private set; } = string.Empty;

    public IAmazonS3 S3 => _s3 ?? throw new InvalidOperationException("O LocalStack ainda não foi iniciado.");

    public IAmazonKeyManagementService Kms
        => _kms ?? throw new InvalidOperationException("O LocalStack ainda não foi iniciado.");

    /// <summary>The maintenance composition against these containers, with the local signer by default.</summary>
    public ServiceProvider BuildProvider(
        Dictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null,
        ILoggerProvider? loggerProvider = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Modules:Audit:WormExport:Bucket"] = Bucket,
            ["Modules:Audit:WormExport:ServiceUrl"] = AwsEndpoint,
            ["Modules:Audit:WormExport:Region"] = "us-east-1",
            ["Modules:Audit:WormExport:AccessKey"] = "test",
            ["Modules:Audit:WormExport:SecretKey"] = "test",
            ["Modules:Audit:WormExport:ForcePathStyle"] = "true",
            ["Modules:Audit:WormExport:RetentionYears"] = "1",
            ["Platform:Cryptography:Attestation:ServiceUrl"] = AwsEndpoint,
            ["Platform:Cryptography:Attestation:Region"] = "us-east-1",
            ["Platform:Cryptography:Attestation:AccessKey"] = "test",
            ["Platform:Cryptography:Attestation:SecretKey"] = "test",
        };
        if (overrides is not null)
        {
            foreach ((var key, var value) in overrides)
            {
                settings[key] = value;
            }
        }

        return AuditMaintenanceComposition.Build(
            PostgresConnectionString, settings, configureServices, loggerProvider);
    }

    /// <summary>Appends one event through the production writer, in its own committed transaction.</summary>
    public async Task AppendAsync(string entityId, DateTimeOffset occurredAt, string? detailsJson = null)
    {
        var trail = new TransactionalAuditTrail();
        await using var connection = new NpgsqlConnection(PostgresConnectionString);
        await connection.OpenAsync();
        await using DbTransaction transaction = await connection.BeginTransactionAsync();
        await trail.AppendAsync(
            transaction,
            new AuditEntry
            {
                ActorType = AuditActorTypes.System,
                ActorId = "audit-maintenance-tests",
                Action = AuditActions.TemplateCreated,
                EntityType = AuditEntityTypes.Template,
                EntityId = entityId,
                DetailsJson = detailsJson ?? """{"origin":"audit-maintenance-tests"}""",
                OccurredAt = occurredAt,
            },
            CancellationToken.None);
        await transaction.CommitAsync();
    }

    /// <summary>Guarantees the monthly partition of an instant, including months already in the past.</summary>
    public async Task EnsurePartitionAsync(DateTimeOffset instant)
    {
        var from = new DateOnly(instant.UtcDateTime.Year, instant.UtcDateTime.Month, 1);
        DateOnly to = from.AddMonths(1);
        var sql = $"""
            CREATE TABLE IF NOT EXISTS audit."audit_event_{from.Year:D4}_{from.Month:D2}"
            PARTITION OF audit."audit_event"
            FOR VALUES FROM ('{from:yyyy-MM-dd} 00:00:00+00') TO ('{to:yyyy-MM-dd} 00:00:00+00')
            """;
        await ExecuteAsync(sql);
    }

    /// <summary>Runs raw SQL as the owning role; the tests use it to set up and to tamper.</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(PostgresConnectionString);
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Reads a single text column as the owning role, in the query's own order.</summary>
    public async Task<List<string>> QueryTextsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(PostgresConnectionString);
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    /// <summary>Reads a single scalar as the owning role.</summary>
    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(PostgresConnectionString);
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (!DockerEnvironment.IsAvailable)
        {
            return;
        }

        await Task.WhenAll(_postgres.StartAsync(), _localStack.StartAsync());
        await MigrateAsync();

        var credentials = new BasicAWSCredentials("test", "test");
        _s3 = new AmazonS3Client(credentials, new AmazonS3Config
        {
            ServiceURL = AwsEndpoint,
            AuthenticationRegion = "us-east-1",
            ForcePathStyle = true,
        });
        _kms = new AmazonKeyManagementServiceClient(credentials, new AmazonKeyManagementServiceConfig
        {
            ServiceURL = AwsEndpoint,
            AuthenticationRegion = "us-east-1",
        });

        // Object Lock can only be enabled at creation time, which is why the
        // bucket is infrastructure and never something the job provisions.
        await _s3.PutBucketAsync(new PutBucketRequest
        {
            BucketName = Bucket,
            ObjectLockEnabledForBucket = true,
        });

        CreateKeyResponse key = await _kms.CreateKeyAsync(new CreateKeyRequest
        {
            KeySpec = KeySpec.ECC_NIST_P256,
            KeyUsage = KeyUsageType.SIGN_VERIFY,
        });
        KmsKeyId = key.KeyMetadata.KeyId;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _localStack.DisposeAsync();
    }

    public void Dispose()
    {
        _s3?.Dispose();
        _kms?.Dispose();
    }

    /// <summary>
    /// TemplateManagement first on purpose: its history creates the trail
    /// tables the Audit adoption migration takes over.
    /// </summary>
    private async Task MigrateAsync()
    {
        DbContextOptions<TemplateManagementDbContext> templateOptions =
            new DbContextOptionsBuilder<TemplateManagementDbContext>()
                .UseNpgsql(PostgresConnectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "templatemanagement"))
                .Options;
        await using (var templates = new TemplateManagementDbContext(templateOptions))
        {
            await templates.Database.MigrateAsync();
        }

        DbContextOptions<AuditDbContext> auditOptions =
            new DbContextOptionsBuilder<AuditDbContext>()
                .UseNpgsql(PostgresConnectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "audit"))
                .Options;
        await using var audit = new AuditDbContext(auditOptions);
        await audit.Database.MigrateAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class AuditMaintenanceCollectionDefinition : ICollectionFixture<AuditMaintenanceFixture>
{
    public const string Name = "audit-maintenance";
}
