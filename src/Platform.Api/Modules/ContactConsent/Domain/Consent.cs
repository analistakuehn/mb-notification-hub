namespace NotificationHub.Api.Modules.ContactConsent.Domain;

/// <summary>Canonical origins of a consent record.</summary>
public static class ConsentSources
{
    public const string App = "app";
    public const string CustomerService = "atendimento";
    public const string Import = "importacao";

    public static IReadOnlyList<string> CanonicalValues { get; } = [App, CustomerService, Import];

    public static bool IsCanonical(string? value)
        => value is App or CustomerService or Import;
}

/// <summary>
/// One entry of the append-only consent ledger: a grant or a revocation for a
/// purpose, anchored on the exact contact point it was declared for, with the
/// origin, the acting principal, the terms version, and the instant. The type
/// has no mutators by construction and the table rejects UPDATE and DELETE:
/// the current state of a (purpose, channel) pair is always the latest record,
/// never an edited one.
/// </summary>
public sealed class Consent
{
    private Consent()
    {
        Purpose = null!;
        Source = null!;
        ActorId = null!;
        TermsVersion = null!;
    }

    public Guid Id { get; private set; }

    public Guid ContactPointId { get; private set; }

    public string Purpose { get; private set; }

    public bool Granted { get; private set; }

    public string Source { get; private set; }

    /// <summary>Stable identity of the principal that declared the state.</summary>
    public string ActorId { get; private set; }

    public string TermsVersion { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }

    public static Consent Record(
        Guid contactPointId,
        string purpose,
        bool granted,
        string source,
        string actorId,
        string termsVersion,
        DateTimeOffset recordedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(termsVersion);
        if (!ConsentSources.IsCanonical(source))
        {
            throw new ArgumentException($"Origem de consentimento desconhecida: '{source}'.", nameof(source));
        }

        return new Consent
        {
            Id = Guid.CreateVersion7(),
            ContactPointId = contactPointId,
            Purpose = purpose,
            Granted = granted,
            Source = source,
            ActorId = actorId,
            TermsVersion = termsVersion,
            RecordedAt = recordedAt,
        };
    }
}
