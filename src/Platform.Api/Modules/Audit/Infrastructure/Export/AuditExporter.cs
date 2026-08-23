using System.Text;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.Verification;
using NotificationHub.Api.Modules.Audit.Infrastructure.Worm;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Export;

/// <summary>
/// Raised when an export cannot proceed without either destroying evidence or
/// publishing evidence that does not hold. Both are conditions an operator
/// must see, never conditions a job may work around.
/// </summary>
internal sealed class AuditExportException : Exception
{
    public AuditExportException(string message)
        : base(message)
    {
    }

    public AuditExportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>What to export: one contiguous sequence range of one partition.</summary>
internal sealed record AuditExportRequest(
    MonthlyPartitionWindow Window,
    string Type,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    string Folder,
    long AfterSeq,
    long ThroughSeq,
    byte[] HeadPrevHash,
    AuditExportManifestLink? Previous);

/// <summary>Result of one export: the manifest that now stands, and whether this round wrote it.</summary>
internal sealed record AuditExportResult(
    string ManifestKey,
    AuditExportManifest Manifest,
    bool AlreadyPresent);

/// <summary>
/// Writes one export: the chain segment as newline-delimited canonical events,
/// the pre-chain rows apart, a manifest that lets the whole thing be checked
/// without the database, and a signature over that manifest.
/// </summary>
/// <remarks>
/// The events file carries the stored canonical text of each row byte for
/// byte. Nothing is reparsed, reordered, or re-serialized on the way out,
/// because the hash covers those exact bytes: repackaging the JSON would
/// preserve the meaning, change the bytes, and quietly destroy the ability to
/// prove anything.
/// </remarks>
internal sealed class AuditExporter(
    AuditTrailReader reader,
    IWormObjectStore store,
    IAttestationSigner signer,
    AuditMaintenanceJournal journal,
    IOptions<WormExportOptions> options,
    ILogger<AuditExporter> logger)
{
    private const string NdjsonContentType = "application/gzip";
    private const string JsonContentType = "application/json";
    private const int MaxRowsPerExport = 500_000;

    public async Task<AuditExportResult> ExportAsync(
        AuditExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<AuditTrailRow> rows = await reader.ReadRowsAsync(
            request.Window, request.AfterSeq, request.ThroughSeq, MaxRowsPerExport, cancellationToken);

        AuditTrailRow[] chained = [.. rows.Where(row => !row.IsUnchained)];
        AuditTrailRow[] unchained = [.. rows.Where(row => row.IsUnchained)];

        AuditChainSegmentResult segment = AuditChainSegment.Verify(
            request.HeadPrevHash,
            [.. chained.Select(row => new AuditChainRow(row.Seq, row.Canonical!, row.PrevHash, row.Hash))]);
        if (!segment.IsIntact)
        {
            throw new AuditExportException(
                $"A cadeia da partição {request.Window.PartitionName} não fecha no seq {segment.BrokenSeq} "
                + $"({segment.Reason}); nada foi exportado.");
        }

        var events = NewlineDelimited(chained.Select(row => row.Canonical!));
        var compressedEvents = AuditGzip.Compress(events);
        var compressedDigest = AuditDigest.Hex(compressedEvents);

        var eventsKey = request.Folder + AuditExportKeys.EventsObject;
        var manifestKey = request.Folder + AuditExportKeys.ManifestObject;
        var attestationKey = request.Folder + AuditExportKeys.AttestationObject;

        WormObjectHead? existing = await store.HeadAsync(eventsKey, cancellationToken);
        if (existing is not null)
        {
            AuditExportResult? settled = await ReuseExistingAsync(
                manifestKey, attestationKey, existing, compressedDigest, cancellationToken);
            if (settled is not null)
            {
                return settled;
            }
        }

        var compressedUnchained = unchained.Length == 0
            ? null
            : AuditGzip.Compress(NewlineDelimited(unchained.Select(row => row.CanonicalizeForExport())));

        var manifest = new AuditExportManifest
        {
            FormatVersion = AuditExportManifest.CurrentFormatVersion,
            Table = TableOf(request.Window),
            Partition = request.Window.PartitionName,
            Type = request.Type,
            WindowFrom = request.WindowFrom,
            WindowTo = request.WindowTo,
            SeqMin = segment.SeqMin,
            SeqMax = segment.SeqMax,
            ChainedCount = chained.Length,
            UnchainedCount = unchained.Length,
            Anchor = AuditHex.ToHex(AuditChain.PartitionAnchor(request.Window.PartitionName)),
            HeadPrevHash = AuditHex.ToHex(request.HeadPrevHash),
            TailHash = AuditHex.ToHex(segment.TailHash),
            UncompressedDigest = AuditDigest.Hex(events),
            CompressedDigest = compressedDigest,
            UnchainedDigest = compressedUnchained is null ? null : AuditDigest.Hex(compressedUnchained),
            Previous = request.Previous,
        };

        await ArchivePublicKeyAsync(cancellationToken);
        await store.PutAsync(eventsKey, compressedEvents, NdjsonContentType, cancellationToken);
        if (compressedUnchained is not null)
        {
            await store.PutAsync(
                request.Folder + AuditExportKeys.UnchainedObject,
                compressedUnchained,
                NdjsonContentType,
                cancellationToken);
        }

        var manifestBytes = manifest.CanonicalBytes();
        await store.PutAsync(manifestKey, manifestBytes, JsonContentType, cancellationToken);
        await AttestAsync(attestationKey, manifestBytes, cancellationToken);

        logger.ExportWritten(
            request.Type, request.Window.PartitionName, chained.Length, unchained.Length, manifestKey);

        // The export is a governed effect of this module, so it lands in the
        // trail like any other: an auditor sees what was exported and where,
        // without depending on an operational log.
        await journal.RecordAsync(
            AuditActions.AuditExported,
            request.Window.PartitionName,
            [
                ("exportType", request.Type),
                ("manifestKey", manifestKey),
                ("chainedCount", chained.Length),
                ("unchainedCount", unchained.Length),
                ("seqMin", manifest.SeqMin),
                ("seqMax", manifest.SeqMax),
                ("tailHash", manifest.TailHash),
            ],
            cancellationToken);
        return new AuditExportResult(manifestKey, manifest, AlreadyPresent: false);
    }

    /// <summary>
    /// Decides what a rerun does with an events object that is already there.
    /// Same digest with a manifest present means the export stands and this
    /// round only makes sure the signature followed. A different digest under
    /// a completed export means the evidence no longer matches the trail,
    /// which is a finding, not a retry.
    /// </summary>
    private async Task<AuditExportResult?> ReuseExistingAsync(
        string manifestKey,
        string attestationKey,
        WormObjectHead existing,
        string compressedDigest,
        CancellationToken cancellationToken)
    {
        var storedManifest = await store.GetAsync(manifestKey, cancellationToken);
        if (storedManifest is null)
        {
            // An interrupted round left data without its manifest: rewriting
            // the pair is the recovery, and the orphan version stays behind.
            return null;
        }

        if (!string.Equals(existing.Sha256Hex, compressedDigest, StringComparison.Ordinal))
        {
            throw new AuditExportException(
                $"O objeto '{existing.Key}' já existe com digest diferente do recalculado; "
                + "a evidência exportada não corresponde mais à trilha.");
        }

        AuditExportManifest manifest = AuditExportManifest.Parse(storedManifest);
        if (await store.HeadAsync(attestationKey, cancellationToken) is null)
        {
            await AttestAsync(attestationKey, storedManifest, cancellationToken);
        }

        return new AuditExportResult(manifestKey, manifest, AlreadyPresent: true);
    }

    private async Task AttestAsync(string attestationKey, byte[] manifestBytes, CancellationToken cancellationToken)
    {
        var digest = AuditDigest.Compute(manifestBytes);
        AttestationSignature signature = await signer.SignDigestAsync(digest, cancellationToken);
        var attestation = new AuditAttestationDocument(
            signature.Algorithm, signature.KeyId, AuditHex.ToHex(digest), signature.Signature);
        await store.PutAsync(
            attestationKey, attestation.CanonicalBytes(), JsonContentType, cancellationToken);
    }

    /// <summary>
    /// Archives the public half of the signing key next to the evidence, once.
    /// Verification decades from now must not depend on the key provider still
    /// resolving the key, or on this platform existing at all.
    /// </summary>
    private async Task ArchivePublicKeyAsync(CancellationToken cancellationToken)
    {
        AttestationPublicKey publicKey = await signer.ExportPublicKeyAsync(cancellationToken);
        var key = AuditExportKeys.PublicKeyObject(options.Value.KeyPrefix, publicKey.KeyId);
        if (await store.HeadAsync(key, cancellationToken) is not null)
        {
            return;
        }

        var document = new AuditAttestationKeyDocument(
            publicKey.Algorithm, publicKey.KeyId, publicKey.SubjectPublicKeyInfo);
        await store.PutAsync(key, document.CanonicalBytes(), JsonContentType, cancellationToken);
        logger.PublicKeyArchived(publicKey.KeyId, key);
    }

    /// <summary>
    /// The exported stream: one canonical document per line, in chain order,
    /// with a closing newline. Every line is the stored text, untouched.
    /// </summary>
    private static byte[] NewlineDelimited(IEnumerable<string> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(line).Append('\n');
        }

        return AuditHex.Utf8(builder.ToString());
    }

    private static string TableOf(MonthlyPartitionWindow window)
    {
        // Partition names are {table}_{yyyy}_{MM}: drop the two trailing parts.
        var parts = window.PartitionName.Split('_');
        return string.Join('_', parts[..^2]);
    }
}
