using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// The reason vocabulary of the template and layout lifecycle. The codes are
/// spelled out here rather than read back from the type: a rename that keeps
/// the member name and changes the wire value would still group the archived
/// evidence under a new category, and only a literal catches that.
/// </summary>
public sealed class LifecycleReasonsTests
{
    private static readonly string[] Accepted =
    [
        "superseded-by-new-version",
        "visual-identity-change",
        "retired",
        "content-incorrect",
        "content-compromised",
        "other",
    ];

    [Theory]
    [InlineData("superseded-by-new-version")]
    [InlineData("visual-identity-change")]
    [InlineData("retired")]
    [InlineData("content-incorrect")]
    [InlineData("content-compromised")]
    [InlineData("other")]
    public void Every_reason_the_lifecycle_endpoints_accept_is_canonical(string reason)
        => LifecycleReasons.IsCanonical(reason).ShouldBeTrue(reason);

    [Fact]
    public void The_vocabulary_holds_exactly_the_reasons_the_endpoints_accept()
        => LifecycleReasons.CanonicalValues
            .OrderBy(reason => reason, StringComparer.Ordinal)
            .ShouldBe(Accepted.OrderBy(reason => reason, StringComparer.Ordinal));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("substituido pela campanha nova")]
    [InlineData("Content-Incorrect")]
    [InlineData("content_incorrect")]
    [InlineData("retired ")]
    public void A_reason_code_outside_the_vocabulary_is_not_canonical(string? reason)
        => LifecycleReasons.IsCanonical(reason).ShouldBeFalse();
}
