using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Dispatch.Domain;

/// <summary>
/// One row of the materialized provider configuration: which provider
/// delivers a channel, with a priority that already accommodates future
/// failover (lowest wins). The canonical form of this data lives in the
/// infrastructure repository; a deploy job materializes it into the table and
/// the application only ever reads it at runtime.
/// </summary>
public sealed class ProviderSelection
{
    private ProviderSelection(string channelValue, string providerKey, int priority, DateTimeOffset updatedAt)
    {
        ChannelValue = channelValue;
        ProviderKey = providerKey;
        Priority = priority;
        UpdatedAt = updatedAt;
    }

    // EF Core materialization: fields are populated from the store.
    private ProviderSelection()
    {
        ChannelValue = null!;
        ProviderKey = null!;
    }

    /// <summary>Canonical channel value (see the published channel vocabulary).</summary>
    public string ChannelValue { get; }

    public string ProviderKey { get; }

    /// <summary>Selection order within a channel; the lowest value wins.</summary>
    public int Priority { get; }

    public DateTimeOffset UpdatedAt { get; }

    public static Result<ProviderSelection> Create(
        string? channel,
        string? providerKey,
        int priority,
        DateTimeOffset updatedAt)
    {
        Result<Channel> canonicalChannel = Channel.Create(channel);
        if (canonicalChannel.IsFailure)
        {
            return new Result<ProviderSelection>(
                false, default, canonicalChannel.ErrorKind, canonicalChannel.Error);
        }

        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return Result.ValidationError<ProviderSelection>("Provider key must not be empty.");
        }

        if (priority < 0)
        {
            return Result.ValidationError<ProviderSelection>("Priority must not be negative.");
        }

        return Result.Success(new ProviderSelection(
            canonicalChannel.Value!.Value, providerKey.Trim(), priority, updatedAt));
    }
}
