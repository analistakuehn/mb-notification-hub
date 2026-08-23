# Notifications module

## Boundary

- Keep one bounded context in this module: the notification lifecycle, from
  ingestion to dispatch. This unit ships the REST ingestion; pipeline,
  dispatch-side state and the query API arrive in later slices.
- Keep invariants in the entities and pure functions under
  `src/Platform.Api/Modules/Notifications/Domain/`.
- Keep use-case orchestration in the slices under
  `src/Platform.Api/Modules/Notifications/Features/`.
- Read sibling contexts exclusively through their published contracts:
  `Modules.TemplateManagement.Integration.V1` (published catalog and variables
  validation) and `Modules.Audit.Integration.V1` (transactional audit append).
  Never touch another context's data store or internal types.
- Platform infrastructure is a dependency, not a sibling: the outbox writer
  (`NotificationHub.Api.Infrastructure.Messaging.IOutboxWriter`), the envelope
  cipher (`NotificationHub.Api.Infrastructure.Cryptography.IEnvelopeCipher`)
  and the partition provisioning live outside `Modules.*` and never reference
  module types back.

## Owned surfaces

| Path | Responsibility |
|---|---|
| `src/Platform.Api/Modules/Notifications/Domain/` | notification and idempotency entities, class vocabulary, canonical JSON, variables masking, public id form |
| `src/Platform.Api/Modules/Notifications/Features/` | vertical slices for this context |
| `src/Platform.Api/Modules/Notifications/Infrastructure/` | persistence (schema `notifications`), Redis controls, template gate, privacy, partition manager, purge job |
| `src/Platform.Api/Modules/Notifications/NotificationsModule.cs` | service registration and endpoint mapping for this context |

Owned state: `notification` (partitioned parent), `idempotency_key`. The
platform `outbox` belongs to the messaging infrastructure; this module only
appends through the contract.

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

## Variables and PII

- `variables_masked` (jsonb, mandatory) stores the canonical variables object
  with every variable listed in the template's `SensitiveVariables` masked to
  `***`; it is the only plaintext projection ever stored.
- `variables_enc` (bytea, nullable) stores the envelope-encrypted canonical
  form of the **whole** variables object, sealed by the platform envelope
  cipher with the data key of the application.
- **Purge ownership**: erasing `variables_enc` after the notification leaves
  the pipeline belongs to the Core pipeline of a later phase, not to this
  unit; the column is nullable from birth precisely so that purge can null it
  without schema change. Nothing in this module may implement or depend on
  that purge.
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

- `notification` is partitioned by month on `created_at`; the creation
  migration provisions the initial partitions and the module scheduler
  (`Modules:Notifications:PartitionManager`) keeps future months provisioned
  through the platform provisioner. Health check: `notifications-partitions`.
- Never revoke writes on notification partitions: write revoke is a closing
  semantic exclusive to the Audit trail.
- `idempotency_key` stays outside the partitioning so its unique key exists.

## Audit vocabulary

Ingestion actions follow the platform dot vocabulary: `notification.accepted`
(details carry `source = rest`), `notification.duplicate`,
`notification.rejected_at_ingress` (details carry the stable reason). Actor
type is `producer` with the token identity (`appid`/`oid`) as actor id. The
constants live in `Infrastructure/Auditing/IngestionAuditVocabulary.cs`;
promoting them into the Audit `Integration/V1` vocabulary is a pending
cross-module decision.

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
