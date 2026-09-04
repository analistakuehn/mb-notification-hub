using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Dispatch.Domain;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Resilience;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.Dispatch;

/// <summary>
/// Whether a channel carries an accepted set of attachments, asked of the
/// composition a deployment actually runs.
/// <para>
/// The answer decides whether a notification carrying attachments may be
/// planned onto a channel at all, so the two ways it can be wrong are
/// expensive in opposite directions. An answer that is falsely negative kills
/// every send with a set on a deployment whose adapter carries one; an answer
/// that is falsely positive puts a message in front of a recipient without the
/// documents its producer was told it would have. Neither is visible from an
/// adapter held in a bare instance: the container hands out decorated
/// providers, and a decorator that forgot to forward the answer would answer
/// for an adapter it is not.
/// </para>
/// </summary>
[Collection(DispatchPostgresCollectionDefinition.Name)]
public sealed class ChannelAttachmentSupportTests(DispatchPostgresFixture fixture)
{
    private static readonly Dictionary<string, string?> ProviderSettings = new()
    {
        ["Modules:Dispatch:Providers:SendGrid:BaseAddress"] = "https://sendgrid.invalid",
        ["Modules:Dispatch:Providers:Fcm:BaseAddress"] = "https://fcm.invalid",
        ["Modules:Dispatch:Providers:Twilio:BaseAddress"] = "https://twilio.invalid",
    };

    /// <summary>
    /// Every adapter this process hosts, taken out of the container exactly as
    /// the send takes it, answering for itself.
    /// <para>
    /// The instances are the decorated ones, and the assertion below says so
    /// before it says anything else: resolving the bare adapters instead would
    /// leave the two decorators unmeasured, and they are the only place in this
    /// graph where the answer can be lost without anybody editing an adapter.
    /// </para>
    /// </summary>
    [Fact]
    public void The_composed_providers_answer_the_attachment_question_for_themselves()
    {
        using ServiceProvider services = DispatchTestServices.BuildProviderHost(ProviderSettings);

        IChannelProvider[] hosted = [.. services.GetServices<IChannelProvider>()];

        hosted.Length.ShouldBe(3);
        hosted.ShouldAllBe(provider => provider is RateLimitedChannelProvider,
            "o container entrega o adaptador decorado, e é o decorado que o envio usa; "
            + "medir a instância nua deixaria os dois decoradores fora do alcance desta regra.");

        var answers = hosted.ToDictionary(
            provider => provider.ProviderKey,
            provider => provider.CarriesAttachments,
            StringComparer.Ordinal);

        answers["sendgrid"].ShouldBeTrue(
            "o adaptador de e-mail compõe cada membro do conjunto no corpo que envia; "
            + "um não aqui recusaria toda notificação com anexo desta implantação.");
        answers["twilio"].ShouldBeFalse();
        answers["fcm"].ShouldBeFalse();
    }

    /// <summary>
    /// The published question, answered through the configured resolution.
    /// <para>
    /// The arrangement inverts what the shipped adapters answer, on purpose:
    /// the channel that carries here is SMS and the one that does not is
    /// e-mail. A table keyed by channel name kept anywhere in this path would
    /// answer the other way round on both, and that is the whole failure this
    /// shape exists to make impossible.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task The_answer_comes_from_the_adapter_the_configuration_points_at()
    {
        await ResetTableAsync();
        await SeedAsync(("email", "quiet-mail", 0), ("sms", "loud-sms", 0));
        await using ServiceProvider services = DispatchTestServices.BuildResolutionHost(
            fixture.ConnectionString,
            TimeProvider.System,
            new FakeChannelProvider(Channel.Email, "quiet-mail") { CarriesAttachments = false },
            new FakeChannelProvider(Channel.Sms, "loud-sms") { CarriesAttachments = true });
        IChannelAttachmentSupport support = services.GetRequiredService<IChannelAttachmentSupport>();

        Result<bool> email = await support.CarriesAttachmentsAsync(
            Channel.Email, CancellationToken.None);
        Result<bool> sms = await support.CarriesAttachmentsAsync(Channel.Sms, CancellationToken.None);

        email.IsSuccess.ShouldBeTrue(email.Error);
        email.Value.ShouldBeFalse();
        sms.IsSuccess.ShouldBeTrue(sms.Error);
        sms.Value.ShouldBeTrue();
    }

    /// <summary>
    /// A channel nothing is configured for is a deployment defect and travels
    /// as one. Answering it as a plain no would end notifications with a
    /// product reason for a fault that is ours, and the reason would name the
    /// message when the defect is in the configuration table.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_channel_with_no_configured_adapter_fails_instead_of_answering_no()
    {
        await ResetTableAsync();
        await SeedAsync(("email", "quiet-mail", 0));
        await using ServiceProvider services = DispatchTestServices.BuildResolutionHost(
            fixture.ConnectionString,
            TimeProvider.System,
            new FakeChannelProvider(Channel.Email, "quiet-mail") { CarriesAttachments = false });
        IChannelAttachmentSupport support = services.GetRequiredService<IChannelAttachmentSupport>();

        Result<bool> push = await support.CarriesAttachmentsAsync(
            Channel.Push, CancellationToken.None);

        push.IsFailure.ShouldBeTrue();
        push.ErrorKind.ShouldBe(ResultErrorKind.Integration);
        push.Error!.ShouldContain("push");
    }

    private async Task ResetTableAsync()
    {
        await using DispatchDbContext context = fixture.CreateDbContext();
        await context.ProviderSelections.ExecuteDeleteAsync();
    }

    private async Task SeedAsync(params (string Channel, string ProviderKey, int Priority)[] rows)
    {
        await using DispatchDbContext context = fixture.CreateDbContext();
        foreach ((var channel, var providerKey, var priority) in rows)
        {
            Result<ProviderSelection> selection = ProviderSelection.Create(
                channel, providerKey, priority, DateTimeOffset.UtcNow);
            selection.IsSuccess.ShouldBeTrue(selection.Error);
            context.ProviderSelections.Add(selection.Value!);
        }

        await context.SaveChangesAsync();
    }
}
