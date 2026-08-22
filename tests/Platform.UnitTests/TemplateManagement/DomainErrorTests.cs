using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class DomainErrorTests
{
    [Fact]
    public void A_formatted_error_round_trips_code_and_detail()
    {
        var error = DomainError.Format(ErrorCodes.TemplateNotFound, "Template 'x' does not exist.");

        DomainErrorInfo info = DomainError.Describe(error, ResultErrorKind.NotFound);

        info.Code.ShouldBe(ErrorCodes.TemplateNotFound);
        info.Detail.ShouldBe("Template 'x' does not exist.");
        info.CurrentStatus.ShouldBeNull();
        info.AllowedTransitions.ShouldBeEmpty();
    }

    [Fact]
    public void A_state_transition_error_round_trips_status_and_transitions()
    {
        var error = DomainError.StateTransition("published", ["superseded"], "Cannot edit a published version.");

        DomainErrorInfo info = DomainError.Describe(error, ResultErrorKind.BusinessRule);

        info.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        info.Detail.ShouldBe("Cannot edit a published version.");
        info.CurrentStatus.ShouldBe("published");
        info.AllowedTransitions.ShouldBe(["superseded"]);
    }

    [Fact]
    public void A_terminal_status_reports_an_empty_transition_list()
    {
        var error = DomainError.StateTransition("superseded", [], "No transitions remain.");

        DomainErrorInfo info = DomainError.Describe(error, ResultErrorKind.BusinessRule);

        info.AllowedTransitions.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ResultErrorKind.Validation, "invalid-request")]
    [InlineData(ResultErrorKind.NotFound, "not-found")]
    [InlineData(ResultErrorKind.BusinessRule, "conflict")]
    [InlineData(ResultErrorKind.Forbidden, "forbidden")]
    public void A_plain_error_string_falls_back_to_a_code_derived_from_the_kind(
        ResultErrorKind kind,
        string expectedCode)
    {
        DomainErrorInfo info = DomainError.Describe("something went wrong", kind);

        info.Code.ShouldBe(expectedCode);
        info.Detail.ShouldBe("something went wrong");
    }

    [Fact]
    public void A_missing_error_string_still_produces_a_describable_problem()
    {
        DomainErrorInfo info = DomainError.Describe(null, ResultErrorKind.BusinessRule);

        info.Code.ShouldBe("conflict");
        info.Detail.ShouldNotBeNullOrWhiteSpace();
    }
}
