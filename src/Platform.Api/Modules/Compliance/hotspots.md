# Compliance decision hotspots

Record only evidence-backed risks, accepted assumptions, scheduled actions, or formally deferred decisions. Every entry requires its evidence source, owner, status, and review condition. Keep unresolved questions in the interactive task or an ephemeral discovery inventory.

## The disclosure append serializes against ingestion

- **Assumption accepted**: every successful audit read takes the chain advisory
  lock of the current monthly partition, so audit reads and ingestion
  contend on the same lock.
- **Evidence**: `Infrastructure/Disclosure/DisclosureRecorder.cs` appends
  through the transactional trail, which takes `pg_advisory_xact_lock` before
  reading `prev_hash`; the transaction contains only the inserts and commits at
  once, and every heavy read of the answer happens before it opens.
- **Owner**: Compliance module maintainers with Engineering.
- **Status**: accepted. The synchronous append is the point of the surface: an
  outbox would place the record after the egress.
- **Review condition**: the ingestion load gate of the current phase. If audit
  read volume becomes material, the fallback is the same one the trail already
  has planned, sub-chains inside the partition.

## Two links per evidence answer

- **Assumption accepted**: the reconstruction route writes two trail rows per
  call, one over the notification and one over the recipient, because both
  subjects are disclosed by the same answer.
- **Evidence**: `DisclosureRecorder.RecordAsync(EvidenceDisclosure, ...)`; the
  contact points, the device registrations and the consent ledger of the
  recipient travel in the answer.
- **Owner**: Compliance module maintainers.
- **Status**: accepted. Both rows share one transaction and therefore one
  acquisition of the chain lock, and the alternative, recording the recipient
  inside the details of the notification row, would turn "who looked at this
  recipient" into a scan. Both rows carry the same access identifier in their
  details, so counting distinct accesses never inflates with the number of
  subjects an answer happens to reach.
- **Load budget**: the audit surface costs **two appends per reconstruction
  call** and one per content call. Any threshold that decides whether the
  planned fallback of the trail (sub-chains inside the partition) has to be
  switched on must be computed over that number, not over one append per call.
- **Review condition**: the ingestion load gate, with the budget above as the
  input. A route that discloses more subjects per answer needs the trade-off
  between rows and lock time measured, not assumed.

## The volume alarm counts per replica

- **Assumption accepted**: the content-disclosure alarm counts openings in
  process, so a principal spread across replicas raises the alarm later than a
  shared counter would.
- **Evidence**: `Infrastructure/Http/ContentDisclosureAlarm.cs` holds a
  per-principal window in memory, like the rate limiter registered beside it.
- **Owner**: Compliance module maintainers with SRE.
- **Status**: accepted for now: a replica-local count is enough to notice a
  sweep, and a shared counter would put the disclosure path behind a network hop
  it does not need.
- **Review condition**: the telemetry work that introduces alarm rules. A
  counter derived from the trail itself, which is already durable and shared, is
  the natural replacement.

## Lifecycle stamps the contact ledger does not record

- **Assumption accepted**: a contact point is described with its channel, its
  masked value, whether it is verified, whether it is active and when it was
  removed, and with nothing else. There is no creation instant and no
  verification instant, because the ledger has no such columns.
- **Evidence**: `ContactPoint` carries `Verified` and `RemovedAt` only; the
  `contact_point` table has no `created_at` and no `verified_at`.
- **Owner**: Architecture with the ContactConsent maintainers.
- **Status**: accepted with a named trigger, not an open pending decision. None
  of the reconstruction questions depends on those stamps, and backfilling them
  for existing rows would be a claim about history rather than a record of it.
- **Trigger**: whichever comes first between (i) the level-2 policy the threat
  model anticipates, which blocks a channel for a contact verified less than 24
  hours ago and turns the instant into a functional requirement, and (ii) the
  next ContactConsent migration that already touches contact writes. In case
  (ii) `created_at` is born prospective with a default and `verified_at` is
  stamped only on a new verification.
- **Rule when it lands**: a row that predates the columns stays null forever,
  and the contract **omits the null member**. It never answers zero and never
  answers an epoch, because both would state a fact the ledger does not hold.

## The invalidation reason lives in the trail, not in the state block

- **Assumption accepted**: a device registration in the state block states that
  it was invalidated and when, never why. The reason the provider gave is
  recorded by the lifecycle write as an audit event over that registration.
- **Evidence**: `DeviceTokenInvalidation` writes the reason into the details of
  a `device.invalidated` event; the `device_token` table has no reason column.
- **Owner**: Compliance module maintainers.
- **Status**: accepted, and it is the better shape: the reason is a trail fact
  and belongs to the block the chain covers. The answer therefore includes the
  registration subjects in the trail read, and the contract says so in words
  rather than by omission, so the next reader does not reopen the same gap.
- **Review condition**: none open. A reason column on the row would create a
  second home for one truth, which is exactly the drift between a column and the
  canonical text that the chain verification exists to catch.

## A stored complete form that is old is a stuck attempt

- **Assumption accepted**: the content route answers `completeFormStillStored`
  true whenever the attempt has not reached a terminal verdict, and serves the
  masked form regardless.
- **Evidence**: the envelope keeps the masked form as a companion member until
  the verdict discards the complete one; the route reads the companion.
- **Owner**: Compliance module maintainers with SRE.
- **Status**: accepted for the response. The member is a fact about the attempt,
  not a defect of the answer.
- **Review condition**: the alarm belongs to the **age** of the condition, not
  to the member. A true value on a recent attempt is normal; a true value on an
  old one means an attempt stuck in queued or sending, which is exactly the case
  the module's own backstop sweep covers. Build the alarm on that age when the
  telemetry work lands, together with the other alarms of the phase.
