using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.Worm;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Export;

/// <summary>Verdict over one exported artifact, with the first reason it failed.</summary>
internal sealed record WormVerificationResult(bool IsValid, string? Failure, AuditExportManifest? Manifest)
{
    internal static WormVerificationResult Invalid(string failure) => new(false, failure, null);
}

/// <summary>Verdict over a walk backwards through the manifest chain.</summary>
internal sealed record WormChainWalkResult(bool IsValid, string? Failure, int VisitedCount, string? BrokenKey);

/// <summary>
/// Verifies exported evidence using nothing but the bucket. It never touches
/// the database, and that is the whole point: it is the check an auditor can
/// run years from now, on an archived copy, with no access to this system.
/// The platform runs exactly the same code before it destroys anything.
/// </summary>
internal sealed class WormExportVerifier(IWormObjectStore store, IOptions<WormExportOptions> options)
{
    private const int DefaultWalkLimit = 400;

    /// <summary>
    /// Checks one export end to end: signature over the manifest, digests of
    /// the stored objects, and the chain replayed from the head the manifest
    /// declares to the tail it claims.
    /// </summary>
    public async Task<WormVerificationResult> VerifyAsync(string manifestKey, CancellationToken cancellationToken)
    {
        var manifestBytes = await store.GetAsync(manifestKey, cancellationToken);
        if (manifestBytes is null)
        {
            return WormVerificationResult.Invalid("manifest-missing");
        }

        AuditExportManifest manifest;
        try
        {
            manifest = AuditExportManifest.Parse(manifestBytes);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or KeyNotFoundException)
        {
            return WormVerificationResult.Invalid("manifest-unreadable");
        }

        var folder = manifestKey[..(manifestKey.Length - AuditExportKeys.ManifestObject.Length)];
        var attestation = await VerifyAttestationAsync(folder, manifestBytes, cancellationToken);
        if (attestation is not null)
        {
            return WormVerificationResult.Invalid(attestation);
        }

        var events = await store.GetAsync(folder + AuditExportKeys.EventsObject, cancellationToken);
        if (events is null)
        {
            return WormVerificationResult.Invalid("events-missing");
        }

        if (!string.Equals(AuditDigest.Hex(events), manifest.CompressedDigest, StringComparison.Ordinal))
        {
            return WormVerificationResult.Invalid("compressed-digest-mismatch");
        }

        byte[] plain;
        try
        {
            plain = AuditGzip.Decompress(events);
        }
        catch (InvalidDataException)
        {
            return WormVerificationResult.Invalid("events-unreadable");
        }

        if (!string.Equals(AuditDigest.Hex(plain), manifest.UncompressedDigest, StringComparison.Ordinal))
        {
            return WormVerificationResult.Invalid("uncompressed-digest-mismatch");
        }

        var chainFailure = VerifyChain(manifest, plain);
        if (chainFailure is not null)
        {
            return WormVerificationResult.Invalid(chainFailure);
        }

        var unchainedFailure = await VerifyUnchainedAsync(folder, manifest, cancellationToken);
        return unchainedFailure is null
            ? new WormVerificationResult(true, null, manifest)
            : WormVerificationResult.Invalid(unchainedFailure);
    }

    /// <summary>
    /// Follows the backward references from one manifest as far as they go. A
    /// removed export stops being an absence nobody can see: the reference
    /// that named it no longer resolves, and the walk reports where.
    /// </summary>
    public async Task<WormChainWalkResult> WalkAsync(
        string manifestKey,
        CancellationToken cancellationToken,
        int maxLinks = DefaultWalkLimit)
    {
        var visited = 0;
        var currentKey = manifestKey;
        while (visited < maxLinks)
        {
            var content = await store.GetAsync(currentKey, cancellationToken);
            if (content is null)
            {
                return new WormChainWalkResult(false, "manifest-missing", visited, currentKey);
            }

            AuditExportManifest manifest = AuditExportManifest.Parse(content);
            visited++;
            if (manifest.Previous is null)
            {
                return new WormChainWalkResult(true, null, visited, null);
            }

            var previous = await store.GetAsync(manifest.Previous.Key, cancellationToken);
            if (previous is null)
            {
                return new WormChainWalkResult(
                    false, "previous-manifest-missing", visited, manifest.Previous.Key);
            }

            AuditExportManifest previousManifest = AuditExportManifest.Parse(previous);
            if (!string.Equals(previousManifest.TailHash, manifest.Previous.TailHash, StringComparison.Ordinal))
            {
                return new WormChainWalkResult(
                    false, "previous-tail-mismatch", visited, manifest.Previous.Key);
            }

            currentKey = manifest.Previous.Key;
        }

        return new WormChainWalkResult(false, "walk-limit-reached", visited, currentKey);
    }

    private async Task<string?> VerifyAttestationAsync(
        string folder,
        byte[] manifestBytes,
        CancellationToken cancellationToken)
    {
        var attestationBytes = await store.GetAsync(
            folder + AuditExportKeys.AttestationObject, cancellationToken);
        if (attestationBytes is null)
        {
            return "attestation-missing";
        }

        AuditAttestationDocument attestation = AuditAttestationDocument.Parse(attestationBytes);
        var digest = AuditDigest.Compute(manifestBytes);
        if (!string.Equals(AuditHex.ToHex(digest), attestation.ManifestDigest, StringComparison.Ordinal))
        {
            return "manifest-digest-mismatch";
        }

        var keyBytes = await store.GetAsync(
            AuditExportKeys.PublicKeyObject(options.Value.KeyPrefix, attestation.KeyId), cancellationToken);
        if (keyBytes is null)
        {
            return "public-key-missing";
        }

        AuditAttestationKeyDocument archived = AuditAttestationKeyDocument.Parse(keyBytes);
        return AttestationVerification.VerifyDigest(archived.ToPublicKey(), digest, attestation.Signature)
            ? null
            : "signature-invalid";
    }

    private async Task<string?> VerifyUnchainedAsync(
        string folder,
        AuditExportManifest manifest,
        CancellationToken cancellationToken)
    {
        if (manifest.UnchainedCount == 0)
        {
            return manifest.UnchainedDigest is null ? null : "unchained-digest-without-rows";
        }

        var stored = await store.GetAsync(folder + AuditExportKeys.UnchainedObject, cancellationToken);
        if (stored is null)
        {
            return "unchained-missing";
        }

        if (!string.Equals(AuditDigest.Hex(stored), manifest.UnchainedDigest, StringComparison.Ordinal))
        {
            return "unchained-digest-mismatch";
        }

        return Lines(AuditGzip.Decompress(stored)).Count == manifest.UnchainedCount
            ? null
            : "unchained-count-mismatch";
    }

    /// <summary>
    /// Replays <c>hash = SHA-256(prev_hash ‖ canonical)</c> over the exported
    /// lines. No hash travels per line on purpose: the manifest anchors the
    /// head and the tail, and everything between is derived, so a single
    /// altered byte anywhere in the file moves the tail.
    /// </summary>
    private static string? VerifyChain(AuditExportManifest manifest, byte[] plain)
    {
        var expectedAnchor = AuditHex.ToHex(AuditChain.PartitionAnchor(manifest.Partition));
        if (!string.Equals(expectedAnchor, manifest.Anchor, StringComparison.Ordinal))
        {
            return "anchor-mismatch";
        }

        var startsAtAnchor = manifest.Previous is null
            || !string.Equals(manifest.Previous.Partition, manifest.Partition, StringComparison.Ordinal)
            || string.Equals(manifest.Type, AuditExportManifest.ClosingType, StringComparison.Ordinal);
        if (startsAtAnchor)
        {
            if (!string.Equals(manifest.HeadPrevHash, manifest.Anchor, StringComparison.Ordinal))
            {
                return "head-not-anchored";
            }
        }
        else if (!string.Equals(manifest.HeadPrevHash, manifest.Previous!.TailHash, StringComparison.Ordinal))
        {
            return "head-does-not-continue-previous";
        }

        List<string> lines = Lines(plain);
        if (lines.Count != manifest.ChainedCount)
        {
            return "chained-count-mismatch";
        }

        var rows = new List<AuditChainRow>(lines.Count);
        foreach (var line in lines)
        {
            long seq;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                seq = document.RootElement.GetProperty("seq").GetInt64();
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException)
            {
                return "event-unreadable";
            }

            rows.Add(new AuditChainRow(seq, line, null, null));
        }

        AuditChainSegmentResult segment = AuditChainSegment.Verify(AuditHex.FromHex(manifest.HeadPrevHash), rows);
        if (!segment.IsIntact)
        {
            return "chain-broken";
        }

        if (!string.Equals(AuditHex.ToHex(segment.TailHash), manifest.TailHash, StringComparison.Ordinal))
        {
            return "tail-mismatch";
        }

        return segment.SeqMin == manifest.SeqMin && segment.SeqMax == manifest.SeqMax
            ? null
            : "seq-range-mismatch";
    }

    private static List<string> Lines(byte[] plain)
        => [.. Encoding.UTF8.GetString(plain).Split('\n', StringSplitOptions.RemoveEmptyEntries)];
}
