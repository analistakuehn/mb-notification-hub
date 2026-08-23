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
