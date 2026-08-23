using System.Text.Json;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;

/// <summary>The two stored forms of a variables payload.</summary>
internal sealed record ProtectedVariables
{
    /// <summary>Canonical JSON with every sensitive value masked; always present.</summary>
    public required string MaskedJson { get; init; }

    /// <summary>Envelope-encrypted canonical JSON of the full object; null when the request carried none.</summary>
    public byte[]? Encrypted { get; init; }
}

/// <summary>
/// Produces the two persisted forms of the variables object: the masked
/// projection for queries and audit, and the envelope-encrypted full object
/// for the render stage. The whole object is encrypted, not only the
/// sensitive variables, so the ciphertext never leaks which variables were
/// sensitive; the data key is scoped per application by the envelope cipher.
/// </summary>
internal sealed class VariablesProtector(IEnvelopeCipher cipher)
{
    public async Task<ProtectedVariables> ProtectAsync(
        string application,
        JsonElement? variables,
        IReadOnlyList<string> sensitiveVariables,
        CancellationToken cancellationToken)
    {
        var masked = VariablesMask.MaskedProjection(variables, sensitiveVariables);
        if (variables is not { ValueKind: JsonValueKind.Object } payload)
        {
            return new ProtectedVariables { MaskedJson = masked };
        }

        var encrypted = await cipher.EncryptAsync(
            application,
            CanonicalJson.CanonicalBytes(payload),
            cancellationToken);
        return new ProtectedVariables { MaskedJson = masked, Encrypted = encrypted };
    }
}
