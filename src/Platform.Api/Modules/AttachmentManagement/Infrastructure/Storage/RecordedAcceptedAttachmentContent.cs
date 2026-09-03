using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

/// <summary>
/// Hands over the content behind an accepted attachment, resolving the opaque
/// handle against this module's own record of the generation it names.
/// <para>
/// The handle is the whole input, and it is resolved here so that the
/// coordinate never crosses. What the caller gets is a reading of exactly the
/// generation the handle names, never of whatever the key points at now: a
/// handle that no longer resolves, a row that is gone and a custody that
/// cannot be reached all leave through the one answer that yields nothing,
/// because none of them yields bytes and a caller with no bytes does the same
/// thing in all three cases.
/// </para>
/// <para>
/// It reads no state of the attachment and no release. Whether the set may
/// still leave was settled by the release check immediately before the call
/// this is part of, and asking it again here would be a second authority over
/// eligibility, answering later than the one that owns the question. What this
/// does own is the identity of the bytes: the generation is pinned by the
/// handle, so a member released over other content than the accepted one is
/// refused by that check rather than silently opened here.
/// </para>
/// <para>
/// The context is created for the reading and disposed with it, because this
/// serves a caller that may live as long as the process: it is composed beside
/// a provider adapter, which is a singleton, and taking a scoped context would
/// pin the first scope that ever resolved it.
/// </para>
/// </summary>
internal sealed class RecordedAcceptedAttachmentContent(
    IDbContextFactory<AttachmentManagementDbContext> contextFactory,
    IAttachmentObjectStore store,
    ILogger<RecordedAcceptedAttachmentContent> logger) : IAcceptedAttachmentContent
{
    public async Task<AcceptedAttachmentContent> OpenAsync(
        string contentIdentity,
        CancellationToken cancellationToken)
    {
        if (AttachmentContentIdentity.GenerationOf(contentIdentity) is not { } generationId)
        {
            // Text this module never minted names no generation, whatever the
            // reason, and the value itself never reaches the line: a handle is
            // producer-adjacent data and an operational line is where it must
            // not start appearing.
            logger.AcceptedContentHandleNotMinted();
            return AcceptedAttachmentContent.Unavailable();
        }

        AttachmentObjectLocator locator;
        try
        {
            await using AttachmentManagementDbContext dbContext =
                await contextFactory.CreateDbContextAsync(cancellationToken);
            StoredGeneration? generation = await dbContext.ObjectGenerations
                .AsNoTracking()
                .Where(row => row.Id == generationId)
                .Select(row => new StoredGeneration(row.Store, row.Key, row.Version))
                .FirstOrDefaultAsync(cancellationToken);
            if (generation is null)
            {
                logger.AcceptedContentGenerationGone(generationId);
                return AcceptedAttachmentContent.Unavailable();
            }

            locator = AttachmentObjectLocator.FromStoredRow(
                generation.Store, generation.Key, generation.Version);
        }
        catch (DbException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The record could not be read, so nothing about the content could
            // be established. Answering that, instead of letting the failure
            // travel, keeps the decision with the caller: it is the one that
            // knows whether it has already started something it cannot take
            // back.
            logger.AcceptedContentRecordUnavailable(exception);
            return AcceptedAttachmentContent.Unavailable();
        }

        AttachmentStoreOpen reading = await store.OpenAsync(locator, cancellationToken);
        if (reading is { Status: AttachmentStoreOpenStatus.Opened, Content: { } content })
        {
            return AcceptedAttachmentContent.Opened(content, reading);
        }

        reading.Dispose();
        logger.AcceptedContentCustodyRefused(generationId, reading.Status.ToString());
        return AcceptedAttachmentContent.Unavailable();
    }

    /// <summary>
    /// The coordinate of one generation, projected instead of materialized:
    /// the row carries the digest and the algorithm too, and neither of them
    /// has any business being in memory on the path that reads the bytes out.
    /// </summary>
    private sealed record StoredGeneration(string Store, string Key, string Version);
}
