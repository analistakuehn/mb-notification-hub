using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Resilience;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;

public sealed class TwilioOptions : IValidatableObject
{
    public const string SectionName = "Modules:Dispatch:Providers:Twilio";

    /// <summary>
    /// Destination format the adapter ships with: a Brazilian mobile number in
    /// E.164. It is the shape of the only market this phase sends to, and it is
    /// a default rather than a constant so opening a second market is a
    /// configuration change instead of a deploy of this assembly.
    /// </summary>
    internal const string DefaultDestinationPattern = @"^\+55\d{10,11}$";

    /// <summary>
    /// Longest validity this adapter asks the provider to hold a queued
    /// message for. Twilio revises its own ceiling on its own schedule, so the
    /// shipped value is the conservative bound and an operator raises it
    /// without waiting for a release.
    /// </summary>
    internal const int DefaultMaxValidityPeriodSeconds = 14_400;

    private static readonly string[] DefaultAllowedCountryPrefixes = ["+55"];

    private readonly Lazy<Regex> _destinationExpression;

    private readonly Lazy<IReadOnlyDictionary<string, string>> _messagingServiceSidsByApplication;

    public TwilioOptions()
    {
        _destinationExpression = new Lazy<Regex>(() => new Regex(
            DestinationPattern,
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(1)));
        _messagingServiceSidsByApplication = new Lazy<IReadOnlyDictionary<string, string>>(
            () => new Dictionary<string, string>(MessagingServiceSids, StringComparer.OrdinalIgnoreCase));
    }

    [Required]
    [Url]
    public string BaseAddress { get; init; } = "https://api.twilio.com";

    /// <summary>
    /// Twilio product the SMS call is built for. The two products carry
    /// different delivery contracts: only Programmable Messaging accepts a
    /// Messaging Service, a per-message status callback and a validity period,
    /// so it is the default. Verify sends the code and reports nothing back,
    /// which is usable for a test account but leaves the delivery tracker
    /// without any event to close the notification with.
    /// </summary>
    public TwilioSmsProduct Product { get; init; } = TwilioSmsProduct.ProgrammableMessaging;

    /// <summary>
    /// Refuses to start the SMS role in a configuration that can send and can
    /// never confirm.
    /// <para>
    /// The failure it guards is silent in the worst way: with the Verify
    /// product, or with no callback address, the message goes out and no
    /// delivery event ever comes back, so the tracker never closes the
    /// notification, the fallback never learns the send worked and the
    /// end-to-end proof this phase rests on cannot reproduce in production.
    /// Nothing errors; the confirmation simply never arrives.
    /// </para>
    /// <para>
    /// It is off by default and turned on where it matters, rather than the
    /// other way round, because a local host with one verified test number and
    /// no public address is a legitimate configuration and refusing it would
    /// make the guard the first thing anybody switches off.
    /// </para>
    /// </summary>
    public bool RequireDeliveryFeedback { get; init; }

    /// <summary>Credential family used to authenticate the provider call.</summary>
    public TwilioAuthenticationMode AuthenticationMode { get; init; } = TwilioAuthenticationMode.AuthToken;

    /// <summary>Twilio account identifier used by the route or Auth Token mode.</summary>
    public string AccountSid { get; init; } = "";

    /// <summary>API key SID used as the Basic authentication username in API key mode.</summary>
    public string ApiKeySid { get; init; } = "";

    /// <summary>Auth Token or API key secret supplied by the local secret store.</summary>
    public string CredentialSecret { get; init; } = "";

    /// <summary>Verified Twilio number used by Programmable Messaging.</summary>
    public string FromNumber { get; init; } = "";

    /// <summary>
    /// Messaging Service that owns the sender pool of this deployment. Set, it
    /// replaces the single sender number: the provider picks the sender, keeps
    /// the sticky sender per destination and applies the service's own
    /// compliance rules. Empty keeps the single-number path, so an environment
    /// with one verified test number still sends.
    /// </summary>
    public string MessagingServiceSid { get; init; } = "";

    /// <summary>
    /// Messaging Service per calling application, keyed as the hub names the
    /// application. A pool carries the brand a recipient sees and the
    /// compliance registration behind it, so a deployment that serves more
    /// than one brand allocates one pool per application and this map is where
    /// the allocation lands. An application without an entry falls back to the
    /// deployment-wide service, and then to the single verified number, which
    /// is what keeps a local environment and a single-brand deployment
    /// unchanged.
    /// </summary>
    public Dictionary<string, string> MessagingServiceSids { get; init; } = [];

    /// <summary>
    /// Absolute public address of this hub's callback route for this provider,
    /// for example <c>https://hooks.example.com/webhooks/twilio</c>. The
    /// adapter appends the correlation identifiers of the attempt to it,
    /// because this provider echoes nothing back in the callback body and the
    /// address it was given is the only place left to carry them. Empty sends
    /// no callback address, which is the correct local behaviour: an
    /// unreachable address would only make the provider retry against nothing.
    /// </summary>
    public string StatusCallbackUrl { get; init; } = "";

    /// <summary>Twilio Verify service used when the product is Verify.</summary>
    public string ServiceSid { get; init; } = "";

    /// <summary>
    /// International prefixes the adapter accepts as destinations. Unset keeps
    /// the shipped list; a configured list replaces it whole, so a market can
    /// be opened or retired without a deploy. An explicitly empty list turns
    /// the prefix guard off and leaves only the format guard, in the same
    /// regime the callback origin allowlist follows.
    /// </summary>
    public string[]? AllowedCountryPrefixes { get; init; }

    /// <summary>
    /// Regular expression every destination must match before the adapter
    /// calls the provider. It is the cheapest guard against a malformed number
    /// consuming an SMS, and it is configuration because the shape of a valid
    /// number is a property of the market, not of this code. An unparsable
    /// pattern surfaces at send time, in the same regime as every other
    /// configuration guard of this module.
    /// </summary>
    [Required]
    public string DestinationPattern { get; init; } = DefaultDestinationPattern;

    /// <summary>
    /// Ceiling, in seconds, of the validity period asked of the provider. A
    /// remaining validity longer than this is sent as this value, because the
    /// provider refuses anything above its own limit and refusing the send
    /// here would cost a notification that is still perfectly deliverable.
    /// </summary>
    [Range(1, 86_400)]
    public int MaxValidityPeriodSeconds { get; init; } = DefaultMaxValidityPeriodSeconds;

    /// <summary>
    /// Half-width of the time window the delivery lookup searches by
    /// destination, in seconds, centred on the instant this hub handed the
    /// message over. It is the whole of what separates the message being asked
    /// about from the next message to the same person, so it is narrow by
    /// default: widening it buys nothing, because a window that matches more
    /// than one message makes the adapter conclude nothing at all.
    /// </summary>
    [Range(30, 86_400)]
    public int LookupWindowSeconds { get; init; } = 300;

    /// <summary>
    /// How many messages the search route asks for. Above one only so that an
    /// ambiguous window is visible as ambiguous: a page of one would hide the
    /// second match and let a wrong message settle an attempt.
    /// </summary>
    [Range(2, 100)]
    public int LookupPageSize { get; init; } = 5;

    /// <summary>
    /// How long one provider call may take before it is abandoned.
    /// <para>
    /// The knob is per provider and the timeout budget of the design is per
    /// notification class, and the two do not line up: the resilience pipeline
    /// is composed once per provider and never per queue, so a class never
    /// reaches this decision. The gap is closed by what this channel is used
    /// for rather than by making the knob finer. SMS exists in the delivery
    /// plan as the fallback of critical, so every call this provider makes is a
    /// critical call, and the value is the critical budget rather than the one
    /// for everything else.
    /// </para>
    /// <para>
    /// It is also a term of the fallback arithmetic: the step deadline, the
    /// scheduler interval, the two queue hops and this timeout together have to
    /// stay inside the accepted time to a fallback SMS, and this is the term
    /// that used to push the sum past it.
    /// </para>
    /// </summary>
    [Range(1, 120)]
    public int TimeoutSeconds { get; init; } = 2;

    /// <summary>Maximum simultaneous SMS sends against Twilio from this host.</summary>
    [Range(1, 1_000)]
    public int MaxConcurrency { get; init; } = 4;

    public ProviderCircuitBreakerOptions CircuitBreaker { get; init; } = new();

    internal IReadOnlyList<string> EffectiveAllowedCountryPrefixes
        => AllowedCountryPrefixes ?? DefaultAllowedCountryPrefixes;

    /// <summary>
    /// The Messaging Service that owns the sender of one send: the pool of the
    /// calling application when it has one, the pool of the deployment
    /// otherwise, and none at all when neither is configured, which leaves the
    /// single verified number as the sender.
    /// </summary>
    internal string? MessagingServiceSidFor(string? application)
    {
        if (!string.IsNullOrWhiteSpace(application)
            && _messagingServiceSidsByApplication.Value.TryGetValue(application, out var perApplication)
            && !string.IsNullOrWhiteSpace(perApplication))
        {
            return perApplication;
        }

        return string.IsNullOrWhiteSpace(MessagingServiceSid) ? null : MessagingServiceSid;
    }

    internal Regex DestinationExpression => _destinationExpression.Value;

    /// <summary>
    /// Validates the nested circuit-breaker knobs, which the registration of
    /// this type does not reach on its own (their ranges would read as enforced
    /// and never be evaluated, letting an out-of-range threshold reach the
    /// pipeline at runtime), plus the delivery-feedback contract when the
    /// deployment declares that it needs one.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (ValidationResult result in NestedOptionsValidation.Validate(
            CircuitBreaker, nameof(CircuitBreaker)))
        {
            yield return result;
        }

        if (!RequireDeliveryFeedback) yield break;

        if (Product != TwilioSmsProduct.ProgrammableMessaging)
        {
            yield return new ValidationResult(
                $"O produto '{Product}' não devolve evento de entrega; com retorno de entrega "
                + "exigido, o canal SMS precisa de Programmable Messaging.",
                [nameof(Product)]);
        }

        if (string.IsNullOrWhiteSpace(StatusCallbackUrl))
        {
            yield return new ValidationResult(
                "Sem endereço de callback o provedor não tem para onde reportar a entrega.",
                [nameof(StatusCallbackUrl)]);
        }

        if (string.IsNullOrWhiteSpace(MessagingServiceSid) && MessagingServiceSids.Count == 0)
        {
            yield return new ValidationResult(
                "Nenhum Messaging Service configurado; o pool de sender por aplicação não existe.",
                [nameof(MessagingServiceSid)]);
        }
    }
}

public enum TwilioSmsProduct
{
    ProgrammableMessaging = 1,
    Verify = 2,
}

public enum TwilioAuthenticationMode
{
    ApiKey = 1,
    AuthToken = 2,
}
