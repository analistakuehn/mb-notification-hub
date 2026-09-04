namespace NotificationHub.Api.Modules.AttachmentManagement.Domain;

public static class ErrorCodes
{
    public const string InvalidMetadata = "attachment-metadata-invalid";
    public const string InvalidProducerGrant = "attachment-producer-grant-invalid";
    public const string InvalidReference = "attachment-reference-invalid";
    public const string AccessDenied = "attachment-access-denied";
    public const string AuthorizationUnavailable = "attachment-authorization-unavailable";
    public const string NotFound = "attachment-not-found";
    public const string SizeMismatch = "attachment-size-mismatch";
    public const string AlreadyReceived = "attachment-already-received";
    public const string UploadConflict = "attachment-upload-conflict";
    public const string StoreUnavailable = "attachment-store-unavailable";

    /// <summary>
    /// The store took the bytes and named no generation for them. The store
    /// answered, so it was not unavailable, and saying it was sends whoever
    /// reads the code looking at connectivity instead of at a store that does
    /// not keep generations.
    /// </summary>
    public const string StoreUnidentifiedGeneration = "attachment-store-unidentified-generation";

    /// <summary>
    /// The generation the store had just named could not be read back. Not
    /// finding it is a different event from not reaching the store, and only
    /// this one says the bytes moved under the module.
    /// </summary>
    public const string GenerationUnreadable = "attachment-generation-unreadable";

    /// <summary>
    /// The one public reason for the whole family of content refusals: an
    /// unrecognized file, a declaration the bytes contradict, a type nobody
    /// admitted, and a verdict that never concluded all leave under this word.
    /// <para>
    /// One word, on purpose. The public vocabulary is a contract with
    /// producers and it is easier to add to than to take back, so the fine
    /// detail of which check refused stays in durable state and reaches the
    /// operational side through the authorized query. Splitting this into a
    /// word per check would also tell a producer which check to work around.
    /// </para>
    /// </summary>
    public const string ContentRefused = "attachment-content-refused";

    /// <summary>
    /// A verdict was asked for over an attachment whose bytes never arrived.
    /// It is not a refusal of the content, because there is no content, and it
    /// is the one answer that tells a producer the next step is the upload it
    /// still owes rather than a file it has to change.
    /// </summary>
    public const string ContentMissing = "attachment-content-missing";

    /// <summary>
    /// A revocation was asked for over an attachment that carries no release.
    /// Nothing was taken back because nothing had been granted, and answering
    /// with a success would report a withdrawal that never happened.
    /// </summary>
    public const string NotReleased = "attachment-not-released";

    /// <summary>
    /// The release was taken back. It is a word of its own beside the refusal
    /// of content, because the two are different events for whoever reads
    /// them: the content of a refused attachment was never approved, and the
    /// content of a revoked one was approved and the approval was withdrawn.
    /// </summary>
    public const string Revoked = "attachment-revoked";

    /// <summary>
    /// The content of the attachment was discarded, so there is nothing left
    /// to upload to, to validate or to use. It is a word of its own beside the
    /// refusal and the withdrawal, because those two say what happened to the
    /// approval and this one says the content is gone, and only this one tells
    /// a producer that the reference is spent and a new registration is the
    /// next step.
    /// </summary>
    public const string Discarded = "attachment-discarded";

    /// <summary>
    /// The module could not carry a lifecycle transition through, and wrote
    /// nothing. One word covers the whole family, as the refusal of content
    /// does: a policy that did not decide, an identity the module cannot name,
    /// and a release it cannot find are three lines on the record and one
    /// answer here, because a producer's next step is the same for all three
    /// and the difference between them is not a producer's business.
    /// </summary>
    public const string LifecycleUnavailable = "attachment-lifecycle-unavailable";
}
