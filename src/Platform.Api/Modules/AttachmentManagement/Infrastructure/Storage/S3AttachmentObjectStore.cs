using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

/// <summary>
/// Custody backed by an object store.
/// <para>
/// Removal travels on its own client parameter so a separate principal can be
/// handed to it without touching <see cref="IAttachmentObjectStore"/> or any
/// caller. Today the composition root passes the same client to both, which
/// means one principal currently needs to write, to read a named generation,
/// and to remove a named generation. Which principal holds which of those, and
/// whether removal is denied to the principal that accepts producer bytes, is
/// not decided here and is not established by anything this module can run.
/// </para>
/// </summary>
internal sealed class S3AttachmentObjectStore(
    IAmazonS3 s3,
    IAmazonS3 removals,
    string bucket) : IAttachmentObjectStore, IAttachmentObjectInventory, IDisposable
{
    /// <summary>
    /// How many generations one listing page asks for. A key holds one
    /// generation in every outcome this module produces on purpose, and more
    /// than one only where a write was amplified, so the page is sized for the
    /// pathology and the loop below is what covers the rest.
    /// </summary>
    private const int InventoryPageSize = 100;

    public async Task<AttachmentObjectCapture> PutAsync(
        AttachmentObjectRequest request,
        Stream content,
        CancellationToken cancellationToken)
    {
        var objectKey = AttachmentObjectKeys.For(request.ContentId);
        var body = new DeclaredLengthStream(content, request.ExpectedSizeBytes);
        var putRequest = new PutObjectRequest
        {
            BucketName = bucket,
            Key = objectKey,
            InputStream = body,
            ContentType = request.ContentType,
            AutoCloseStream = false,
            AutoResetStreamPosition = false,

            // The condition belongs to the write and never to the caller: a
            // single call whose answer is lost on the wire is repeated by the
            // client library, and each repetition that finds the key free
            // leaves one more durable generation behind. With the condition,
            // only the first repetition can place bytes; every later one is
            // refused by the store itself.
            IfNoneMatch = "*",
        };

        try
        {
            PutObjectResponse response = await s3.PutObjectAsync(putRequest, cancellationToken);
            Result<AttachmentObjectLocator> locator = AttachmentObjectLocator.Create(
                bucket,
                objectKey,
                response.VersionId);
            return locator.IsSuccess && locator.Value is { } captured
                ? AttachmentObjectCapture.Captured(captured)
                : AttachmentObjectCapture.Unidentified();
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            return AttachmentObjectCapture.AlreadyExists();
        }
        catch (Exception exception) when (IsStoreFailure(exception, cancellationToken))
        {
            // Asking the body whether it ran out is what separates a request
            // that sent too few bytes from a store that could not be reached.
            // Both surface as the same transport failure, and answering the
            // first one as unavailable blames the store for the caller.
            return body.SourceEndedEarly
                ? AttachmentObjectCapture.ContentShorterThanDeclared()
                : AttachmentObjectCapture.Unavailable();
        }
    }

    public async Task<AttachmentStoreOpen> OpenAsync(
        AttachmentObjectLocator locator,
        CancellationToken cancellationToken)
    {
        try
        {
            GetObjectResponse response = await s3.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = locator.Store,
                    Key = locator.Key,
                    VersionId = locator.Version,
                },
                cancellationToken);
            return AttachmentStoreOpen.Opened(response.ResponseStream, response);
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return AttachmentStoreOpen.Missing();
        }
        catch (Exception exception) when (IsStoreFailure(exception, cancellationToken))
        {
            return AttachmentStoreOpen.Unavailable();
        }
    }

    public async Task<AttachmentObjectDiscard> DiscardAsync(
        AttachmentObjectLocator locator,
        CancellationToken cancellationToken)
    {
        try
        {
            // Naming the generation is what makes this a removal. Without it
            // the store keeps the bytes and answers success, which reads as a
            // discard and is not one.
            await removals.DeleteObjectAsync(
                new DeleteObjectRequest
                {
                    BucketName = locator.Store,
                    Key = locator.Key,
                    VersionId = locator.Version,
                },
                cancellationToken);
            return AttachmentObjectDiscard.Removed;
        }
        catch (Exception exception) when (IsStoreFailure(exception, cancellationToken))
        {
            return AttachmentObjectDiscard.Unavailable;
        }
    }

    /// <summary>
    /// Enumerates the generations under one derived key.
    /// <para>
    /// The listing is by prefix and the answers are filtered back down to the
    /// exact key, because the provider's enumeration takes a prefix and a
    /// prefix that ends where a key ends still matches every longer key that
    /// starts with it. Without the equality this would hand back generations
    /// of a neighbouring attachment, and the caller removes what it is handed.
    /// </para>
    /// <para>
    /// A page the provider truncated and a page this loop could not finish
    /// reading are the same thing here: an incomplete inventory. It answers
    /// unavailable rather than short, because a short answer reads as
    /// "the store holds nothing else" to a caller that decides what to remove
    /// by what is absent from it.
    /// </para>
    /// </summary>
    public async Task<AttachmentKeyInventory> ListAsync(
        Guid contentId,
        CancellationToken cancellationToken)
    {
        var objectKey = AttachmentObjectKeys.For(contentId);
        var generations = new List<AttachmentObjectLocator>();
        string? keyMarker = null;
        string? versionMarker = null;
        try
        {
            do
            {
                ListVersionsResponse response = await s3.ListVersionsAsync(
                    new ListVersionsRequest
                    {
                        BucketName = bucket,
                        Prefix = objectKey,
                        KeyMarker = keyMarker,
                        VersionIdMarker = versionMarker,
                        MaxKeys = InventoryPageSize,
                    },
                    cancellationToken);
                foreach (S3ObjectVersion version in response.Versions ?? [])
                {
                    if (version.IsDeleteMarker == true
                        || !string.Equals(version.Key, objectKey, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    Result<AttachmentObjectLocator> locator = AttachmentObjectLocator.Create(
                        bucket,
                        version.Key,
                        version.VersionId);

                    // A generation the provider named in a way this module
                    // cannot pin is not skipped quietly. Removal is by exact
                    // generation, so an entry nobody can name is an entry
                    // nobody can remove, and reporting the rest as the whole
                    // inventory would let the caller conclude the key is clean.
                    if (locator.IsFailure || locator.Value is not { } pinned)
                    {
                        return AttachmentKeyInventory.Unavailable();
                    }

                    generations.Add(pinned);
                }

                var truncated = response.IsTruncated ?? false;
                keyMarker = truncated ? response.NextKeyMarker : null;
                versionMarker = truncated ? response.NextVersionIdMarker : null;
            }
            while (keyMarker is not null || versionMarker is not null);
        }
        catch (Exception exception) when (IsStoreFailure(exception, cancellationToken))
        {
            return AttachmentKeyInventory.Unavailable();
        }

        return AttachmentKeyInventory.Listed(generations);
    }

    public void Dispose()
    {
        s3.Dispose();
        if (!ReferenceEquals(s3, removals))
        {
            removals.Dispose();
        }
    }

    /// <summary>
    /// Every failure class this store answers for instead of letting it reach
    /// the caller as an exception.
    /// <para>
    /// The service exception and the client exception are siblings, not parent
    /// and child, so naming only the store's own service exception leaves out
    /// every other service failure the client can raise, the credential chain
    /// included. A port that accepts the connection and never answers raises
    /// neither of them and surfaces as an elapsed deadline.
    /// </para>
    /// <para>
    /// A cancellation the caller did not ask for is the store running out of
    /// time and is answered here. A cancellation the caller did ask for is the
    /// caller's own decision, and it stays unhandled on purpose so the request
    /// that asked for it hears it instead of reading an unavailable store.
    /// </para>
    /// </summary>
    private static bool IsStoreFailure(Exception exception, CancellationToken cancellationToken)
        => exception switch
        {
            OperationCanceledException => !cancellationToken.IsCancellationRequested,
            AmazonServiceException or AmazonClientException => true,
            TimeoutException or HttpRequestException or IOException => true,
            _ => false,
        };
}
