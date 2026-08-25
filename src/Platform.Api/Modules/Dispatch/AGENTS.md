# Dispatch module

## Boundary

- Keep one bounded context in this module: provider adapters behind the
  published channel-provider contract, plus the resolution of which provider
  delivers each channel. It answers exactly one question per call: what did
  the provider say about this send.
- No attempt state lives here. Attempt rows, the queued-to-sending lock,
  fan-out over device tokens and fallback belong to the notification
  pipeline. This module neither reads nor writes their data.
- Delivery feedback is split at the same seam as sending. What only the
  provider knows, its signature scheme, its replay window, its event
  vocabulary, lives here behind a published contract. The route, the
  deduplication, the correlation with an attempt and the state machine live
  with the module that owns attempt state. This module never sees a
  notification identifier it did not receive from the provider.
- Do not read or write another context's data store, infrastructure types,
  or mutable domain types. Cross-context capability enters and leaves only
  through distinct, versioned contracts under
  `src/Platform.Api/Modules/Dispatch/Integration/V1/`; the channel
  vocabulary is consumed from the template-management published surface.

## Owned surfaces

| Path | Responsibility |
|---|---|
| `src/Platform.Api/Modules/Dispatch/Domain/` | provider selection row (channel, provider key, priority) |
| `src/Platform.Api/Modules/Dispatch/Integration/V1/` | `IChannelProvider`, `IChannelProviderResolver`, `DispatchRequest`, `DeliveryTarget` and `RenderedMessage` hierarchies, `ProviderResult`/`ProviderOutcome`, `IProviderWebhookInterpreter`, `IProviderWebhookInterpreterResolver`, `ProviderWebhookRequest`, `VerifiedProviderWebhook`, `ProviderDeliveryEvent`, `DeliveryFeedbackKind`, `SuppressionSignal`, `ProviderWebhookRefusal` |
| `src/Platform.Api/Modules/Dispatch/Infrastructure/Persistence/` | `DispatchDbContext` and migrations (schema `dispatch`, table `provider_config`) |
| `src/Platform.Api/Modules/Dispatch/Infrastructure/ProviderConfig/` | cached read of `provider_config`, channel-to-adapter resolution |
| `src/Platform.Api/Modules/Dispatch/Infrastructure/Providers/` | SendGrid, FCM and Twilio adapters, their webhook interpreters, error sanitization |
| `src/Platform.Api/Modules/Dispatch/Infrastructure/Webhooks/` | interpreter resolution, shared verification guards, suppression classification, registration |
| `src/Platform.Api/Modules/Dispatch/Infrastructure/Resilience/` | per-provider concurrency limiter, per-provider rate limit and its Redis, circuit-breaker options |

## Adapter contract

- An adapter translates one `DispatchRequest` into one provider call and the
  provider's answer into one normalized `ProviderResult`. It knows no
  policy, no fallback, no audit, no attempt state.
- `DispatchRequest` carries one optional correlation member
  (`DispatchCorrelation(NotificationId, AttemptId)`), a pure pass-through
  for webhook reconciliation: the SendGrid adapter writes both ids into
  `custom_args`, the Twilio adapter appends them to the callback address it
  gives the provider, the FCM adapter ignores the member, and the identifiers
  never enter the rendered content nor its audited hashes.
- `DispatchRequest.Application` names the calling application of the send. It
  exists for providers whose sending identity is allocated per application, a
  sender pool bound to one brand being the case in hand, and it is the same kind
  of pass-through the correlation is: it never enters the rendered content nor
  its audited hashes, and an adapter whose provider has no such notion ignores
  it. Null means the caller states nothing and the adapter uses the sending
  identity of the deployment.
- `DispatchRequest.Validity` states how long the message is still worth
  delivering, counted from the call. It is the remaining validity of the
  notification, computed by the caller that owns that state, and it reaches
  the provider as its own validity knob when the provider has one. Null means
  the caller states nothing and the provider keeps its default. Deciding not
  to call a provider at all is the caller's decision and never the adapter's:
  a validity that already ended is settled by the dispatcher, which is the
  only party that can end an attempt.
- Every provider verdict returns as a result; exceptions are reserved for
  caller defects (wrong channel in the request) and misconfiguration
  (missing API key, missing service account). Configuration guards fire at
  send time on purpose: environments without a channel still boot.
- Outcome mapping is honest: only explicit acceptance is `Accepted`; 4xx is
  `Rejected` except 429, which is `Throttled`; 5xx, timeout, network fault
  and open circuit are `TransientError`, because the provider gave no
  verdict and the message may or may not have arrived.
- FCM `UNREGISTERED` and `INVALID_ARGUMENT` come back as `Rejected` with the
  provider code preserved in `ErrorCode`: the caller invalidates the device
  token on exactly those codes.
- Provider error text passes `ProviderErrorSanitizer` before entering any
  result or log: address-like and long numeric tokens are masked, because
  providers occasionally echo the destination in validation messages.
- The e-mail preheader is part of the rendered shape, but Mail Send v3 has
  no preheader field; embedding it into the HTML belongs to the render
  stage. Adapters never rewrite content: the audited content hash must
  describe the exact bytes handed to the provider. SMS encoding follows the
  same rule for the same reason: composing accents, dropping control
  characters and flattening line breaks all happen at the render stage, and
  an adapter that normalized here would leave the trail describing a message
  nobody sent.

## SMS specifics

- The sender is a Messaging Service when one is configured, so the provider
  picks from the sender pool and keeps the sticky sender per destination.
  Without one the adapter keeps the single verified number, because that is
  what a local environment has; Programmable Messaging still requires one of
  the two.
- The pool is chosen per calling application and falls back twice:
  `MessagingServiceSids` maps an application to its own service, an application
  with no entry gets `MessagingServiceSid` of the deployment, and with neither
  the send keeps `FromNumber`. A pool carries the brand a recipient reads and
  the registration behind it, so a deployment serving more than one brand
  allocates one pool per application; a single-brand deployment configures none
  of the map and behaves exactly as before. The configuration guard follows the
  same resolution: what it demands is a sender for this send, not a specific
  one of the three.
- `StatusCallback` is built from a configured absolute address
  (`StatusCallbackUrl`) plus the correlation identifiers as query parameters
  named after the members of `DispatchCorrelation`. The provider echoes
  nothing back in the callback body, so the address it was given is the only
  place left to carry them, and the route that reads them binds by exactly
  those names. Empty configuration, or a request without correlation, sends
  no callback address at all: a callback nobody can tie to an attempt is
  feedback nobody can apply, and it would still cost this hub a retry loop.
- `ValidityPeriod` is the request's remaining validity in whole seconds,
  clamped to `MaxValidityPeriodSeconds`. The ceiling is configuration because
  the provider revises its own limit on its own schedule, and clamping beats
  refusing: a message that is still deliverable must not be lost to a number
  the provider would reject.
- Destination guards are configuration with the shipped values as defaults.
  `DestinationPattern` says the number is well formed and
  `AllowedCountryPrefixes` says this deployment may address that market at
  all. They stay separate because they answer different questions, and an
  explicitly empty prefix list turns the market guard off while the format
  guard remains. An unparsable pattern surfaces at send time, like every
  other configuration guard here.

## Delivery feedback contract

- An interpreter answers two questions about one provider and nothing else:
  did this provider send these bytes, and what does its dialect mean in the
  canonical vocabulary. Verification and interpretation are separate members
  because authentication happens before the endpoint and interpretation
  inside it; a verified webhook carries only the provider key, the instant of
  the proof and the bytes.
- Every refusal is a failed result whose error text is exactly one catalogue
  code: `signature-invalid`, `timestamp-out-of-window`, `origin-not-allowed`,
  `payload-unreadable`, `provider-unknown`. Never an exception, and never a
  code with an appended detail: everything an adapter could append there is
  personal data or attacker-supplied. `origin-not-allowed` stays separable
  because it means forgery and earns its own alarm, while `signature-invalid`
  is also the everyday symptom of a rotated secret.
- Gate order is origin, then payload readability, then replay window, then
  signature. The cheapest and loudest gate runs first so a later refusal
  cannot mask it.
- Signature comparison runs in fixed time. The Twilio scheme is HMAC-SHA1
  over the request URL plus the ordered form fields, which the provider
  dictates and the analyzer suppression names; the SendGrid scheme is an
  elliptic-curve signature over the timestamp plus the raw body.
- The replay window is mandatory for SendGrid, whose timestamp is inside the
  signed payload. Twilio message status callbacks carry no timestamp, so the
  window engages only when the configured field is present, and replay
  protection for that provider rests on deduplication by event identifier at
  the consumer.
- Missing verification secrets do not stop the host. `Verify` refuses with
  `signature-invalid` and logs the misconfiguration under its own event, so
  an environment without a channel still boots, exactly like the send side.
- The origin allowlist ships empty, which means off. Pinning provider ranges
  belongs first to the network edge, and a half-filled list inside the
  application drops authentic callbacks in silence.
- The vocabulary that turns a provider failure into a suppression signal is
  configuration, not a table compiled into this assembly, because suppressing
  a contact point is close to irreversible and providers revise their codes
  on their own schedule. An unset list keeps the shipped default; a
  configured list replaces it whole. Anything the lists do not name stays
  `SuppressionSignal.None`.
- The shipped SMS list names codes an operator could read as temporary (an
  unknown or unreachable handset, a landline or unreachable carrier), and
  that is deliberate: on this channel one signal suppresses nothing, because
  the ledger asks for two refusals inside seven days. A handset unreachable
  once is an outage; the same number refusing twice in a week is the
  destination. A test that expects suppression from the first SMS refusal is
  reading the e-mail rule and fails correctly.
- An unmapped word is handled differently per provider, on purpose. A Twilio
  status callback carries one status, so a word outside the vocabulary is a
  provider change and the callback is refused. A SendGrid batch mixes
  delivery events with engagement events, so an untracked word is dropped and
  any other unmapped word is reported, which is how a new delivery word
  becomes visible instead of vanishing.
- The canonical event carries no destination and no content: it is persisted
  and re-read as evidence. Twilio echoes no correlation identifiers in the
  callback body, so its events correlate through `ProviderMessageId` at the
  consumer; SendGrid echoes the identifiers it was given in `custom_args`.

## Resilience posture

- **No retry on a send.** A provider send is not idempotent: a retried POST
  can reach the same person twice. Redelivery of a failed attempt is the
  queue's decision. The only retried call in this module is the FCM OAuth
  token acquisition, which is idempotent at the endpoint and runs on its own
  named client.
- Per-provider named client, each with its own pipeline: circuit breaker
  outside, timeout inside, so timed-out attempts feed the breaker. Breaker
  and timeout knobs live in the provider options; tests lower them through
  configuration.
- Concurrency per provider is a local semaphore
  (`ConcurrencyLimitedChannelProvider`) wrapping each adapter registration.
- Rate per provider is a token bucket in Redis
  (`RateLimitedChannelProvider` over `ProviderRateLimiter`), shared by every
  instance, sized by the contracted rate with one second of burst. It wraps the
  concurrency limiter and not the other way round: a send with no budget must
  not first wait for a slot it is about to give back. A refusal comes back as
  `Throttled` with the code `rate-limited`, never as a rejection, because this
  hub decided not to call and the provider said nothing; the caller settles it
  like any throttle, and the code is what lets it tell our own congestion from
  the provider's.
- The bucket fails open with an alarm on any Redis failure, the same posture the
  ingestion limits hold: a control that blocks sends when its store is
  unreachable stops a channel for a reason the provider never gave, and the kill
  switch is the compensation that is meant to stop channels. A provider with no
  configured rate is not measured at all, and a section with rates but no store
  refuses to boot, because a limit that never measures anything is worse than a
  declared absence of limits.

## Provider configuration

- Table `dispatch.provider_config(channel, provider_key, priority,
  updated_at)`, primary key `(channel, provider_key)`, lowest priority wins.
  The canonical form is declarative data in the infrastructure repository; a
  deploy job materializes the table. The application only reads it, through
  a snapshot cached for sixty seconds (`Modules:Dispatch:ProviderConfig`),
  so a provider change lands without a deploy. When a refresh fails and an
  older snapshot exists, the older snapshot keeps serving.
- Resolution joins the configured key with the adapters hosted in this
  process and fails as an integration error on any mismatch.

## Testing rules

- **FCM has no sandbox.** Every FCM test, in every suite, talks to a fake
  HTTP server; there is no gated real-FCM suite and none may be added.
- SendGrid sandbox mode exists and the adapter supports it
  (`SandboxMode`, enabled by default; production overrides it explicitly),
  but the manually gated suite against the real sandbox is not part of this
  phase.
- CI provider tests run against an in-process HTTP fake asserting the
  request contract (payload shape, authorization header) and the outcome
  mapping; Postgres-backed tests cover `provider_config` reads and cache
  expiry with a controllable `TimeProvider`.
- **Provider signatures are tested by vector, never against a live provider.**
  The Twilio recipe is pinned by the vector the provider publishes, recomputed
  independently before it entered the suite. The SendGrid key pair and
  signature are minted inside the test, so no secret is committed and no
  external vector has to stay reachable. Every window and vocabulary
  assertion signs its own input, so it can never pass because the signature
  gate refused first.

## Error axis and logging

- The published resolvers return the repository result type; adapters return
  `ProviderResult` and interpreters return catalogue refusals. Loggers follow
  the repository dialect:
  `*.Logger.cs` files with source-generated extension methods, identifiers
  in English, message text in pt-BR, never personal data, destinations or
  rendered content in placeholders.

Update this file in the same change that alters the module boundary, the
published contracts, the outcome mapping, or the resilience posture.
