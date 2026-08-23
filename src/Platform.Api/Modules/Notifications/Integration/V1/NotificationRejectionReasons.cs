namespace NotificationHub.Api.Modules.Notifications.Integration.V1;

/// <summary>
/// The canonical rejection-reason catalog of the notification lifecycle,
/// versioned with the outgoing event schema. Every reason a producer can
/// receive, on the REST problem type or on the <c>reason</c> of a rejection or
/// failure event, is a member of this set: one vocabulary, whatever the
/// transport that carried the request.
///
/// Collapsing a specific reason into a generic one destroys the producer's
/// diagnosis, so a new decision point adds a member here instead of reusing
/// <see cref="PayloadInvalid"/>.
/// </summary>
public static class NotificationRejectionReasons
{
    /// <summary>The template no longer accepts new requests.</summary>
    public const string TemplateDeprecated = "template-deprecated";

    /// <summary>The template is disabled.</summary>
    public const string TemplateDisabled = "template-disabled";

    /// <summary>No published template answers the requested key for the application.</summary>
    public const string TemplateNotFound = "template-not-found";

    /// <summary>The template belongs to a class other than the requested one.</summary>
    public const string TemplateClassMismatch = "template-class-mismatch";

    /// <summary>The variables payload fails the published schema of the template.</summary>
    public const string TemplateVariablesInvalid = "template-variables-invalid";

    /// <summary>Rendering the published content failed.</summary>
    public const string TemplateRenderFailed = "template-render-failed";

    /// <summary>The producer principal is outside the registry, or the class is not allowed for it.</summary>
    public const string ProducerNotAuthorized = "producer-not-authorized";

    /// <summary>The producer registration exists and is switched off.</summary>
    public const string ProducerDisabled = "producer-disabled";

    /// <summary>The token of the REST principal carries no app role covering the requested class.</summary>
    public const string ClassNotAllowedForPrincipal = "class-not-allowed-for-principal";

    /// <summary>The template declares sensitive variables and the request arrived over the shared bus.</summary>
    public const string SensitiveVariablesOnBus = "sensitive-variables-on-bus";

    /// <summary>The recipient has no reachable contact for any eligible channel.</summary>
    public const string NoValidContact = "no-valid-contact";

    /// <summary>The recipient did not consent to the purpose of this template.</summary>
    public const string NoConsent = "no-consent";

    /// <summary>The per-recipient budget of the class is exhausted.</summary>
    public const string RecipientRateLimited = "recipient-rate-limited";

    /// <summary>An equivalent notification is inside the policy's deduplication window.</summary>
    public const string DuplicateWindow = "duplicate-window";

    /// <summary>The request body is structurally invalid.</summary>
    public const string PayloadInvalid = "payload-invalid";

    /// <summary>The same idempotency key arrived with a different payload.</summary>
    public const string IdempotencyKeyConflict = "idempotency-key-conflict";

    /// <summary>The time to live elapsed before the notification reached a channel.</summary>
    public const string Expired = "expired";

    /// <summary>Every catalog member, for contract checks and for operator tooling.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        TemplateDeprecated,
        TemplateDisabled,
        TemplateNotFound,
        TemplateClassMismatch,
        TemplateVariablesInvalid,
        TemplateRenderFailed,
        ProducerNotAuthorized,
        ProducerDisabled,
        ClassNotAllowedForPrincipal,
        SensitiveVariablesOnBus,
        NoValidContact,
        NoConsent,
        RecipientRateLimited,
        DuplicateWindow,
        PayloadInvalid,
        IdempotencyKeyConflict,
        Expired,
    };

    public static bool IsCanonical(string? reason) => reason is not null && All.Contains(reason);
}
