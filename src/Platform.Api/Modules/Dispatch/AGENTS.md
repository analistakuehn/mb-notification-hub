# Dispatch module

## Boundary

- Keep one bounded context in this module: provider adapters behind the
  published channel-provider contract, plus the resolution of which provider
  delivers each channel. It answers exactly one question per call: what did
  the provider say about this send.
- No attempt state lives here. Attempt rows, the queued-to-sending lock,
  fan-out over device tokens and fallback belong to the notification
  pipeline; webhooks and reconciliation belong to delivery tracking. This
  module neither reads nor writes their data.
- Do not read or write another context's data store, infrastructure types,
  or mutable domain types. Cross-context capability enters and leaves only
  through distinct, versioned contracts under
  `src/Platform.Api/Modules/Dispatch/Integration/V1/`; the channel
  vocabulary is consumed from the template-management published surface.

## Owned surfaces

| Path | Responsibility |
|---|---|
| `src/Platform.Api/Modules/Dispatch/Domain/` | provider selection row (channel, provider key, priority) |
| `src/Platform.Api/Modules/Dispatch/Integration/V1/` | `IChannelProvider`, `IChannelProviderResolver`, `DispatchRequest`, `DeliveryTarget` and `RenderedMessage` hierarchies, `ProviderResult`/`ProviderOutcome` |
| `src/Platform.Api/Modules/Dispatch/Infrastructure/Persistence/` | `DispatchDbContext` and migrations (schema `dispatch`, table `provider_config`) |
| `src/Platform.Api/Modules/Dispatch/Infrastructure/ProviderConfig/` | cached read of `provider_config`, channel-to-adapter resolution |
| `src/Platform.Api/Modules/Dispatch/Infrastructure/Providers/` | SendGrid and FCM adapters, error sanitization |
| `src/Platform.Api/Modules/Dispatch/Infrastructure/Resilience/` | per-provider concurrency limiter, circuit-breaker options |

## Adapter contract

- An adapter translates one `DispatchRequest` into one provider call and the
  provider's answer into one normalized `ProviderResult`. It knows no
  policy, no fallback, no audit, no attempt state.
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
  describe the exact bytes handed to the provider.

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

## Error axis and logging

- The published resolver returns the repository result type; adapters
  return `ProviderResult`. Loggers follow the repository dialect:
  `*.Logger.cs` files with source-generated extension methods, identifiers
  in English, message text in pt-BR, never personal data, destinations or
  rendered content in placeholders.

Update this file in the same change that alters the module boundary, the
published contracts, the outcome mapping, or the resilience posture.
