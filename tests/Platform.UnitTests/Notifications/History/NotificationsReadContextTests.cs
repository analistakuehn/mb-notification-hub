using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.UnitTests.Notifications.History;

public sealed class NotificationsReadContextTests
{
    private const string WriteConnection = "Host=write-host;Database=hub;Username=hub";
    private const string ReadConnection = "Host=replica-host;Database=hub;Username=hub";

    [Fact]
    public void Without_a_read_connection_the_query_reads_the_write_database()
    {
        var options = new NotificationsEfOptions { ConnectionString = WriteConnection };

        options.EffectiveReadConnectionString.ShouldBe(WriteConnection);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_read_connection_falls_back_instead_of_opening_nothing(string configured)
    {
        var options = new NotificationsEfOptions
        {
            ConnectionString = WriteConnection,
            ReadConnectionString = configured,
        };

        options.EffectiveReadConnectionString.ShouldBe(WriteConnection);
    }

    [Fact]
    public void A_configured_read_connection_is_the_one_the_query_opens()
    {
        var options = new NotificationsEfOptions
        {
            ConnectionString = WriteConnection,
            ReadConnectionString = ReadConnection,
        };

        options.EffectiveReadConnectionString.ShouldBe(ReadConnection);
    }

    [Fact]
    public void The_read_context_never_tracks_what_it_materializes()
    {
        using NotificationsReadDbContext db = CreateReadContext();

        db.ChangeTracker.QueryTrackingBehavior.ShouldBe(QueryTrackingBehavior.NoTracking);
    }

    [Fact]
    public void Saving_through_the_read_context_throws_instead_of_reaching_the_database()
    {
        using NotificationsReadDbContext db = CreateReadContext();

        Should.Throw<InvalidOperationException>(() => db.SaveChanges());
        Should.Throw<InvalidOperationException>(() => db.SaveChanges(acceptAllChangesOnSuccess: true));
    }

    [Fact]
    public async Task Saving_asynchronously_through_the_read_context_throws_too()
    {
        await using NotificationsReadDbContext db = CreateReadContext();

        await Should.ThrowAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        await Should.ThrowAsync<InvalidOperationException>(
            () => db.SaveChangesAsync(acceptAllChangesOnSuccess: true));
    }

    [Fact]
    public void The_write_context_still_saves_through_the_same_model()
    {
        // The guardrail belongs to the read derivation alone: proving the base
        // context kept its write entry point keeps the previous test from
        // passing because saving broke everywhere.
        using var db = new NotificationsDbContext(
            new DbContextOptionsBuilder<NotificationsDbContext>().UseNpgsql(WriteConnection).Options);

        db.SaveChanges().ShouldBe(0);
    }

    private static NotificationsReadDbContext CreateReadContext()
        => new(new DbContextOptionsBuilder<NotificationsReadDbContext>().UseNpgsql(ReadConnection).Options);
}
