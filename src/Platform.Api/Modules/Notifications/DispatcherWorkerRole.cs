using NotificationHub.Api.Composition;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Infrastructure.Messaging.Relay;
using NotificationHub.Api.Modules.Notifications.Features.Dispatching;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Consuming;
using NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications;

/// <summary>
/// Configuration of the dispatcher role: which channels and priority bands
/// this instance drains. Empty selects every hosted channel and every band;
/// a dedicated instance restricts to a subset. The drain priority never
/// changes: it lives in the slot allocation, not in these lists.
/// </summary>
public sealed class DispatcherWorkerOptions
{
    public const string SectionName = "Modules:Notifications:Dispatcher";

    /// <summary>Channel names among the hosted adapters; empty means all of them.</summary>
    public string[] Channels { get; init; } = [];

    /// <summary>Band names among auth, critical, transactional, operational; empty means all.</summary>
    public string[] Bands { get; init; } = [];
}

/// <summary>
/// Composition of the <c>dispatcher</c> worker role, owned by this module:
/// the dispatch queues of every configured channel and band consumed
/// concurrently with processing slots prioritized auth over critical over
/// transactional over operational, the optimistic claim over the attempt
/// state, the provider surface of the Dispatch context, the read and
/// lifecycle surfaces of ContactConsent and the release and capacity checks of
/// AttachmentManagement, each behind its published contract. The last two are
/// composed here and in no other role because this is the only role that
/// reaches a provider, and what they answer is only true for the instant
/// before it does.
/// It is also the only role that observes provider circuits, so it is the only
/// one composing the automatic channel stop.
/// </summary>
public sealed class DispatcherWorkerRole : IWorkerRoleModule
{
    public static string Role => "dispatcher";

    /// <summary>
    /// Channels whose adapters this role hosts, mirroring the provider
    /// surface it composes: SendGrid delivers e-mail, Twilio delivers SMS and
    /// FCM delivers push.
    /// A configured channel outside this set refuses to boot, because a
    /// dispatcher without an adapter would drain messages it can never send.
    /// </summary>
    internal static readonly string[] HostedChannels =
        [Channel.Email.Value, Channel.Sms.Value, Channel.Push.Value];

    /// <summary>Queue suffix of each band, in slot-priority order.</summary>
    internal static readonly (OutboxBand Band, string Suffix)[] BandSuffixes =
    [
        (OutboxBand.Auth, "auth"),
        (OutboxBand.Critical, "critical"),
        (OutboxBand.Transactional, "transactional"),
        (OutboxBand.Operational, "operational"),
    ];

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddPlatformMessaging(configuration);
        services.AddSqsMessageConsuming(configuration);
        services.AddEnvelopeEncryption(configuration);
        services.AddAuditTrailSurface();
        services.AddContactConsentReadSurface(configuration);
        services.AddContactConsentDeviceLifecycle(configuration);
        services.AddDispatchProviderSurface(configuration);
        services.AddAttachmentReleaseCheckSurface(configuration);
        services.AddNotificationsPersistence(configuration);
        services.AddNotificationsKillSwitch();
        services.AddNotificationsKillSwitchHolds();
        services.AddAutomaticChannelKillSwitch(configuration);

        services.AddScoped<AttemptDispatchWriter>();
        services.AddScoped<AttachmentPreflight>();
        services.AddScoped<IPoisonMessageSink, DispatchPoisonMessageSink>();

        services.AddSqsQueueConsumer<DispatchMessageProcessor>(QueueBindings(configuration));
    }

    /// <summary>
    /// The queues this instance drains: the product of the configured
    /// channels and bands, one queue per pair, ranked by the band's fixed
    /// position. An unknown band, an unknown channel, or a channel without a
    /// hosted adapter refuses to boot.
    /// </summary>
    internal static SqsQueueBinding[] QueueBindings(IConfiguration configuration)
    {
        DispatcherWorkerOptions options =
            configuration.GetSection(DispatcherWorkerOptions.SectionName).Get<DispatcherWorkerOptions>()
                ?? new DispatcherWorkerOptions();
        foreach (var name in options.Bands)
        {
            if (!OutboxBands.TryParseName(name, out _))
            {
                throw new InvalidOperationException(
                    $"Banda desconhecida '{name}' em '{DispatcherWorkerOptions.SectionName}:Bands'. "
                    + "Bandas conhecidas: auth, critical, transactional, operational.");
            }
        }

        var channels = ResolveChannels(options.Channels);
        OutboxBand[] selectedBands = OutboxBands.Restrict(options.Bands);
        return [.. BandSuffixes
            .Where(entry => selectedBands.Contains(entry.Band))
            .SelectMany(entry => channels.Select(channel =>
                new SqsQueueBinding($"dispatch-{channel}-{entry.Suffix}", (int)entry.Band)))];
    }

    /// <summary>
    /// The hosted channels this instance drains, in the hosting order and
    /// never in the configured one.
    /// <para>
    /// Every configured name passes through the vocabulary once, and only the
    /// canonical word it resolves to is compared from there on. Doing it in
    /// one place is what keeps the two comparisons below from disagreeing: a
    /// name the vocabulary accepts and the hosted list does not spell the same
    /// way would otherwise clear the guard and then fall out of the selection,
    /// and the instance would boot healthy draining fewer channels than the
    /// operator configured, without the empty binding list that refuses the
    /// boot. Failing closed with the wrong diagnosis is bad; booting open and
    /// silent is worse.
    /// </para>
    /// </summary>
    private static string[] ResolveChannels(string[] configured)
    {
        if (configured.Length == 0)
        {
            return HostedChannels;
        }

        var canonical = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in configured)
        {
            Result<Channel> channel = Channel.Create(name);
            if (channel.IsFailure)
            {
                throw new InvalidOperationException(
                    $"Canal desconhecido '{name}' em '{DispatcherWorkerOptions.SectionName}:Channels'. "
                    + $"Canais conhecidos: {string.Join(", ", Channel.All.Select(known => known.Value))}.");
            }

            var word = channel.Value!.Value;
            if (!HostedChannels.Contains(word, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"O canal '{name}' não possui adapter hospedado neste papel; "
                    + $"os canais hospedados são: {string.Join(", ", HostedChannels)}.");
            }

            canonical.Add(word);
        }

        return [.. HostedChannels.Where(canonical.Contains)];
    }
}
