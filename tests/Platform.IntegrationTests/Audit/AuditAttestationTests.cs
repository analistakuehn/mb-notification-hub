using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.Export;
using NotificationHub.Api.Modules.Audit.Infrastructure.Worm;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Audit;

/// <summary>
/// Signing evidence with the managed key service instead of a local key. The
/// point of these tests is that nothing else changes: the same exporter, the
/// same artifacts, the same verification, one configuration value apart.
/// </summary>
[Collection(AuditMaintenanceCollectionDefinition.Name)]
public sealed class AuditAttestationTests(AuditMaintenanceFixture fixture)
{
    [RequiresDockerFact]
    public async Task An_export_signed_by_the_key_service_verifies_with_the_archived_public_key()
    {
        DateTimeOffset month = MonthOffset(-12);
        DateTimeOffset day = month.AddDays(7);
        MonthlyPartitionWindow window = WindowOf(month);
        await fixture.EnsurePartitionAsync(month);
        await fixture.AppendAsync($"attest-kms-{Guid.CreateVersion7():N}", day);

        await using ServiceProvider provider = fixture.BuildProvider(new Dictionary<string, string?>
        {
            ["Platform:Cryptography:Attestation:Provider"] = "kms",
            ["Platform:Cryptography:Attestation:KeyId"] = fixture.KmsKeyId,
        });

        using (IServiceScope scope = provider.CreateScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<AuditExportPlanner>()
                .RunDailyAsync([window], CancellationToken.None);
        }

        var folder = AuditExportKeys.DailyFolder(
            "audit-export/v1", "audit_event", window.PartitionName, DateOnly.FromDateTime(day.UtcDateTime));

        WormVerificationResult verdict = await BucketOnlyVerifier()
            .VerifyAsync(folder + AuditExportKeys.ManifestObject, CancellationToken.None);
        verdict.IsValid.ShouldBeTrue(verdict.Failure);

        // The artifact says which key signed it and how, which is what lets a
        // verifier check it without knowing who produced it.
        var attestation = AuditAttestationDocument.Parse(
            await ReadObjectAsync(folder + AuditExportKeys.AttestationObject));
        attestation.KeyId.ShouldBe(fixture.KmsKeyId);
        attestation.Algorithm.ShouldBe(AttestationAlgorithms.EcdsaSha256);

        var archived = AuditAttestationKeyDocument.Parse(
            await ReadObjectAsync(AuditExportKeys.PublicKeyObject("audit-export/v1", fixture.KmsKeyId)));
        archived.KeyId.ShouldBe(fixture.KmsKeyId);
        AttestationVerification
            .VerifyDigest(archived.ToPublicKey(), AuditHex.FromHex(attestation.ManifestDigest), attestation.Signature)
            .ShouldBeTrue();
    }

    [RequiresDockerFact]
    public async Task A_signature_from_one_provider_does_not_verify_under_the_other_provider_key()
    {
        var digest = AuditDigest.Compute(AuditHex.Utf8("evidência de atestado"));

        await using ServiceProvider kmsProvider = fixture.BuildProvider(new Dictionary<string, string?>
        {
            ["Platform:Cryptography:Attestation:Provider"] = "kms",
            ["Platform:Cryptography:Attestation:KeyId"] = fixture.KmsKeyId,
        });
        await using ServiceProvider localProvider = fixture.BuildProvider();

        IAttestationSigner kms = kmsProvider.GetRequiredService<IAttestationSigner>();
        IAttestationSigner local = localProvider.GetRequiredService<IAttestationSigner>();

        AttestationSignature kmsSignature = await kms.SignDigestAsync(digest, CancellationToken.None);
        AttestationPublicKey kmsKey = await kms.ExportPublicKeyAsync(CancellationToken.None);
        AttestationPublicKey localKey = await local.ExportPublicKeyAsync(CancellationToken.None);

        AttestationVerification.VerifyDigest(kmsKey, digest, kmsSignature.Signature).ShouldBeTrue();
        AttestationVerification.VerifyDigest(localKey, digest, kmsSignature.Signature).ShouldBeFalse();
    }

    private WormExportVerifier BucketOnlyVerifier()
    {
        IOptions<WormExportOptions> options = Options.Create(new WormExportOptions
        {
            Bucket = AuditMaintenanceFixture.Bucket,
            ServiceUrl = fixture.AwsEndpoint,
            Region = "us-east-1",
            AccessKey = "test",
            SecretKey = "test",
            ForcePathStyle = true,
        });
        return new WormExportVerifier(
            new S3WormObjectStore(fixture.S3, options, TimeProvider.System), options);
    }

    private async Task<byte[]> ReadObjectAsync(string key)
    {
        using GetObjectResponse response = await fixture.S3.GetObjectAsync(
            AuditMaintenanceFixture.Bucket, key);
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static MonthlyPartitionWindow WindowOf(DateTimeOffset month)
        => MonthlyPartitions.Plan("audit_event", month, 0)[0];

    private static DateTimeOffset MonthOffset(int months)
    {
        DateTime utc = DateTime.UtcNow;
        return new DateTimeOffset(new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.Zero)
            .AddMonths(months);
    }
}
