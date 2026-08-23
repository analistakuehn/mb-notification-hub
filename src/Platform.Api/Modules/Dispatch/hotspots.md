# Dispatch decision hotspots

Record only evidence-backed risks, accepted assumptions, scheduled actions, or formally deferred decisions. Every entry requires its evidence source, owner, status, and review condition. Keep unresolved questions in the interactive task or an ephemeral discovery inventory.

## Send envelope extends the sketched provider signature

- **Assumption accepted**: `IChannelProvider.SendAsync` receives a
  `DispatchRequest` (delivery target plus rendered message) instead of the
  bare rendered message sketched in the accepted plugin decision, because no
  send exists without a destination and the data model keeps the destination
  (contact point, device token) beside, not inside, the audited rendered
  content. Correlation identifiers for webhook reconciliation will join the
  envelope as optional members without breaking the signature.
- **Evidence**: attempt rows reference a contact point and store rendered
  content separately (`Integration/V1/DispatchRequest.cs`; the accepted data
  model); push fan-out targets one device token per attempt.
- **Owner**: Dispatch module maintainers. Ratified by the architect on 2026-08-23: `DispatchRequest(Target, Message)` is the published contract shape and `Integration/V1` is the normative source; correlation identifiers join as optional members together with the dispatch slice that consumes them.
- **Status**: closed. The dispatch slice landed the single optional
  correlation member (`DispatchCorrelation`) as decided: default null,
  additive in V1, consumed by the SendGrid adapter as `custom_args` and
  ignored by FCM.
- **Review condition**: none; a future provider needing a different
  correlation shape reopens the envelope discussion as a new decision.

## 429 maps to throttled, not to permanent rejection

- **Assumption accepted**: HTTP 429 (and FCM quota codes) map to
  `Throttled`, a transient class, even though the coarse rule "4xx is
  permanent" would catch them; the normalized outcome set includes the
  throttled verdict precisely for this case.
- **Evidence**: `Providers/SendGrid/SendGridChannelProvider.cs` and
  `Providers/Fcm/FcmChannelProvider.cs` mapping blocks; retry-after is
  propagated when the provider names a wait.
- **Owner**: Dispatch module maintainers.
- **Status**: accepted.
- **Review condition**: a provider whose 429 semantics are not transient.

## Credential faults stay transient with alarm

- **Assumption accepted**: FCM `UNAUTHENTICATED`/`PERMISSION_DENIED` and
  token-endpoint failures return `TransientError` (with warning logs), not
  `Rejected`: the message remains deliverable once credentials heal, and a
  permanent rejection would wrongly condemn the attempt.
- **Evidence**: `Providers/Fcm/FcmChannelProvider.cs` mapping block;
  `FcmTokenUnavailableException` flow.
- **Owner**: Dispatch module maintainers.
- **Status**: accepted.
- **Review condition**: operational evidence that credential faults persist
  long enough to exhaust queue redelivery for critical classes.

## Gated SendGrid sandbox suite deferred

- **Decision deferred**: the manually gated test suite against the real
  SendGrid sandbox is not part of this phase; CI covers the request contract
  with an in-process fake. FCM never gets a real-provider suite (no sandbox
  exists).
- **Evidence**: `AGENTS.md` testing rules; the load-test plan substitutes
  providers with fakes.
- **Owner**: Dispatch module maintainers.
- **Status**: deferred, scheduled with the resilience phase.
- **Review condition**: before the e-mail channel goes live.
