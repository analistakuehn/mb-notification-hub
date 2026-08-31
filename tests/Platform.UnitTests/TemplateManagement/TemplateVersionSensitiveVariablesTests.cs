using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// The declaration of which variables carry sensitive data, as an edit of the
/// version: it must be covered by the content hash the publication approval
/// signs, it must make its author an editor so a second person is required to
/// publish it, and it must refuse a name the mask could never address.
/// </summary>
public sealed class TemplateVersionSensitiveVariablesTests
{
    private static readonly TemplateKey Key = TemplateKey.Create("auth.otp.login").Value!;
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Channel Email = Channel.Create("email").Value!;
    private static readonly Locale PtBr = Locale.Create("pt-BR").Value!;

    [Theory]
    [InlineData("1code")]
    [InlineData("with space")]
    [InlineData("with-dash")]
    [InlineData("")]
    [InlineData(".cpf")]
    [InlineData("cpf.")]
    [InlineData("cliente..cpf")]
    [InlineData("cliente.1cpf")]
    public void Rejects_sensitive_variables_that_are_not_variable_names(string variable)
    {
        TemplateVersion draft = DraftWithContent();

        Result result = draft.SetSensitiveVariables([variable], "author-1");

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
        draft.SensitiveVariables.ShouldBeEmpty();
    }

    /// <summary>
    /// The binding that makes the approval mean something. Both lists are
    /// non-empty and different on purpose: comparing an empty declaration
    /// against a non-empty one is also satisfied by a hash that only records
    /// whether the field is there, which is the weaker property.
    /// </summary>
    [Fact]
    public void Two_versions_that_differ_only_in_the_declared_names_do_not_share_a_content_hash()
    {
        TemplateVersion first = DraftWithContent();
        TemplateVersion second = DraftWithContent();
        first.ContentHash.ShouldBe(second.ContentHash);

        first.SetSensitiveVariables(["cpf"], "author-1").IsSuccess.ShouldBeTrue();
        second.SetSensitiveVariables(["email"], "author-1").IsSuccess.ShouldBeTrue();

        first.SensitiveVariables.ShouldBe(["cpf"]);
        second.SensitiveVariables.ShouldBe(["email"]);
        first.ContentHash.ShouldNotBe(second.ContentHash);
    }

    /// <summary>
    /// The declaration is a set: the mask and the publication check both read
    /// it through a hash set, so two versions naming the same variables are the
    /// same version whatever order the author typed them in.
    /// </summary>
    [Fact]
    public void The_order_the_names_were_typed_does_not_change_the_content_hash()
    {
        TemplateVersion first = DraftWithContent();
        TemplateVersion second = DraftWithContent();

        first.SetSensitiveVariables(["cpf", "email"], "author-1").IsSuccess.ShouldBeTrue();
        second.SetSensitiveVariables(["email", "cpf"], "author-1").IsSuccess.ShouldBeTrue();

        first.ContentHash.ShouldBe(second.ContentHash);
    }

    /// <summary>
    /// Four eyes over the declaration itself. The principal here writes nothing
    /// but the list, so a mutator that changed the names without recording its
    /// editor would leave this principal free to approve their own declaration.
    /// </summary>
    [Fact]
    public void The_principal_who_declared_the_sensitive_variables_cannot_publish_the_version()
    {
        TemplateVersion draft = DraftWithContent();
        draft.SetSensitiveVariables(["cpf"], "reviewer-2").IsSuccess.ShouldBeTrue();

        Result refused = draft.CanBePublishedBy("reviewer-2");

        refused.IsFailure.ShouldBeTrue();
        refused.ErrorKind.ShouldBe(ResultErrorKind.Forbidden);
        DomainError.Describe(refused.Error, refused.ErrorKind).Code.ShouldBe(ErrorCodes.FourEyesViolation);
        draft.CanBePublishedBy("publisher-3").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_published_version_refuses_a_change_to_its_sensitive_variables()
    {
        TemplateVersion published = DraftWithContent();
        published.SetSensitiveVariables(["cpf"], "author-1").IsSuccess.ShouldBeTrue();
        published.Publish("publisher-1", CreatedAt.AddHours(1)).IsSuccess.ShouldBeTrue();
        var approved = published.ContentHash;

        Result result = published.SetSensitiveVariables(["cpf", "email"], "author-1");

        result.IsFailure.ShouldBeTrue();
        published.SensitiveVariables.ShouldBe(["cpf"]);
        published.ContentHash.ShouldBe(approved);
    }

    private static TemplateVersion DraftWithContent()
    {
        var draft = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        draft.SetContent(
            new ContentEdit(Email, PtBr, "Aviso", "<p>Documento {{ cpf }}</p>", "Documento {{ cpf }}"),
            "author-1").IsSuccess.ShouldBeTrue();
        return draft;
    }
}
