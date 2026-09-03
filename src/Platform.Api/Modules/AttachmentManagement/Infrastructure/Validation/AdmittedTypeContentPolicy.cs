using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;

/// <summary>
/// The policy this module ships. It decides on what was declared, on what the
/// leading bytes were recognized as, and on the list of types an operator
/// admitted, in that order.
/// <para>
/// What it does not do, stated because the gate it feeds is a security gate:
/// it does not open the content, it does not look for malicious code, and it
/// cannot tell a document that needs a password from one that does not. A file
/// of an admitted type whose leading bytes agree with its declaration is
/// approved by this policy whatever is inside it. Closing that gap is a
/// verifier's job, and until one exists behind this seam the admitted list is
/// what stands between a producer and a recipient.
/// </para>
/// </summary>
internal sealed class AdmittedTypeContentPolicy(IOptions<AttachmentValidationOptions> options)
    : IAttachmentContentPolicy
{
    private readonly HashSet<string> _admitted =
    [
        .. options.Value.AdmittedContentTypes
            .Select(AttachmentContentSignatures.Canonical)
            .OfType<string>(),
    ];

    public Task<AttachmentPolicyVerdict> EvaluateAsync(
        AttachmentContentSubject subject,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Evaluate(subject));
    }

    private AttachmentPolicyVerdict Evaluate(AttachmentContentSubject subject)
    {
        // Nothing in the table matched, so the module knows what the producer
        // said and nothing about what it got. That is the whole of what
        // uninspectable means here, and it is refused rather than waited on.
        if (subject.DetectedContentType is not { } detected)
        {
            return AttachmentPolicyVerdict.Refuse(
                AttachmentValidationDetails.ContentNotInspectable);
        }

        // The declaration and the bytes name different types. Whichever of the
        // two is wrong, the pair cannot be released: a recipient would be
        // handed something other than what the notification says it is.
        if (AttachmentContentSignatures.Canonical(subject.DeclaredContentType) != detected)
        {
            return AttachmentPolicyVerdict.Refuse(
                AttachmentValidationDetails.ContentTypeDivergent);
        }

        return _admitted.Contains(detected)
            ? AttachmentPolicyVerdict.Approve()
            : AttachmentPolicyVerdict.Refuse(
                AttachmentValidationDetails.ContentTypeNotAdmitted);
    }
}
