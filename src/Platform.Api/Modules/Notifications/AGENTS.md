# Notifications module

## Boundary

- Keep one bounded context in this module: the notification lifecycle, from
  ingestion to dispatch. This unit ships the REST ingestion, the Core
  pipeline that consumes the `core-*` queues, and the dispatch slice: the
  `dispatch-*` consumers, the attempt state machine from `queued` on, the
  push fan-out and the fallback handler. The query API arrives in a later
  slice.
- Keep invariants in the entities and pure functions under
  `src/Platform.Api/Modules/Notifications/Domain/`.
- Keep use-case orchestration in the slices under
  `src/Platform.Api/Modules/Notifications/Features/`.
- Read sibling contexts exclusively through their published contracts:
  `Modules.TemplateManagement.Integration.V1` (published catalog, variables
  validation, renderer and the policy rule contract),
  `Modules.ContactConsent.Integration.V1` (recipient directory, contact and
  token reveal, device-token lifecycle),
  `Modules.Dispatch.Integration.V1` (channel providers and their resolution)
  and `Modules.Audit.Integration.V1` (transactional audit append). Never
  touch another context's data store or internal types.
- Platform infrastructure is a dependency, not a sibling: the outbox writer
  (`NotificationHub.Api.Infrastructure.Messaging.IOutboxWriter`), the SQS
  consuming surface (`NotificationHub.Api.Infrastructure.Messaging.Consuming`),
  the envelope cipher
  (`NotificationHub.Api.Infrastructure.Cryptography.IEnvelopeCipher`)
  and the partition provisioning live outside `Modules.*` and never reference
  module types back.

## Owned surfaces

| Path | Responsibility |
|---|---|
| `src/Platform.Api/Modules/Notifications/Domain/` | notification, attempt, policy evaluation and idempotency entities, class vocabulary, canonical JSON, variables masking, public id form |
| `src/Platform.Api/Modules/Notifications/Features/` | vertical slices for this context: the Core pipeline under `Features/Pipeline/`, the dispatch consumer under `Features/Dispatching/`, the fallback handler under `Features/Fallback/` |
| `src/Platform.Api/Modules/Notifications/Infrastructure/` | persistence (schema `notifications`), Redis controls, template gate, privacy, partition manager, purge job, pipeline commit writer, attempt dispatch writer, poison sinks |
| `src/Platform.Api/Modules/Notifications/NotificationsModule.cs` | service registration and endpoint mapping for this context |
| `src/Platform.Api/Modules/Notifications/CoreWorkerRole.cs` | composition of the `core` worker role, discovered by the worker host |
| `src/Platform.Api/Modules/Notifications/DispatcherWorkerRole.cs` | composition of the `dispatcher` worker role, discovered by the worker host |

Owned state: `notification`, `notification_attempt` and `policy_evaluation`
(monthly partitioned parents), `idempotency_key`. The platform `outbox` and
`processed_messages` belong to the messaging infrastructure; this module only
writes through their contracts, on its own transaction.

## Transactional invariant of the ingestion

Accepting a request commits four writes in one database transaction or none:
the `notification` row, the `idempotency_key` row, the `platform.outbox`
message, and the `audit_event` appended through `IAuditTrail` with the raw
`DbTransaction`. The audit append holds the partition chain lock until the
transaction ends, so the commit follows it immediately
(`Infrastructure/Persistence/IngestionWriter.cs`). A rejection or duplicate
has no business effect and records its trail in its own short transaction.

## Idempotency contract

- Scope `(application, idempotency_key)`; the authority is the primary key of
  `idempotency_key`, never the Redis fast path.
- Replay inside 24 h with the same canonical payload hash answers 200 with the
  original notification id; the same key with a different hash answers 409.
- The Redis entry (`idem:{application}:{key}`, TTL 24 h) is written only after
  the commit; an absent or malformed entry is a miss and the database decides.
- The purge job (`Modules:Notifications:IdempotencyPurge`) removes
  registrations older than 24 h, so a replay beyond the window creates a new
  notification on purpose.
- The canonical payload hash is documented on
  `Features/Mutations/RequestNotification/RequestNotification.PayloadHash.cs`.

## Core pipeline

- One mutable `NotificationContext` crosses the ordered stage list Validate,
  Resolve, Policy, Render, Route; the commit at the end writes everything the
  run produced in one database transaction: the notification transition, the
  first attempt (`queued`, `fallback_deadline` stamped at enqueue time), the
  `policy_evaluation` rows, the outbox message to
  `dispatch-{channel}-{class}` or `dispatch-{channel}-auth` (claim check
  `{notificationId, attemptId}`), the `audit_event` through `IAuditTrail`,
  and the consumer dedupe mark in `platform.processed_messages`
  (`Infrastructure/Persistence/PipelineCommitWriter.cs`).
- Business rejection is an explicit stage outcome with a stable reason, never
  an exception; an unexpected exception propagates, the message returns to the
  queue with backoff, and only the redrive policy reaches the DLQ. A missing
  published class policy is an operational failure, not a rejection.
- Notification states written by the commit: `dispatched` (attempt#1 queued),
  `rejected` (policy or validation decision), `expired` (TTL over), and
  `deferred` (`release_at` set, run parked; the releaser arrives in a later
  slice). `variables_enc` is purged on `rejected` and `expired`; `deferred`
  keeps it sealed because the pipeline resumes from there.
- Policy stage: `IPolicyRule<NotificationContext>` implementations under
  `Features/Pipeline/Rules/`, in fixed v1 order `ConsentGate`, `QuietHours`,
  `DedupeWindow`, `ChannelSelection`. Every rule records one
  `policy_evaluation` row with compact JSON evidence; canonical rejection
  reasons: `no-consent`, `duplicate-window`, `no-valid-contact`. A hard guard
  in code keeps critical and authentication flows out of any deferral.
- The dedupe barrier is Redis `SET NX` keyed by
  `(application, templateKey, recipientId)` with the policy window as TTL and
  the notification id as value, so a redelivery recognizes its own mark. Redis
  failure fails open with the fail-open recorded in the evidence.
- The `core` worker role consumes the four `core-*` queues concurrently with
  processing slots prioritized `auth > critical > transactional >
  operational`; an optional band restriction mirrors the relay
  (`Modules:Notifications:CoreWorker:Bands`, empty = all).

## Dispatch slice

- The `dispatcher` worker role consumes the product of its configured
  channels and bands over the `dispatch-{channel}-{band}` queues, with the
  same slot priority as the core role (`auth > critical > transactional >
  operational`). The composition of this phase hosts only e-mail (SendGrid)
  and push (FCM); a configured channel without a hosted adapter refuses to
  boot.
- Ownership of an attempt is an optimistic lock: `queued -> sending` through
  `UPDATE ... WHERE status = 'queued'`, stamping `provider_key`. Every later
  transition is guarded by the expected stored status. The consumer dedupe
  mark commits with the verdict, never with the claim: a send is not
  idempotent, so a redelivery resolves on the stored status and never
  reaches the provider again. A crash between claim and verdict parks the
  attempt on `sending` for the reconciliation of a later phase.
- Push fan-out expands at claim time, in the claim transaction: the claimed
  attempt is stamped with the most recent active token and one sibling per
  remaining token (five in total, at most, by `last_seen_at`) is inserted
  already queued, copying the sealed content, the hashes and the step's
  absolute `fallback_deadline`, each announced to the same queue via outbox.
  `sequence` is the monotonic creation order per notification. Zero active
  tokens at claim fail the attempt with `no-active-device-token`.
- Verdicts: `Accepted` lands on `sent` (push: the first sibling accepted
  also lands the notification on `delivered`); `Rejected` lands on `failed`
  and, when the failure exhausts the step (push: no sibling succeeded and
  every other sibling already failed), advances the plan in the same
  transaction: a step with a deadline emits `FallbackRequested` to
  `core-{class}` plus the `fallback.triggered` trail, and the last step
  fails the notification; `Throttled` and an open circuit revert to
  `queued` and postpone the message honoring `RetryAfter`; any other
  transient error parks the attempt on `unknown`, which does not progress
  in this phase.
- The fallback handler runs inside the core role: it verifies the TTL
  (expired ends the notification on `expired`), finds the step after the
  failed channel in the published plan, renders the next channel and queues
  the next attempt with the pipeline's transactional invariant. A next step
  without content, contact or plan entry fails the notification with a
  stable reason.
- PII at send time only: the sealed render is opened in memory, the e-mail
  address comes from `RevealContactValueAsync` and the push token from
  `RevealDeviceTokenAsync`, both transient. FCM `UNREGISTERED` and
  `INVALID_ARGUMENT` report the dead token through the ContactConsent
  lifecycle contract after the verdict commits; the report is best effort
  and idempotent on the owning side.

## Variables and PII

- `variables_masked` (jsonb, mandatory) stores the canonical variables object
  with every variable listed in the template's `SensitiveVariables` masked to
  `***`; it is the only plaintext projection ever stored.
- `variables_enc` (bytea, nullable) stores the envelope-encrypted canonical
  form of the **whole** variables object, sealed by the platform envelope
  cipher with the data key of the application.
- **Purge ownership**: the Core pipeline commit purges `variables_enc` on the
  reachable terminal states `rejected` and `expired`, in the same transaction
  as the transition. The terminals of the dispatch side (`delivered`,
  `failed`) purge in a later phase; `deferred` keeps the ciphertext because
  the pipeline resumes from there.
- No recipient existence check happens at ingestion (anti-enumeration): the
  API answers 202 whether or not the recipient exists.

## Rate limiting

- Two Redis-backed dimensions, both keyed by canonical class: per producer
  principal and per recipient (`Modules:Notifications:RateLimits`); exceeding
  answers 429 with `Retry-After`, and the recipient dimension also records
  `notification.rejected_at_ingress` with reason `recipient-rate-limited`.
- Every Redis failure fails open with an alarm log: availability prevails and
  the manual kill switch is the compensation. The named ASP.NET policy on the
  endpoint is only a coarse in-process backstop.

## Partitioning

- `notification` (on `created_at`), `notification_attempt` (on `created_at`)
  and `policy_evaluation` (on `evaluated_at`) are partitioned by month; each
  creation migration provisions the initial partitions and the module
  scheduler (`Modules:Notifications:PartitionManager`) keeps future months
  provisioned for the three tables through the platform provisioner. Health
  checks: `notifications-partitions`, `notifications-attempt-partitions`,
  `notifications-policy-evaluation-partitions`.
- Never revoke writes on this module's partitions: write revoke is a closing
  semantic exclusive to the Audit trail.
- `idempotency_key` stays outside the partitioning so its unique key exists.

## Audit vocabulary

Ingestion actions follow the platform dot vocabulary: `notification.accepted`
(details carry `source = rest`), `notification.duplicate`,
`notification.rejected_at_ingress` (details carry the stable reason). Actor
type is `producer` with the token identity (`appid`/`oid`) as actor id. The
pipeline adds `notification.dispatched`, `notification.rejected`,
`notification.deferred`, `notification.expired`, `notification.duplicate` and
`message.discarded`, with actor type `system` and actor id `core-worker`
(`Infrastructure/Auditing/PipelineAuditVocabulary.cs`). The dispatch side adds
`fallback.triggered`, `notification.delivered`, `notification.failed` and
`fallback.attempt_queued`, with actor id `dispatcher` for the dispatcher's
own decisions and `core-worker` for the fallback handler
(`Infrastructure/Auditing/DispatchingAuditVocabulary.cs`). The constants live
module-locally; promoting them into the Audit `Integration/V1` vocabulary is
a pending cross-module decision.

## Error axis

- Handlers return `Result<T>`; every ingestion outcome, including rejections,
  is data inside the response union
  (`Features/Mutations/RequestNotification/RequestNotification.Response.cs`),
  and the endpoint maps each case to RFC 9457 problems
  (`Infrastructure/Http/IngestionProblems.cs`). Problem `type` values are
  stable codes: `idempotency-key-required`, `idempotency-key-conflict`,
  `class-not-allowed-for-principal`, `rate-limit-exceeded`,
  `template-not-found`, `template-class-mismatch`,
  `template-variables-invalid`, plus the catalog reasons
  `template-deprecated` and `template-disabled`.

## Security and tests

- The route requires a send role; the class-level check runs against the
  resource in the use case, because the class arrives in the body.
- Never bind HTTP bodies to domain types; never log variables, recipient
  contact data, tokens or rendered content. Log identifiers only.
- Start with a failing behavior test; keep the transactional invariant, the
  idempotency contract and the fail-open behavior covered by integration
  tests.

Update this file in the same change that alters the module boundary, the
transactional invariant, the idempotency contract, or the PII rules.
