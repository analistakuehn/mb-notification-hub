using NotificationHub.Api.Infrastructure.Messaging.Consuming;

namespace NotificationHub.UnitTests.Infrastructure.Messaging;

public sealed class SqsBackoffTests
{
    [Fact]
    public void The_delay_grows_exponentially_with_the_receive_count()
    {
        // Jitter subtracts at most half, so the lower bound per attempt still
        // proves growth: 5*2^(n-1)/2 exceeds the previous ceiling only later,
        // so assert the band per attempt instead of strict monotonicity.
        for (var receiveCount = 1; receiveCount <= 6; receiveCount++)
        {
            var expectedCeiling = Math.Min(5 << (receiveCount - 1), 900);
            var delay = SqsBackoff.DelaySeconds(receiveCount, baseSeconds: 5, maxSeconds: 900);
            delay.ShouldBeInRange(Math.Max(1, expectedCeiling / 2), expectedCeiling);
        }
    }

    [Fact]
    public void The_delay_never_exceeds_the_configured_maximum()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            SqsBackoff.DelaySeconds(30, baseSeconds: 5, maxSeconds: 120).ShouldBeLessThanOrEqualTo(120);
        }
    }

    [Fact]
    public void A_zero_or_negative_receive_count_behaves_like_the_first_delivery()
    {
        SqsBackoff.DelaySeconds(0, baseSeconds: 4, maxSeconds: 900).ShouldBeInRange(2, 4);
        SqsBackoff.DelaySeconds(-3, baseSeconds: 4, maxSeconds: 900).ShouldBeInRange(2, 4);
    }

    [Fact]
    public void The_delay_is_always_at_least_one_second()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            SqsBackoff.DelaySeconds(1, baseSeconds: 1, maxSeconds: 900).ShouldBeGreaterThanOrEqualTo(1);
        }
    }
}
