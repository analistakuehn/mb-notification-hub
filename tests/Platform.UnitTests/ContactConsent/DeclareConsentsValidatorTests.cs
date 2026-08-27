using FluentValidation.Results;
using NotificationHub.Api.Modules.ContactConsent.Features.Mutations;

namespace NotificationHub.UnitTests.ContactConsent;

public sealed class DeclareConsentsValidatorTests
{
    private static readonly DeclareConsents.Validator Validator = new();

    private static DeclareConsents.ConsentDeclaration Declaration(string purpose, bool granted)
        => new(purpose, "email", granted, "app", "v1");

    private static ValidationResult Validate(params DeclareConsents.ConsentDeclaration[] declarations)
        => Validator.Validate(new DeclareConsents.Command(declarations));

    [Fact]
    public void Two_spellings_of_one_purpose_in_one_request_are_the_same_pair_declared_twice()
    {
        ValidationResult result = Validate(
            Declaration("Marketing", granted: true),
            Declaration("marketing", granted: false));

        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void An_untrimmed_spelling_is_caught_by_the_same_guard()
    {
        ValidationResult result = Validate(
            Declaration(" marketing", granted: true),
            Declaration("marketing", granted: false));

        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Two_genuinely_distinct_purposes_stay_valid()
        => Validate(
                Declaration("marketing", granted: true),
                Declaration("marketing-updates", granted: false))
            .IsValid.ShouldBeTrue();
}
