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

    /// <summary>An attachment whose release is in force: the one state a claim accepts.</summary>
    internal static Task<SeededAttachment> ReleasedAsync(
        WebApplicationFactory<Program> host,
        string application,
        string fileName = DefaultFileName,
        string mediaType = DefaultMediaType,
        long length = DefaultLength)
        => SeedAsync(host, application, AttachmentStates.Released, fileName, mediaType, length);

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

    private static async Task<SeededAttachment> SeedAsync(
        WebApplicationFactory<Program> host,
        string application,
        string state,
        string fileName,
        string mediaType,
        long length)
    {
        ArgumentNullException.ThrowIfNull(host);
        using IServiceScope scope = host.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        DateTimeOffset now = DateTimeOffset.UtcNow;

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

        AttachmentObjectGeneration generation = AttachmentObjectGeneration.Capture(
            attachment.Id,
            AttachmentObjectLocator.FromStoredRow(
                "attachment-store",
                $"attachments/{Guid.NewGuid():N}",
                $"generation-{Guid.NewGuid():N}"),
            AttachmentContentProof.Sha256Of(
                SHA256.HashData(Encoding.UTF8.GetBytes(fileName + length)),
                length),
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
            attachment.Revoke().ShouldBe(AttachmentRevocationTransition.Applied);
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
