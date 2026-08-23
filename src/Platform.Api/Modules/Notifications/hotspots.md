# Notifications decision hotspots

Record only evidence-backed risks, accepted assumptions, scheduled actions, or formally deferred decisions. Every entry requires its evidence source, owner, status, and review condition. Keep unresolved questions in the interactive task or an ephemeral discovery inventory.

## Ingestion audit vocabulary lives module-locally

- **Assumption accepted**: the `notification.*` audit actions, the
  `notification` entity type and the `producer` actor type are declared in
  `Infrastructure/Auditing/IngestionAuditVocabulary.cs` instead of the Audit
  module's `Integration/V1` vocabulary.
- **Evidence**: the Audit contract accepts free strings
  (`Modules/Audit/Integration/V1/AuditEntry.cs`), and extending its constant
  vocabulary is an Audit-module change outside this unit's write boundary.
- **Owner**: Notifications module maintainers with Audit module maintainers.
- **Status**: accepted, pending promotion.
- **Review condition**: the next change that touches the Audit
  `Integration/V1` surface promotes these constants there and this module
  consumes them.

## Request fields not persisted at ingestion

- **Assumption accepted**: `locale`, `channelsHint` and `metadata` are
  validated, participate in the idempotency payload hash, and are not stored:
  the notification row carries exactly the accepted data-model columns.
- **Evidence**: the accepted data model omits those columns, and the pipeline
  stages re-read state from the store by design.
- **Owner**: Notifications module maintainers.
- **Status**: accepted.
- **Review condition**: the Core pipeline slice that needs locale or channel
  preference at render/route time must revisit the ingestion persistence
  before consuming them.

## ChannelSelection runs without the producer's channel hint

- **Assumption accepted**: the version-1 channel selection intersects eligible
  channels, delivery plan, published content and reachable contacts, without
  the `channelsHint` reordering, because the hint is not persisted at
  ingestion (previous entry). The render locale likewise comes from the
  recipient profile or the template default, never from the request.
- **Evidence**: `Features/Pipeline/Rules/ChannelSelectionRule.cs` and the
  ingestion persistence, which stores no hint column.
- **Owner**: Notifications module maintainers with Arquitetura.
- **Status**: closed. The accepted policy design drops the hint from the
  version-1 rule; the public contract notes document that the hint is
  accepted and ignored, and per-request reordering stays a named extension
  point whose return trigger is the first producer with an evidenced need.

## The pipeline re-stamps the template version it actually rendered

- **Assumption accepted**: the published renderer only renders the currently
  published version; when a publish lands between ingestion and processing,
  the commit re-stamps `template_version` with the rendered version so the
  notification always records exactly the content it shipped.
- **Evidence**: `Infrastructure/Persistence/PipelineCommitWriter.cs` and the
  render contract, which exposes no render-by-version member.
- **Owner**: Notifications module maintainers.
- **Status**: accepted.
- **Review condition**: a compliance requirement to render the ingested
  version verbatim forces a render-by-version member on the published
  contract.

## Pipeline tables extend the accepted data model with partition columns

- **Assumption accepted**: `notification_attempt` carries `created_at` and
  `policy_evaluation` carries `id` plus `evaluated_at` beyond the accepted
  column lists, because a monthly-partitioned table needs its partition
  column inside the primary key.
- **Evidence**: the creation migration
  (`Infrastructure/Persistence/Migrations/20260823122306_CreateCorePipelineState.cs`)
  and the same fact already accepted for `notification`.
- **Owner**: Notifications module maintainers with Arquitetura.
- **Status**: accepted (`evaluated_at` as partition key is an architect
  decision of this phase).
- **Review condition**: the next data-model revision adopts the columns.

## A crash between claim and verdict parks the attempt on sending

- **Assumption accepted**: when the dispatcher dies after committing the
  claim and before committing a verdict, the attempt stays on `sending`
  forever in this phase, and the redelivered message resolves as a
  duplicate on the stored status instead of resending.
- **Evidence**: a provider send is not idempotent
  (`Infrastructure/Persistence/AttemptDispatchWriter.cs`); the accepted
  posture bans any resend without a conclusive verdict, and reconciliation
  belongs to the phase-2 tracker.
- **Owner**: Notifications module maintainers.
- **Status**: accepted, same bucket as the `unknown` attempts of this phase.
- **Review condition**: the phase-2 reconciliation must sweep `sending`
  attempts older than the provider timeout together with the `unknown` ones.

## Device-token invalidation is reported best effort after the verdict

- **Assumption accepted**: the dead-token report to the ContactConsent
  lifecycle runs after the verdict commit and outside its transaction; a
  failure there is logged and not retried, because the queue message is
  already settled and a redelivery resolves as duplicate.
- **Evidence**: `Features/Dispatching/DispatchMessageProcessor.cs`
  (`ReportDeadTokenAsync`); the invalidation is idempotent on the owning
  side, so any later report of the same token heals the gap.
- **Owner**: Notifications module maintainers with ContactConsent module
  maintainers.
- **Status**: accepted.
- **Review condition**: the phase-2 provider-feedback path must re-report
  dead tokens seen by webhooks, closing the window a lost report leaves.

## The disabled-producer reason is declared and unreachable in this phase

- **Assumption accepted**: `producer-disabled` is a member of the canonical
  rejection catalog that no code path can produce, because
  `producer_registry` has no enabled column.
- **Evidence**: the accepted decision rejects the column outright, since a
  switched-off row would be a slow lever pretending to be an emergency stop;
  cutting a producer off is the kill switch plus the broker ACL, and neither
  exists in this phase either.
- **Owner**: Notifications module maintainers with Arquitetura.
- **Status**: accepted, declared on purpose so the published vocabulary does
  not shift later.
- **Review condition**: the slice that implements the kill switch decides
  whether it produces this reason or keeps refusing at the broker.

## The bus ingress commits its deduplication mark outside the effect transaction

- **Assumption accepted**: the accepted path commits the effect (notification,
  idempotency registration, outbox message, audit entry) in one transaction and
  the offset deduplication mark in a short transaction right after it, with the
  offset committed only after both. Every other consumer keeps the mark inside
  the transaction of its effect.
- **Evidence**: the bus request carries `idempotencyKey`, so the unique
  constraint `(application, idempotency_key)` is the guard, and it is the
  stronger one: it also catches a producer resend that mints a new envelope id,
  which no offset mark ever sees. Forcing the mark inside would require a
  `SAVEPOINT` around the unique-violation resolution of `IngestionWriter`,
  complicating the hot path to reinforce the weaker guard. A crash between the
  two commits redelivers, and the redelivery resolves as a replay with the same
  notification id and no new effect.
- **Owner**: Notifications module maintainers with Arquitetura.
- **Status**: accepted, ratified, and bounded to this consumer.
- **Review condition**: any future consumer that wants this shape must show the
  unique business-key constraint that justifies it. Without one, the mark goes
  back inside the transaction of the effect, which is why `PipelineCommitWriter`
  and `AttemptDispatchWriter` keep it there.

## Authorization by application is asymmetric between the two transports

- **Assumption accepted**: the producer registry authorizes the triple
  principal, application and class, while the Entra app roles authorize only
  the class, so a REST producer authorized for a class may declare any
  `application` in the body.
- **Evidence**: `NotificationClasses.RequiredRole` maps class to role and
  nothing binds a principal to an application on the REST path; the bus path
  reads `producer_registry`. The gap belongs to the REST ingestion and this
  slice only made it visible by contrast.
- **Owner**: Arquitetura with Segurança.
- **Status**: accepted for this phase, recorded as a phase pendency.
- **Review condition**: the onboarding of the second REST producer decides the
  binding form (app role per application, a dedicated claim, or the same
  registry).

## A rejection at ingestion emits an event without a notification identifier

- **Assumption accepted**: the `rejected` event of the ingestion carries no
  `notificationId`, because no notification row exists when the ingestion
  refuses; the correlation the producer holds is the idempotency key.
- **Evidence**: `RequestNotification.Handler` builds the event before any
  persistence, and the outgoing contract now documents the field as optional.
- **Owner**: Notifications module maintainers.
- **Status**: accepted, reflected in the design contract.
- **Review condition**: a consumer that needs a durable identifier at refusal
  time forces the ingestion to mint one before deciding, which would change
  the transactional shape of the refusal.

## A malformed request without a recipient emits no bus event

- **Assumption accepted**: when the shape validation refuses a request whose
  `recipientId` is empty, the trail records the refusal but no event is
  published, because the outgoing contract keys every event by subject and has
  no subject to use.
- **Evidence**: `RequestNotification.Handler.RejectionEvent` returns null for a
  blank recipient; the dead-letter record carries the diagnosis on the bus path.
- **Owner**: Notifications module maintainers with Arquitetura.
- **Status**: accepted, narrow by construction: every other refusal does emit.
- **Review condition**: a producer that relies on the event stream alone for
  diagnosis needs either a synthetic subject or a separate diagnostics channel.

## An idempotency conflict now records an ingress trail

- **Assumption accepted**: the same key with a different payload writes
  `notification.rejected_at_ingress` with reason `idempotency-key-conflict` and
  publishes the rejection event, on both transports. The committed behavior
  answered 409 and recorded nothing.
- **Evidence**: the reason entered the canonical catalog because the bus path
  must dead-letter with it, and a refusal the producer must diagnose without a
  trail is exactly what the catalog exists to prevent. The published REST
  contract is unchanged: same status, same problem type, same body.
- **Owner**: Notifications module maintainers.
- **Status**: accepted.
- **Review condition**: a measured volume of conflicts that makes the trail
  noisy turns this into a sampled or aggregated record.

## The keyset cursor is duplicated instead of promoted

- **Assumption accepted**: `Infrastructure/Http/NotificationQueryCursor.cs`
  reimplements opaque cursor encoding rather than sharing the template
  surface's `PageCursor`, and neither is promoted into the shared kernel.
- **Evidence**: `PageCursor` is `internal` to TemplateManagement and encodes a
  single string key, while this cursor encodes an instant plus an identity and
  must round-trip a microsecond-precision timestamp exactly. Promoting either
  would turn a detail of two routes into a platform contract, and the shared
  kernel has an explicit size budget (`Shared_kernel_must_remain_small`).
- **Owner**: Notifications module maintainers.
- **Status**: accepted, deliberate duplication.
- **Review condition**: a third module needing an opaque keyset cursor with
  the same key shape reopens the promotion.

## The push target reads the platform from the active snapshot

- **Assumption accepted**: the query resolves the push platform through
  `IRecipientDirectory.FindAsync`, whose snapshot only carries active device
  registrations. A push attempt whose registration was later invalidated keeps
  its `deviceTokenId`, reports `active = false`, and loses `platform`. Only
  `platform` is lost: the target identity, the channel, the sequence, the
  status and the error code survive whole, and the routing token never leaves
  the directory in any form.
- **Evidence**: `Infrastructure/Reads/AttemptTargetDirectory.cs`; the accepted
  contract addition of this slice covers masked contact points, including
  removed ones, and grants no equivalent read for device registrations. Adding
  a second contract member was outside this unit's authority. The inactive flag
  is inferred, not guessed: the identity came from this recipient's own push
  fan-out, so an answered snapshot that omits it is conclusive, and the flag is
  omitted entirely when the directory did not answer.
- **Owner**: Notifications module maintainers with ContactConsent module
  maintainers and Arquitetura.
- **Status**: accepted and ratified, with the asymmetry recorded on purpose.
- **Review condition**: the historical read of device registrations is decided
  in the audit API slice, in the same Compliance round as the masked contact
  point of a removed row. That slice already reads historical contact data
  under the audit role and with an access trail, so the second widening costs
  least there and is judged together with the first.

## Policy evidence stays out of the query response

- **Assumption accepted**: `policyEvaluations[]` carries rule, result, reason
  and instant, and not the compact JSON evidence each rule records.
- **Evidence**: the accepted response shape names the members the query owes
  and does not include evidence; the evidence is the trail's payload and the
  audit routes are where it belongs, behind the audit role and its own
  `audit.read`. The evidence is also not PII-free, whatever an older comment on
  `Domain/PolicyEvaluation.EvidenceJson` used to claim: the quiet-hours rule
  records the recipient's timezone and local time. Adding a member later is a
  compatible evolution; removing one is a break, so the narrow shape ships
  first.
- **Owner**: Notifications module maintainers with Arquitetura and Compliance.
- **Status**: accepted and ratified for this slice.
- **Review condition**: a support workflow that cannot answer "why this
  channel" reopens this as **implementation, not design**, because the shape is
  already decided: never the raw jsonb, always a per-rule allow-list
  projection, on the same precedent as the contact dead-letter summary.
  Candidate fields: `remaining`, `plan`, `withContent`, `reachable`, `selected`
  (ChannelSelection); `purpose`, `granted`, `denied` (ConsentGate);
  `windowSeconds`, `acquired`, `failOpen` (DedupeWindow); `window` and
  `releaseAt` (QuietHours). Outside the projection by default: `timezone` and
  `localTime`, which answer no triage question that `window` and `releaseAt` do
  not already answer.
- **Middle ground rejected, and why**: exposing only the catalog-derived fields
  (`plan`, `withContent`, `selected`) while dropping the subject-derived ones
  looks clean and is not. With `withContent` full and `selected` empty, the
  personal fact leaks by elimination: the reader learns the customer was not
  reachable without any field ever naming it. The allow-list is per rule and
  judged as a whole, not split by provenance of each field.

## The rear-guard sweep can mask an attempt a late message would still send

- **Assumption accepted**: `RenderedContentSweep` settles an attempt still
  queued or sending once its notification expired past the configured grace
  (one hour by default). A dispatch message delivered after that point would
  open an envelope that already carries only the masked form and send masked
  content to the provider.
- **Evidence**: `Infrastructure/Privacy/RenderedContentSweep.cs`; the dispatch
  consumer does not check the notification TTL before sending, so nothing else
  stops a post-expiry send today. The window is the notification's own
  `expires_at` plus the grace, which is the widest deterministic bound
  available without reading the class policy again.
- **Owner**: Notifications module maintainers with Arquitetura.
- **Status**: accepted, with the trade named: an abandoned attempt keeping a
  complete OTP forever is the larger exposure, and a send of an already expired
  authentication code is a defect of its own.
- **Review condition**: the phase-2 reconciliation, which must settle `sending`
  and `unknown` attempts, is the natural place to make the dispatcher refuse an
  expired notification; with that refusal in place the grace can shrink.

## The maintenance role has no deployment entry yet

- **Assumption accepted**: `notifications-maintenance` is discovered by the
  worker host through `IWorkerRoleModule` and composes with configuration
  alone, but no declarative infrastructure deploys it, so the sweep does not
  run until the deployment exists.
- **Evidence**: `NotificationsMaintenanceWorkerRole.cs`; the phase records the
  same gap for the queue topology and for the `audit-maintenance` role.
- **Owner**: Engenharia de Plataforma.
- **Status**: accepted, same bucket as the other pending deployment entries.
- **Review condition**: the infrastructure delivery that provisions the queue
  topology deploys this role in the same round.

## Deferred observability is a structured log, not a metric

- **Assumption accepted**: a deferral logs a structured warning with the
  release instant; no counter or health entry exists yet, and the release
  job itself belongs to a later slice.
- **Evidence**: `Infrastructure/Persistence/PipelineCommitWriter.Logger.cs`;
  the telemetry stack is not provisioned in this phase.
- **Owner**: Notifications module maintainers.
- **Status**: accepted, gated.
- **Review condition**: the observability slice that introduces metrics must
  cover deferred > 0 before the quiet-hours feature reaches production.
