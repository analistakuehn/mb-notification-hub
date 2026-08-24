# Notifications module

## Boundary

- Keep one bounded context in this module: the notification lifecycle, from
  ingestion to dispatch. This unit ships the REST ingestion, the bus ingress
  that consumes `notifications.requested.v1`, the Core pipeline that consumes
  the `core-*` queues, the dispatch slice (the `dispatch-*` consumers, the
  attempt state machine from `queued` on, the push fan-out and the fallback
  handler), the outgoing result events on `notifications.events.v1`, and the
  read-only query API behind `Notifications.Read`. The audit API
  (`/v1/audit/*`) stays outside this unit: rendered content and full contact
  leave through there, never through the query surface.
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
| `src/Platform.Api/Modules/Notifications/Features/` | vertical slices for this context: the Core pipeline under `Features/Pipeline/`, the dispatch consumer under `Features/Dispatching/`, the fallback handler under `Features/Fallback/`, the query slices under `Features/Queries/` |
| `src/Platform.Api/Modules/Notifications/Infrastructure/` | persistence (schema `notifications`, write and read-only contexts), Redis controls, template gate, privacy, partition manager, purge job, pipeline commit writer, attempt dispatch writer, poison sinks, query transport helpers and the history reader |
| `src/Platform.Api/Modules/Notifications/NotificationsModule.cs` | service registration and endpoint mapping for this context |
| `src/Platform.Api/Modules/Notifications/Integration/V1/` | published contract of this context: the canonical rejection-reason catalog |
| `src/Platform.Api/Modules/Notifications/CoreWorkerRole.cs` | composition of the `core` worker role, discovered by the worker host |
| `src/Platform.Api/Modules/Notifications/DispatcherWorkerRole.cs` | composition of the `dispatcher` worker role, discovered by the worker host |
| `src/Platform.Api/Modules/Notifications/KafkaIngressWorkerRole.cs` | composition of the `kafka-ingress` worker role, discovered by the worker host |
| `src/Platform.Api/Modules/Notifications/NotificationsMaintenanceWorkerRole.cs` | composition of the `notifications-maintenance` worker role, discovered by the worker host |

Owned state: `notification`, `notification_attempt` and `policy_evaluation`
(monthly partitioned parents), `idempotency_key`, `producer_registry`. The
platform `outbox` and `processed_messages` belong to the messaging
infrastructure; this module only writes through their contracts, on its own
transaction.

## Transactional invariant of the ingestion

Accepting a request commits four writes in one database transaction or none:
the `notification` row, the `idempotency_key` row, the `platform.outbox`
message, and the `audit_event` appended through `IAuditTrail` with the raw
`DbTransaction`. The audit append holds the partition chain lock until the
transaction ends, so the commit follows it immediately
(`Infrastructure/Persistence/IngestionWriter.cs`). A rejection or duplicate
has no business effect and records its trail in its own short transaction.

## One ingestion use case, two transports

- `Features/Mutations/RequestNotification/` is neutral to the transport. It
  receives an authorization question already answered, the origin of the
  request, and the idempotency key; it answers with data. Every rejection is a
  legitimate outcome, so the route maps it to an RFC 9457 problem and the bus
  ingress maps it to a dead-letter record without either re-implementing a
  rule. The shape validation runs inside the use case, first, so an unreadable
  request is answered for what it is even when the producer would also fail
  authorization. The route carries **no** validation filter: answering ahead of
  the use case would refuse the same defect with the framework body here and
  with `payload-invalid` on the bus, and would leave the synchronous refusal
  without a trail and without a rejection event. The published 400 keeps the
  per-field `errors` dictionary and gains the catalog code as its `type`. The
  accepted consequence is the order: a malformed body without
  `Idempotency-Key` is answered for the missing key first, because the trail
  needs the key for the identity of the entity it records.
- Authorization is resolved per transport before the use case runs:
  `RestProducerAuthorizer` over the Entra app roles (reason
  `class-not-allowed-for-principal`) and `KafkaProducerAuthorizer` over
  `producer_registry` (reason `producer-not-authorized`).
- The trail of an outcome without a business effect is written through
  `IIngestionSink`. The synchronous posture commits it immediately; the bus
  posture holds it until the dead-letter record exists, then commits it with
  the deduplication mark.

## Producer registry

- `producer_registry(principal, application, class, updated_at)` grants one bus
  principal one class of one application. No enabled column, on purpose: a
  switched-off row would be a slow lever pretending to be an emergency stop,
  and cutting a producer off is the kill switch plus the broker ACL.
- The canonical form is declarative data of the infrastructure repository,
  materialized by a deploy job; the hub only reads, through a snapshot with a
  sixty-second window that keeps serving the previous snapshot when a refresh
  fails. There is no configuration seed: a second source of authorization
  outside the auditable trail is how a grant nobody reviewed reaches production.
- An empty registry closes the consumer gate: the `kafka-ingress` role does not
  subscribe and reports unhealthy. An empty table is indistinguishable from a
  materialization that never ran, and with a day of topic retention an
  out-of-order deploy would send a day of legitimate traffic to the dead-letter
  topic while every probe reported success.

## Bus ingress

- `Features/Ingress/` consumes `notifications.requested.v1` under the consumer
  group `notification-hub-ingress`, one record at a time, offsets committed per
  poll batch, at-least-once resolved by `platform.processed_messages` keyed
  `{topic}:{partition}:{offset}`.
- Order of the checks, which is contract: envelope and size, envelope type,
  shape validation, kill switch (declaratory in this phase), producer registry,
  idempotency, recipient budget, published catalog, sensitive-variable
  restriction, variables schema, persistence. The envelope type is checked
  before the body binds, because the type is the schema version and a later
  version would otherwise bind on the coincidence of field names; the refusal
  is `event-type-unsupported`, its own catalog member, so the producer tells
  "your body is wrong" from "your version is not the one this topic speaks".
  The registry runs before the catalog so a
  refusal never leaks which templates exist; the sensitive-variable
  restriction runs before the schema validation because the validation reports
  findings over exactly the payload that must not be inspected; idempotency
  runs before the budget so a legitimate replay never spends it.
- A permanent error records the dead-letter record first, then commits the
  trail and the deduplication mark, then the offset. A mark written first would
  make the replay of a crash skip a record nobody ever recorded.
- The sensitive-variable restriction depends only on the template declaring
  sensitive variables, never on the payload carrying them. For that reason
  alone the dead-letter body replaces `data.variables` with the declared names
  and a header announces the redaction: the entry topic keeps records for a day
  and the dead-letter topic for two weeks, so copying the body verbatim would
  make the control copy the secret to a topic that holds it fourteen times
  longer. Every other permanent reason keeps the original body.

## Outgoing result events

- `Infrastructure/Events/NotificationEvents.cs` builds the CloudEvents rows of
  `notifications.events.v1`, reading the topic name and the hub source URN from
  the platform messaging surface rather than declaring them here: the outgoing
  bus is a transport contract, and ContactConsent publishes `consent_changed`
  onto the same topic. The module owns the event types and payload shapes:
  `rejected` at ingestion and in the pipeline,
  `failed` on an exhausted plan and on expiry, `delivered` on push acceptance.
  Acceptance announces nothing (the producer already holds its 202) and a
  rejection by the principal budget announces nothing either, because one event
  per refused request is the storm the control exists to stop.
- Every event is written through the outbox inside the transaction of the
  effect it reports, and **before** the `IAuditTrail` append, because the
  append holds the partition chain lock until the transaction ends and anything
  queued after it widens the window concurrent ingestion waits on. This holds
  in `IngestionWriter`, `PipelineCommitWriter` and `AttemptDispatchWriter`.
- The band of an outgoing event is the class of its notification, never `auth`:
  the `auth` band protects the delivery latency of an authentication code, and
  a result event is not a delivery.
- The reason of a rejection is always a member of
  `Integration/V1/NotificationRejectionReasons`; the reason of a failure is the
  delivery error vocabulary, which is a pending decision.

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

## Query surface

- Three read routes under `Notifications.Read`: `GET /v1/notifications/{id}`,
  `GET /v1/recipients/{recipientId}/notifications` and
  `GET /v1/notifications?correlationId=`. The role gates the route; there is
  no per-application scope in this phase, because nothing binds a reading
  principal to an application. The containment is elsewhere and is contract:
  exact identity only (no prefix, no wildcard, no listing without a subject,
  no route that lists by `application` alone), a malformed id answering 400
  and a well-formed unknown id answering 404 with a body that never echoes the
  value, a rate-limit policy of its own (`notifications-query`, separate from
  the producer-sized ingestion one), and a structured access log carrying
  principal, route and subject.
- **No `audit_event` per read.** Appending a trail row per query would
  serialize every read against the ingestion on the chain's advisory lock, and
  `audit.read` belongs to `/v1/audit/*`, which is where content and full
  contact actually leave the hub.
- Reads run on `NotificationsReadDbContext`: the same model over
  `Modules:Notifications:Persistence:Ef:ReadConnectionString`, falling back to
  the write connection when absent, no tracking, and every `SaveChanges`
  entry point throwing. Migrations and the design-time factory never touch it.
- Paging is keyset descending over `(created_at, id)` through a PostgreSQL
  row-value comparison, with an opaque cursor (base64url of the instant in ISO
  8601 UTC to the microsecond plus the public `ntf_` id). Four numbers are
  contract, not configuration: page size 50 by default, 200 at most, a 90-day
  default window and a 180-day ceiling. The effective window is echoed in the
  response; a cursor whose position falls outside the window asked for is
  refused as `invalid-cursor`.
- Response shape, three rules. Members that always exist are always present,
  empty arrays included. Members whose value is absent are omitted. Members
  whose source does not exist in this phase are not declared at all, so
  `deliveryEvents` and the read receipt are absent rather than empty.
  `attempts[].deliveredAt` is never stamped in this phase and the omission
  rule keeps it out.
- **Never leaves through here**: rendered content in any form (only
  `content_hash_full` and `content_hash_masked` travel) and
  `variables_masked`, which is still business data and belongs to the audit
  surface. The query projections select column by column so no later refactor
  can reach either.
- The attempt target is the masked contact point, masked by ContactConsent
  through `IRecipientDirectory.MaskContactPointsAsync`, plus whether the point
  is still active. A push attempt has no contact point: it exposes the
  platform and the device registration id, never the token.

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

## Rendered content lives in two phases

- `rendered_content_enc` (bytea) is sealed by
  `Infrastructure/Privacy/RenderedContentEnvelope.cs`, the single owner of that
  shape. The content to send is the top level of the envelope; the masked form
  rides beside it in a `masked` member, and only when the two forms differ.
  Nothing else parses or writes those bytes.
- **The terminal verdict of a send is the transition.** In the same statement
  that writes `sent`, `failed` or `unknown`, the envelope is rewritten with the
  masked form alone: the complete content loses its purpose the instant the
  provider takes or refuses the message, a fallback step renders and seals its
  own content instead of reusing the failed one, and reconciliation asks the
  provider by message id without ever resending content. Throttling and an open
  circuit are not verdicts and never transition.
- **The `notifications-maintenance` role is the rear guard.** An attempt that
  never reaches a verdict (queued or sending, notification expired past the
  configured grace) is settled by `RenderedContentSweep`; content sealed before
  the two-form envelope existed is settled by `RenderedContentBackfill`, which
  renders the published template again with `variables_masked` and substitutes
  only when the recomputed hash matches the stored `content_hash_masked`. A row
  that does not match is left untouched and leaves in a structured review log.
- The two hashes never change: `content_hash_full` stays as the anchor for
  confronting external evidence, and `content_hash_masked` is what the audit
  surface verifies against the durable form.

## Rate limiting

- Two Redis-backed dimensions, both keyed by canonical class: per producer
  principal and per recipient (`Modules:Notifications:RateLimits`); exceeding
  answers 429 with `Retry-After` and the problem `type` of the dimension that
  refused, and the recipient dimension also records
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
  stable codes: `idempotency-key-conflict`,
  `class-not-allowed-for-principal`, `recipient-rate-limited`,
  `payload-invalid`, `template-not-found`, `template-class-mismatch`,
  `template-variables-invalid`, plus the catalog reasons
  `template-deprecated` and `template-disabled`. Exactly two codes are
  protocol conditions of the route and stay out of the catalog, because
  neither ever travels as the `reason` of a rejection event:
  `idempotency-key-required` and `principal-rate-limited`.
- **The 429 names the dimension.** The recipient budget answers
  `recipient-rate-limited` and the principal budget answers
  `principal-rate-limited`, because the two ask the producer for opposite
  behaviors: an exhausted recipient budget means the customer is protected and
  the request must not be retried, an exhausted principal budget means slow
  down and retry. Only the recipient dimension records a trail and publishes a
  rejection event.
- The query surface has its own three stable codes
  (`Infrastructure/Http/QueryProblems.cs`): `invalid-request`,
  `invalid-cursor` and `notification-not-found`. The cursor gets a code of its
  own because a client retrying blindly needs to know whether to drop the
  parameter or the position.

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
