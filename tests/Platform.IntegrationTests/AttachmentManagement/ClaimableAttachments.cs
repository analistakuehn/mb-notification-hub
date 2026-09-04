using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

/// <summary>
/// One attachment as the arrangement left it, with everything a claim over it
/// is expected to answer with. The content identity is computed from the same
/// generation the release names, so an oracle can hold the snapshot against
/// what was seeded instead of against whatever the snapshot happens to say.
/// </summary>
internal sealed record SeededAttachment(
    string Reference,
    Guid Id,
    string ContentIdentity,
    string Name,
    string MediaType,
    long Length);

/// <summary>
/// Seeds attachments straight into the store the claim reads, in the states
/// the claim tells apart.
/// <para>
/// It writes the rows instead of driving the module's own endpoints because
/// the endpoints need an object store, and what a claim reads is the durable
/// record: the aggregate, the generation that was captured, and the release
/// that was granted over it. Nothing here invents a state the module cannot
/// reach; every transition goes through the aggregate's own methods.
/// </para>
/// </summary>
internal static class ClaimableAttachments
{
    private const string DefaultFileName = "comprovante.pdf";
    private const string DefaultMediaType = "application/pdf";
    private const long DefaultLength = 2048;

    /// <summary>
    /// An attachment whose release is in force: the one state a claim accepts.
    /// <para>
    /// <paramref name="grantedAt"/> is the instant the whole lifecycle is
    /// written at, the release included. A claim reads the state and the
    /// release row and never the age of either, so an old grant is claimable
    /// today and is exactly how a set that was accepted over a release past its
    /// validity is arranged.
    /// </para>
    /// </summary>
    internal static Task<SeededAttachment> ReleasedAsync(
        WebApplicationFactory<Program> host,
        string application,
        string fileName = DefaultFileName,
        string mediaType = DefaultMediaType,
        long length = DefaultLength,
        DateTimeOffset? grantedAt = null)
        => SeedAsync(
            host, application, AttachmentStates.Released, fileName, mediaType, length, grantedAt);

    /// <summary>
    /// The same, with the bytes really in custody: the object is written
    /// through the module's own store and the generation is pinned to the
    /// version that write returned, exactly as an upload pins it.
    /// <para>
    /// It exists because a send that carries an accepted set opens every
    /// member on its way to the provider. An arrangement whose generation
    /// names a coordinate nobody wrote to would make each of those sends fail
    /// on custody, and a suite about anything else would be measuring an
    /// environment with no bytes in it. The digest recorded on the generation
    /// is the digest of these bytes, so the witness of what was submitted has
    /// a truthful side to be compared against.
    /// </para>
    /// </summary>
    internal static Task<SeededAttachment> ReleasedWithContentAsync(
        WebApplicationFactory<Program> host,
        string application,
        string fileName = DefaultFileName,
        string mediaType = DefaultMediaType,
        long length = DefaultLength,
        DateTimeOffset? grantedAt = null)
        => SeedAsync(
            host, application, AttachmentStates.Released, fileName, mediaType, length, grantedAt,
            withContent: true);

    /// <summary>An attachment whose content arrived and was never approved.</summary>
    internal static Task<SeededAttachment> ReceivedAsync(
        WebApplicationFactory<Program> host,
        string application)
        => SeedAsync(
            host, application, AttachmentStates.Received,
            DefaultFileName, DefaultMediaType, DefaultLength);

    /// <summary>An attachment whose release was granted and then taken back.</summary>
    internal static Task<SeededAttachment> RevokedAsync(
        WebApplicationFactory<Program> host,
        string application)
        => SeedAsync(
            host, application, AttachmentStates.Revoked,
            DefaultFileName, DefaultMediaType, DefaultLength);

    /// <summary>Every live claim hold over one attachment, newest first.</summary>
    internal static async Task<IReadOnlyList<HeldDependency>> LiveHoldsAsync(
        WebApplicationFactory<Program> host,
        Guid attachmentId)
    {
        using IServiceScope scope = host.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        return await dbContext.AttachmentDependencies
            .AsNoTracking()
            .Where(dependency => dependency.AttachmentId == attachmentId
                && dependency.ReleasedAt == null)
            .Select(dependency => new HeldDependency(
                dependency.Holder, dependency.Reason, dependency.Version, dependency.AcquiredAt))
            .ToListAsync();
    }

    /// <summary>Every hold over one attachment, live or ended.</summary>
    internal static async Task<int> HoldCountAsync(
        WebApplicationFactory<Program> host,
        Guid attachmentId)
    {
        using IServiceScope scope = host.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .AttachmentDependencies
            .AsNoTracking()
            .CountAsync(dependency => dependency.AttachmentId == attachmentId);
    }

    /// <summary>
    /// Bytes of one attachment, derived from its own content identifier so two
    /// seeded attachments never hold the same content, and exactly as long as
    /// the length the release is granted over: the writer of the message
    /// refuses a source that delivers any other number of bytes.
    /// </summary>
    private static byte[] ContentOf(Guid contentId, long length)
    {
        var content = new byte[checked((int)length)];
        var seed = contentId.ToByteArray();
        for (var index = 0; index < content.Length; index++)
        {
            content[index] = seed[index % seed.Length];
        }

        return content;
    }

    private static async Task<SeededAttachment> SeedAsync(
        WebApplicationFactory<Program> host,
        string application,
        string state,
        string fileName,
        string mediaType,
        long length,
        DateTimeOffset? grantedAt = null,
        bool withContent = false)
    {
        ArgumentNullException.ThrowIfNull(host);
        using IServiceScope scope = host.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        DateTimeOffset now = grantedAt ?? DateTimeOffset.UtcNow;

        Attachment attachment = Attachment
            .Register(
                application,
                fileName,
                mediaType,
                length,
                AttachmentApi.SeedSizeCeiling,
                now)
            .Value
            .ShouldNotBeNull();
        attachment.MarkReceived(length, now).ShouldBe(AttachmentReceiveOutcome.Received);

        AttachmentObjectLocator locator = AttachmentObjectLocator.FromStoredRow(
            "attachment-store",
            $"attachments/{Guid.NewGuid():N}",
            $"generation-{Guid.NewGuid():N}");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(fileName + length));
        if (withContent)
        {
            var content = ContentOf(attachment.ContentId, length);
            AttachmentObjectCapture capture = await scope.ServiceProvider
                .GetRequiredService<IAttachmentObjectStore>()
                .PutAsync(
                    new AttachmentObjectRequest(attachment.ContentId, mediaType, length),
                    new MemoryStream(content),
                    CancellationToken.None);

            // Asserted rather than tolerated: a capture that did not happen
            // would leave the arrangement claiming bytes it never wrote, and
            // every send over it would fail for a reason nobody arranged.
            capture.Status.ShouldBe(
                AttachmentObjectCaptureStatus.Captured,
                "o arranjo precisa dos bytes realmente na custódia; sem eles todo envio que "
                + "carrega o conjunto falha ao abrir o conteúdo.");
            locator = capture.Locator.ShouldNotBeNull();
            digest = SHA256.HashData(content);
        }

        AttachmentObjectGeneration generation = AttachmentObjectGeneration.Capture(
            attachment.Id,
            locator,
            AttachmentContentProof.Sha256Of(digest, length),
            mediaType,
            now);
        dbContext.Attachments.Add(attachment);
        dbContext.ObjectGenerations.Add(generation);

        if (state is AttachmentStates.Released or AttachmentStates.Revoked)
        {
            attachment.Release().ShouldBe(AttachmentValidationTransition.Applied);
            dbContext.Releases.Add(AttachmentRelease.Grant(
                attachment.Id, generation.Id, now, TimeSpan.FromDays(30)));
        }

        if (state == AttachmentStates.Revoked)
        {
            attachment.Revoke(now).ShouldBe(AttachmentRevocationTransition.Applied);
        }

        attachment.State.ShouldBe(state);
        await dbContext.SaveChangesAsync();
        return new SeededAttachment(
            attachment.Reference.Value,
            attachment.Id,
            AttachmentContentIdentity.For(generation),
            fileName,
            mediaType,
            length);
    }
}

/// <summary>One hold as the durable record carries it.</summary>
internal sealed record HeldDependency(
    string Holder,
    string Reason,
    long Version,
    DateTimeOffset AcquiredAt);
