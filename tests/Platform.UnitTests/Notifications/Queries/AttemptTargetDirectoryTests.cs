using Microsoft.Extensions.Logging.Abstractions;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Reads;
using NotificationHub.SharedKernel;
using NSubstitute;

namespace NotificationHub.UnitTests.Notifications.Queries;

/// <summary>
/// The flag that separates "the registration is no longer active" from "the
/// directory could not answer". Collapsing the two would make the query claim
/// knowledge it does not have, which is exactly what the omission rule of the
/// response exists to prevent.
/// </summary>
public sealed class AttemptTargetDirectoryTests
{
    private const string RecipientId = "cus_01J5X9";

    [Fact]
    public async Task An_answered_read_that_lists_the_registration_reports_it_active_with_its_platform()
    {
        var deviceTokenId = Guid.CreateVersion7();
        AttemptTargetDirectory directory = Build(Snapshot(deviceTokenId, "android"));

        AttemptTargets targets = await directory.ResolveAsync(
            RecipientId, [], [deviceTokenId], CancellationToken.None);

        targets.DeviceRegistrationsAnswered.ShouldBeTrue();
        targets.DevicePlatforms[deviceTokenId].ShouldBe("android");
    }

    [Fact]
    public async Task An_answered_read_that_omits_the_registration_is_conclusive_about_it_being_inactive()
    {
        var deviceTokenId = Guid.CreateVersion7();
        AttemptTargetDirectory directory = Build(Snapshot(Guid.CreateVersion7(), "ios"));

        AttemptTargets targets = await directory.ResolveAsync(
            RecipientId, [], [deviceTokenId], CancellationToken.None);

        targets.DeviceRegistrationsAnswered.ShouldBeTrue();
        targets.DevicePlatforms.ContainsKey(deviceTokenId).ShouldBeFalse();
    }

    [Fact]
    public async Task A_read_that_failed_states_nothing_about_the_registration()
    {
        var deviceTokenId = Guid.CreateVersion7();
        AttemptTargetDirectory directory = Build(
            Result.NotFound<RecipientSnapshot>("O destinatário não possui cadastro."));

        AttemptTargets targets = await directory.ResolveAsync(
            RecipientId, [], [deviceTokenId], CancellationToken.None);

        targets.DeviceRegistrationsAnswered.ShouldBeFalse();
        targets.DevicePlatforms.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_read_that_threw_states_nothing_either_and_never_propagates()
    {
        var deviceTokenId = Guid.CreateVersion7();
        IRecipientDirectory contacts = Substitute.For<IRecipientDirectory>();
        contacts.FindAsync(RecipientId, Arg.Any<CancellationToken>())
            .Returns<Task<Result<RecipientSnapshot>>>(_ => throw new InvalidOperationException("réplica fora do ar"));

        AttemptTargets targets = await new AttemptTargetDirectory(
                contacts, NullLogger<AttemptTargetDirectory>.Instance)
            .ResolveAsync(RecipientId, [], [deviceTokenId], CancellationToken.None);

        targets.DeviceRegistrationsAnswered.ShouldBeFalse();
        targets.DevicePlatforms.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_any_device_identity_the_read_never_runs_and_claims_nothing()
    {
        IRecipientDirectory contacts = Substitute.For<IRecipientDirectory>();

        AttemptTargets targets = await new AttemptTargetDirectory(
                contacts, NullLogger<AttemptTargetDirectory>.Instance)
            .ResolveAsync(RecipientId, [], [], CancellationToken.None);

        targets.DeviceRegistrationsAnswered.ShouldBeFalse();
        await contacts.DidNotReceive().FindAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static AttemptTargetDirectory Build(Result<RecipientSnapshot> answer)
    {
        IRecipientDirectory contacts = Substitute.For<IRecipientDirectory>();
        contacts.FindAsync(RecipientId, Arg.Any<CancellationToken>()).Returns(answer);
        return new AttemptTargetDirectory(contacts, NullLogger<AttemptTargetDirectory>.Instance);
    }

    private static Result<RecipientSnapshot> Snapshot(Guid deviceTokenId, string platform)
        => Result.Success(new RecipientSnapshot
        {
            RecipientId = RecipientId,
            Timezone = "America/Sao_Paulo",
            ContactPoints = [],
            Consents = [],
            Devices = [new DeviceRegistration(deviceTokenId, platform, null, DateTimeOffset.UtcNow)],
            Suppressions = [],
        });
}
