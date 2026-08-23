using NotificationHub.Api.Modules.Notifications.Infrastructure.Http;

namespace NotificationHub.UnitTests.Notifications.Queries;

public sealed class NotificationQueryWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Without_bounds_the_window_ends_now_and_starts_ninety_days_earlier()
    {
        NotificationQueryWindow.TryResolve(from: null, to: null, Now, out NotificationQueryWindow window, out _)
            .ShouldBeTrue();

        window.To.ShouldBe(Now);
        window.From.ShouldBe(Now.AddDays(-90));
    }

    [Fact]
    public void An_upper_bound_alone_moves_the_default_span_with_it()
    {
        DateTimeOffset upper = Now.AddDays(-10);

        NotificationQueryWindow.TryResolve(from: null, upper, Now, out NotificationQueryWindow window, out _)
            .ShouldBeTrue();

        window.To.ShouldBe(upper);
        window.From.ShouldBe(upper.AddDays(-90));
    }

    [Fact]
    public void Both_bounds_are_honored_when_the_span_fits()
    {
        DateTimeOffset lower = Now.AddDays(-179);

        NotificationQueryWindow.TryResolve(lower, Now, Now, out NotificationQueryWindow window, out _)
            .ShouldBeTrue();

        window.From.ShouldBe(lower);
        window.To.ShouldBe(Now);
    }

    [Fact]
    public void An_inverted_window_is_refused()
    {
        NotificationQueryWindow.TryResolve(Now, Now.AddDays(-1), Now, out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_span_beyond_the_ceiling_is_refused_instead_of_trimmed()
    {
        NotificationQueryWindow.TryResolve(Now.AddDays(-181), Now, Now, out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_ceiling_itself_is_accepted()
        => NotificationQueryWindow.TryResolve(Now.AddDays(-180), Now, Now, out _, out _).ShouldBeTrue();

    [Fact]
    public void Containment_includes_both_bounds_and_excludes_anything_outside()
    {
        var window = new NotificationQueryWindow(Now.AddDays(-1), Now);

        window.Contains(Now.AddDays(-1)).ShouldBeTrue();
        window.Contains(Now).ShouldBeTrue();
        window.Contains(Now.AddDays(-1).AddTicks(-1)).ShouldBeFalse();
        window.Contains(Now.AddTicks(1)).ShouldBeFalse();
    }
}
