using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Audit.Domain;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Worm;

/// <summary>
/// Object-store implementation of the evidence sink. Every write declares
/// Compliance retention until a date the configured term fixes: from that
/// moment no principal, root included, can shorten it, which is precisely the
/// property that makes an export usable as evidence. The digest of the plain
/// content travels as object metadata so a rerun can decide, with one head
/// request, whether the bytes it would write are already there.
/// </summary>
internal sealed class S3WormObjectStore(
    IAmazonS3 s3,
    IOptions<WormExportOptions> options,
    TimeProvider timeProvider) : IWormObjectStore
{
    internal const string DigestMetadataKey = "sha256";

    private const string DigestMetadataHeader = "x-amz-meta-" + DigestMetadataKey;

    public async Task<WormObjectHead?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            GetObjectMetadataResponse response = await s3.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = options.Value.Bucket, Key = key },
                cancellationToken);
            return new WormObjectHead(
                key,
                response.Metadata[DigestMetadataHeader],
                response.ContentLength);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using GetObjectResponse response = await s3.GetObjectAsync(
                new GetObjectRequest { BucketName = options.Value.Bucket, Key = key },
                cancellationToken);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task PutAsync(
        string key,
        byte[] content,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        WormExportOptions value = options.Value;
        using var body = new MemoryStream(content, writable: false);
        var request = new PutObjectRequest
        {
            BucketName = value.Bucket,
            Key = key,
            InputStream = body,
            ContentType = contentType,
            ObjectLockMode = ObjectLockMode.Compliance,
            ObjectLockRetainUntilDate = timeProvider.GetUtcNow().UtcDateTime.AddYears(value.RetentionYears),
        };
        request.Metadata.Add(DigestMetadataKey, AuditDigest.Hex(content));
        await s3.PutObjectAsync(request, cancellationToken);
    }
}
