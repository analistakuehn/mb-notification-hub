using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;

namespace NotificationHub.UnitTests.AttachmentManagement;

/// <summary>
/// The policy the module ships. What it proves is the type rule and the closed
/// default; what it cannot prove is anything about what is inside a file,
/// because it never opens one.
/// </summary>
public sealed class AttachmentContentPolicyTests
{
    [Fact]
    public async Task With_nothing_admitted_a_file_that_matches_its_declaration_is_still_refused()
    {
        AttachmentPolicyVerdict verdict = await Policy().EvaluateAsync(
            new AttachmentContentSubject("application/pdf", "application/pdf", 128),
            CancellationToken.None);

        verdict.Decision.ShouldBe(AttachmentPolicyDecision.Refused);
        verdict.Detail.ShouldBe(AttachmentValidationDetails.ContentTypeNotAdmitted);
    }

    [Fact]
    public async Task A_declaration_the_leading_bytes_contradict_is_refused()
    {
        AttachmentPolicyVerdict verdict = await Policy("image/gif", "application/pdf")
            .EvaluateAsync(
                new AttachmentContentSubject("image/gif", "application/pdf", 128),
                CancellationToken.None);

        // Both types are admitted, so the refusal is about the pair and not
        // about the list: what a recipient would open is not what the
        // notification says it is.
        verdict.Decision.ShouldBe(AttachmentPolicyDecision.Refused);
        verdict.Detail.ShouldBe(AttachmentValidationDetails.ContentTypeDivergent);
    }

    [Fact]
    public async Task Leading_bytes_no_signature_describes_are_refused_as_unrecognized()
    {
        AttachmentPolicyVerdict verdict = await Policy("application/pdf").EvaluateAsync(
            new AttachmentContentSubject("application/pdf", null, 128),
            CancellationToken.None);

        verdict.Decision.ShouldBe(AttachmentPolicyDecision.Refused);
        verdict.Detail.ShouldBe(AttachmentValidationDetails.ContentNotInspectable);
    }

    /// <summary>
    /// The one approval this policy gives, and the whole of what it means: the
    /// bytes start like the declared type and an operator admitted that type.
    /// Nothing was scanned, and a file of this type carrying anything at all
    /// inside it is approved by this same path.
    /// </summary>
    [Fact]
    public async Task An_admitted_type_whose_leading_bytes_agree_is_approved()
    {
        AttachmentPolicyVerdict verdict = await Policy("APPLICATION/PDF").EvaluateAsync(
            new AttachmentContentSubject("application/pdf; charset=binary", "application/pdf", 1),
            CancellationToken.None);

        verdict.Decision.ShouldBe(AttachmentPolicyDecision.Approved);
        verdict.Detail.ShouldBeEmpty();
    }

    /// <summary>
    /// Admitting one type is not admitting the next one. Without this, a list
    /// that was read as a switch rather than as a set would pass every test
    /// above.
    /// </summary>
    [Fact]
    public async Task A_type_outside_the_admitted_list_is_refused_while_another_is_approved()
    {
        AdmittedTypeContentPolicy policy = Policy("image/png");

        AttachmentPolicyVerdict admitted = await policy.EvaluateAsync(
            new AttachmentContentSubject("image/png", "image/png", 1),
            CancellationToken.None);
        AttachmentPolicyVerdict other = await policy.EvaluateAsync(
            new AttachmentContentSubject("image/gif", "image/gif", 1),
            CancellationToken.None);

        admitted.Decision.ShouldBe(AttachmentPolicyDecision.Approved);
        other.Decision.ShouldBe(AttachmentPolicyDecision.Refused);
        other.Detail.ShouldBe(AttachmentValidationDetails.ContentTypeNotAdmitted);
    }

    /// <summary>
    /// A declaration the module cannot even parse is a declaration that names
    /// no type, so it can never equal what the bytes were recognized as.
    /// </summary>
    [Fact]
    public async Task A_declaration_that_names_no_type_is_refused_as_divergent()
    {
        AttachmentPolicyVerdict verdict = await Policy("application/pdf").EvaluateAsync(
            new AttachmentContentSubject("not a media type", "application/pdf", 1),
            CancellationToken.None);

        verdict.Decision.ShouldBe(AttachmentPolicyDecision.Refused);
        verdict.Detail.ShouldBe(AttachmentValidationDetails.ContentTypeDivergent);
    }

    [Fact]
    public async Task The_policy_never_answers_that_it_could_not_conclude()
    {
        AttachmentPolicyVerdict[] verdicts =
        [
            await Policy().EvaluateAsync(
                new AttachmentContentSubject("application/pdf", null, 1),
                CancellationToken.None),
            await Policy().EvaluateAsync(
                new AttachmentContentSubject("application/pdf", "application/pdf", 1),
                CancellationToken.None),
            await Policy("image/png").EvaluateAsync(
                new AttachmentContentSubject("image/png", "image/png", 1),
                CancellationToken.None),
        ];

        // It decides on what it was given, always. The open verdict exists in
        // the vocabulary for a verifier that has not arrived, and the deadline
        // that bounds it has nothing to measure until one does.
        verdicts.ShouldAllBe(verdict =>
            verdict.Decision != AttachmentPolicyDecision.Inconclusive);
    }

    private static AdmittedTypeContentPolicy Policy(params string[] admitted)
        => new(Options.Create(new AttachmentValidationOptions
        {
            AdmittedContentTypes = admitted,
        }));
}
