using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace NotificationHub.UnitTests.AttachmentManagement;

public sealed class S3AttachmentObjectStoreTests
{
    private const string Bucket = "custody-store";

    private static readonly AttachmentObjectLocator Locator =
        AttachmentObjectLocator.FromStoredRow(Bucket, "attachments/6b4c1f7a", "AbCdEf0123456789");

    [Theory]
    [InlineData("deadline")]
    [InlineData("service")]
    [InlineData("client")]
    [InlineData("transport")]
    [InlineData("reading")]
    [InlineData("foreign-cancellation")]
    public async Task A_write_that_fails_on_the_client_answers_without_an_identity(string failure)
    {
        IAmazonS3 s3 = Substitute.For<IAmazonS3>();
        s3.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Failure(failure));
        using var store = new S3AttachmentObjectStore(s3, s3, Bucket);
        using var content = new MemoryStream("payload"u8.ToArray(), writable: false);

        AttachmentObjectCapture capture = await store.PutAsync(
            new AttachmentObjectRequest(Guid.NewGuid(), "application/pdf", 7),
            content,
            CancellationToken.None);

        capture.Status.ShouldBe(AttachmentObjectCaptureStatus.Unavailable);
        capture.Locator.ShouldBeNull();
    }

    [Theory]
    [InlineData("deadline")]
    [InlineData("service")]
    [InlineData("client")]
    [InlineData("transport")]
    [InlineData("reading")]
    [InlineData("foreign-cancellation")]
    public async Task A_reading_that_fails_on_the_client_answers_unavailable(string failure)
    {
        IAmazonS3 s3 = Substitute.For<IAmazonS3>();
        s3.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Failure(failure));
        using var store = new S3AttachmentObjectStore(s3, s3, Bucket);

        using AttachmentStoreOpen reading = await store.OpenAsync(Locator, CancellationToken.None);

        reading.Status.ShouldBe(AttachmentStoreOpenStatus.Unavailable);
        reading.Content.ShouldBeNull();
    }

    [Theory]
    [InlineData("deadline")]
    [InlineData("service")]
    [InlineData("client")]
    [InlineData("transport")]
    [InlineData("reading")]
    [InlineData("foreign-cancellation")]
    public async Task A_removal_that_fails_on_the_client_is_not_answered_as_removed(string failure)
    {
        IAmazonS3 s3 = Substitute.For<IAmazonS3>();
        s3.DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Failure(failure));
        using var store = new S3AttachmentObjectStore(s3, s3, Bucket);

        AttachmentObjectDiscard discard = await store.DiscardAsync(
            Locator,
            CancellationToken.None);

        discard.ShouldBe(AttachmentObjectDiscard.Unavailable);
    }

    [Fact]
    public async Task The_cancellation_the_caller_asked_for_reaches_the_caller_on_every_operation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        IAmazonS3 s3 = Substitute.For<IAmazonS3>();
        s3.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        s3.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        s3.DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        using var store = new S3AttachmentObjectStore(s3, s3, Bucket);
        using var content = new MemoryStream("payload"u8.ToArray(), writable: false);

        await Should.ThrowAsync<OperationCanceledException>(async () => await store.PutAsync(
            new AttachmentObjectRequest(Guid.NewGuid(), "application/pdf", 7),
            content,
            cancellation.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await store.OpenAsync(Locator, cancellation.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await store.DiscardAsync(Locator, cancellation.Token));
    }

    [Fact]
    public async Task A_port_that_accepts_and_never_answers_is_unavailable_on_every_operation()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = new List<TcpClient>();
        Task accepting = AcceptAndHoldAsync(listener, accepted);
        try
        {
            using var store = new S3AttachmentObjectStore(
                SilentEndpointClient(port),
                SilentEndpointClient(port),
                Bucket);
            using var content = new MemoryStream("payload"u8.ToArray(), writable: false);

            AttachmentObjectCapture capture = await store.PutAsync(
                new AttachmentObjectRequest(Guid.NewGuid(), "application/pdf", 7),
                content,
                CancellationToken.None);
            using AttachmentStoreOpen reading = await store.OpenAsync(
                Locator,
                CancellationToken.None);
            AttachmentObjectDiscard discard = await store.DiscardAsync(
                Locator,
                CancellationToken.None);

            capture.Status.ShouldBe(AttachmentObjectCaptureStatus.Unavailable);
            reading.Status.ShouldBe(AttachmentStoreOpenStatus.Unavailable);
            discard.ShouldBe(AttachmentObjectDiscard.Unavailable);
        }
        finally
        {
            listener.Stop();
            await accepting;
            foreach (TcpClient client in accepted)
            {
                client.Dispose();
            }
        }
    }

    private static async Task AcceptAndHoldAsync(TcpListener listener, List<TcpClient> accepted)
    {
        try
        {
            while (true)
            {
                accepted.Add(await listener.AcceptTcpClientAsync());
            }
        }
        catch (ObjectDisposedException)
        {
            // The listener closing is how this loop ends.
        }
        catch (SocketException)
        {
            // The listener closing is how this loop ends.
        }
    }

    private static AmazonS3Client SilentEndpointClient(int port)
        => new(
            new BasicAWSCredentials("unit-test-access", "unit-test-secret"),
            new AmazonS3Config
            {
                ServiceURL = $"http://127.0.0.1:{port}",
                AuthenticationRegion = "us-east-1",
                ForcePathStyle = true,
                // Short on purpose. The endpoint never answers whatever the
                // deadline is, and a worker thread parked for seconds inside a
                // suite of this size moves the timing bands other modules
                // measure.
                Timeout = TimeSpan.FromMilliseconds(400),
                ConnectTimeout = TimeSpan.FromMilliseconds(400),
                MaxErrorRetry = 0,
            });

    private static Exception Failure(string failure)
        => failure switch
        {
            "deadline" => new TimeoutException("The store did not answer in time."),

            // The store's own service exception is a sibling of the client
            // exception, so every other service failure the client raises,
            // the credential chain included, arrives as this one.
            "service" => new AmazonServiceException("A service the client called refused."),
            "client" => new AmazonClientException("The client could not complete the call."),
            "transport" => new HttpRequestException("The connection was refused."),
            "reading" => new IOException("The connection dropped mid-call."),

            // A cancellation nobody asked for is the store running out of
            // time under another name.
            "foreign-cancellation" => new OperationCanceledException(
                "The call was cancelled without the caller asking."),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
        };
}
