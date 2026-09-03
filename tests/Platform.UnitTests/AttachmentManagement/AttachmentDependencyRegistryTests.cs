using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

namespace NotificationHub.UnitTests.AttachmentManagement;

public sealed class AttachmentDependencyRegistryTests
{
    [Theory]
    [InlineData("", "holder")]
    [InlineData("   ", "holder")]
    [InlineData(AttachmentDependencyReasons.ClaimConfirmed, "")]
    [InlineData(AttachmentDependencyReasons.ClaimConfirmed, "   ")]
    public async Task An_unusable_reason_or_dependent_is_refused_before_any_write(
        string reason,
        string holder)
        => (await UnreachableRegistry().HoldAsync(
                AttachmentReference.Generate(),
                reason,
                holder,
                CancellationToken.None))
            .ShouldBe(AttachmentDependencyOutcome.Invalid);

    [Fact]
    public async Task A_reason_or_dependent_past_the_column_is_refused_before_any_write()
    {
        AttachmentDependencyRegistry registry = UnreachableRegistry();

        (await registry.HoldAsync(
                AttachmentReference.Generate(),
                new string('r', AttachmentDependency.MaxReasonLength + 1),
                "holder",
                CancellationToken.None))
            .ShouldBe(AttachmentDependencyOutcome.Invalid);
        (await registry.HoldAsync(
                AttachmentReference.Generate(),
                AttachmentDependencyReasons.ClaimConfirmed,
                new string('h', AttachmentDependency.MaxHolderLength + 1),
                CancellationToken.None))
            .ShouldBe(AttachmentDependencyOutcome.Invalid);
    }

    [Fact]
    public async Task Ending_a_dependency_without_a_dependent_is_refused_before_any_write()
        => (await UnreachableRegistry().ReleaseAsync(
                AttachmentReference.Generate(),
                "  ",
                CancellationToken.None))
            .ShouldBe(AttachmentDependencyOutcome.Invalid);

    /// <summary>
    /// Points at a port nothing listens on, so any call that reaches the
    /// database fails loudly instead of passing as a refusal.
    /// </summary>
    private static AttachmentDependencyRegistry UnreachableRegistry()
        => new(
            new AttachmentManagementDbContext(
                new DbContextOptionsBuilder<AttachmentManagementDbContext>()
                    .UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none")
                    .Options),
            TimeProvider.System);
}
