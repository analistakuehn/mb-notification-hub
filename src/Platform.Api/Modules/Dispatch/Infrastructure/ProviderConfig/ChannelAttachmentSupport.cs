using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.ProviderConfig;

/// <summary>
/// Answers the attachment question off the adapter the send would reach,
/// resolved through the same configuration the send resolves it through.
/// <para>
/// There is nothing else here on purpose. The whole value of this type is that
/// it holds no answer of its own: it forwards the question to the object that
/// composes the provider call, so a channel repointed at another adapter
/// changes what a planner reads on the same deployment act that changes what a
/// sender does.
/// </para>
/// </summary>
internal sealed class ChannelAttachmentSupport(IChannelProviderResolver resolver)
    : IChannelAttachmentSupport
{
    public async Task<Result<bool>> CarriesAttachmentsAsync(
        Channel channel,
        CancellationToken cancellationToken)
    {
        Result<IChannelProvider> provider = await resolver.ResolveAsync(channel, cancellationToken);
        return provider.IsFailure
            ? new Result<bool>(false, default, provider.ErrorKind, provider.Error)
            : Result.Success(provider.Value!.CarriesAttachments);
    }
}
