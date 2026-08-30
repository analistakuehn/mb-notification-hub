namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Stable machine-readable error codes surfaced as the RFC 9457 problem <c>type</c>.
/// </summary>
public static class ErrorCodes
{
    public const string InvalidRequest = "invalid-request";
    public const string InvalidStateTransition = "invalid-state-transition";
    public const string TemplateAlreadyExists = "template-already-exists";
    public const string TemplateNotFound = "template-not-found";
    public const string TemplateVersionNotFound = "template-version-not-found";
    public const string DraftAlreadyExists = "draft-already-exists";
    public const string PublicationConflict = "publication-conflict";
    public const string PreconditionFailed = "precondition-failed";
    public const string ActorUnidentified = "actor-unidentified";
    public const string TemplateContentNotFound = "template-content-not-found";
    public const string TemplateRenderFailed = "template-render-failed";
    public const string VariablesPayloadTooLarge = "variables-payload-too-large";
    public const string VariablesPayloadUnreadable = "variables-payload-unreadable";
    public const string VariablesSchemaUnreadable = "variables-schema-unreadable";
    public const string UrlDomainNotAllowed = "url-domain-not-allowed";
    public const string FourEyesViolation = "four-eyes-violation";
    public const string ContentHashMismatch = "content-hash-mismatch";
    public const string StoredContentUnreadable = "stored-content-unreadable";
    public const string TemplateValidationFailed = "template-validation-failed";
    public const string LayoutAlreadyExists = "layout-already-exists";
    public const string LayoutNotFound = "layout-not-found";
    public const string LayoutVersionNotFound = "layout-version-not-found";
    public const string LayoutContentNotFound = "layout-content-not-found";
    public const string LayoutValidationFailed = "layout-validation-failed";
    public const string ClassPolicyNotFound = "class-policy-not-found";
    public const string ClassPolicyDraftNotFound = "class-policy-draft-not-found";
    public const string ClassPolicyVersionNotFound = "class-policy-version-not-found";
    public const string ClassPolicyValidationFailed = "class-policy-validation-failed";
}
