using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class LayoutLifecycleTests
{
    private static readonly LayoutKey Key = LayoutKey.Create("email.base").Value!;

    [Fact]
    public void Creating_a_layout_normalizes_the_owner_team_and_starts_active()
    {
        Result<Layout> layout = Layout.Create(Key, new LayoutMetadata
        {
            OwnerTeam = "  design-system  ",
            DefaultLocale = Locale.Create("pt-BR").Value,
        });

        layout.IsSuccess.ShouldBeTrue();
        layout.Value!.OwnerTeam.ShouldBe("design-system");
        layout.Value!.DefaultLocale!.Value.ShouldBe("pt-BR");
        layout.Value!.Status.ShouldBe(LayoutStatus.Active);
    }

    [Fact]
    public void Creating_a_layout_without_an_owner_team_is_rejected()
    {
        Result<Layout> layout = Layout.Create(Key, new LayoutMetadata { OwnerTeam = "   " });

        layout.IsFailure.ShouldBeTrue();
        layout.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }

    [Fact]
    public void Rejects_malformed_layout_keys()
    {
        Result<LayoutKey> key = LayoutKey.Create("Email.Base");

        key.IsFailure.ShouldBeTrue();
        DomainError.Describe(key.Error, key.ErrorKind).Code.ShouldBe(ErrorCodes.InvalidRequest);
    }

    [Fact]
    public void An_active_layout_can_be_deprecated_and_then_disabled()
    {
        Layout layout = NewLayout();

        layout.Deprecate().IsSuccess.ShouldBeTrue();
        layout.Status.ShouldBe(LayoutStatus.Deprecated);
        layout.Disable().IsSuccess.ShouldBeTrue();
        layout.Status.ShouldBe(LayoutStatus.Disabled);
    }

    [Fact]
    public void Deprecating_twice_names_the_current_status_and_the_remaining_transitions()
    {
        Layout layout = NewLayout();
        layout.Deprecate().IsSuccess.ShouldBeTrue();

        Result result = layout.Deprecate();

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo info = DomainError.Describe(result.Error, result.ErrorKind);
        info.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        info.CurrentStatus.ShouldBe("deprecated");
        info.AllowedTransitions.ShouldBe(["disabled"]);
    }

    [Fact]
    public void Disabling_a_disabled_layout_is_rejected_with_no_transitions_left()
    {
        Layout layout = NewLayout();
        layout.Disable().IsSuccess.ShouldBeTrue();

        Result result = layout.Disable();

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo info = DomainError.Describe(result.Error, result.ErrorKind);
        info.CurrentStatus.ShouldBe("disabled");
        info.AllowedTransitions.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, "deprecated")]
    [InlineData(true, "disabled")]
    public void A_retired_layout_rejects_publications_naming_its_status(bool disable, string expectedStatus)
    {
        Layout layout = NewLayout();
        Result transition = disable ? layout.Disable() : layout.Deprecate();
        transition.IsSuccess.ShouldBeTrue();

        Result result = layout.EnsureAcceptsPublication();

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo info = DomainError.Describe(result.Error, result.ErrorKind);
        info.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        info.CurrentStatus.ShouldBe(expectedStatus);
    }

    private static Layout NewLayout()
        => Layout.Create(Key, new LayoutMetadata { OwnerTeam = "design-system" }).Value!;
}
