# TemplateManagement decision hotspots

Record only evidence-backed risks, accepted assumptions, scheduled actions, or formally deferred decisions. Every entry requires its evidence source, owner, status, and review condition. Keep unresolved questions in the interactive task or an ephemeral discovery inventory.

## A meta refresh discards the host the canonizer already returned

- **Risk accepted for now**: `<meta http-equiv=refresh content="0;url=https://good.com<TAB>@evil.ru/">`
  is approved and navigates the reader to `evil.ru`. The guard at
  `Domain/LinkDomainPolicy.cs:414-424` calls `TryCanonicalHttpHost` over the
  refresh destination, **receives `evil.ru`**, and keeps only the boolean; the
  destination then travels raw to the scan text, where the candidate cut
  reports `good.com`.
- **Evidence**: reproduced by executing the production policy in an isolated
  project against `HEAD` and against this delivery, identical on both sides. It
  passes with tab, line feed, space, no-break space, U+2009 and apostrophe, and
  fails closed only with a double quote. Independent of body size.
- **Owner**: TemplateManagement module maintainers.
- **Status**: open, inherited, not introduced by the destination-guard work.
- **Review condition**: the next change that touches the refresh extractor uses
  the host that call already produced instead of discarding it.

## Destination preparation covers attributes only

- **Risk accepted for now**: the percent-encoding of what the candidate grammar
  cannot carry, and the deliverable-scheme allowlist, run inside
  `TryReadAttributeDestinations` (`Domain/LinkDomainPolicy.cs:595`), which walks
  URI-bearing attributes and nothing else. Four carriers stay outside it: CSS
  `url()`, meta refresh, loose body text, and the whole non-markup path used for
  the subject, `bodyText`, and non-email channel bodies. In those four the cut
  by character class still governs, so `https://good.com<C>@evil.ru` is approved
  for C in tab, line feed, space, no-break space, U+2009 and apostrophe.
- **Evidence**: executed against both trees, character by character identical,
  with `System.Uri` confirming destination `evil.ru`. Severity differs by
  carrier: meta refresh is the entry above; CSS `url()` is medium, because an
  image fetch leaks the reader's address and open event; loose text and
  `bodyText` are low and remain a hypothesis, because they depend on the
  client's autolinker and no real client was measured.
- **Owner**: TemplateManagement module maintainers.
- **Status**: open, inherited.
- **Review condition**: prepared destinations reach the `url()` and refresh
  extractors, which closes this entry and the one above together.

## A bare host with no trailing slash is not a link to the total prohibition

- **Risk accepted for now**: `Seu codigo e 123456. evil.ru` passes in the class
  that forbids links entirely, because the bare-host alternative of
  `ContainsLinkLikeText` requires a trailing `/`. `evil.ru/x`, `www.evil.ru` and
  `bit.ly/x9k2p` are caught.
- **Evidence**: executed against both trees, identical. This is independent of
  the scheme forms closed by the destination-guard work, which covered the
  announced and protocol-relative alternatives.
- **Owner**: TemplateManagement module maintainers with the product owner of the
  authentication class.
- **Status**: open, inherited, deliberately not fixed here.
- **Review condition**: dropping the trailing-slash requirement carries its own
  false-positive budget, because `Sr.Silva`, `nota.fiscal` and `versao.1` would
  read as links; the change waits for a measured corpus, not for an opinion.

## The prose gate is deliberately absent from the link-like detector

- **Assumption accepted**: the gate that keeps `codigo HTTP:200` from reading as
  a destination lives in `LinkDomainPolicy` and is **not** applied to
  `TemplateValidation.ContainsLinkLikeText`. So an authentication SMS carrying
  `codigo HTTP:200` is refused, and it was not refused before this delivery.
- **Evidence**: measured on both trees. The two error budgets point in opposite
  directions: in the only class where links are forbidden outright, a false
  negative is phishing inside the message people act on without rereading, so
  the wider detector is the correct one there.
- **Owner**: TemplateManagement module maintainers with the product owner of the
  authentication class.
- **Status**: accepted debt, written into the module contract.
- **Review condition**: revisit if operators report refused authentication
  templates; a code that does not reach the customer is the cost being traded.

## Scan published bodies over 110 KB before deploying the destination guard

- **Scheduled action**: removing `matchTimeout` from the `NonBacktracking`
  expressions makes two already-documented rules reach large bodies for the
  first time. In the render path, a refused scheme in `href`, `src` or `action`
  is now refused above roughly 110 KB; in the publication path, the
  dynamic-destination rule applies again above roughly 120 KB. A template
  published today can therefore render now and stop rendering after the deploy.
- **Evidence**: the blind window was measured at 80 KB to 100 KB for the
  attribute reader and at about 120 KB for the destination reader, with the
  render ceiling at 1,000,000 characters, so the whole window sits inside what
  can be published. After the removal, no threshold was found across twelve
  fillings and six attack shapes up to 2 MB.
- **Owner**: whoever runs the deploy, with TemplateManagement module
  maintainers.
- **Status**: open until the deploy runs.
- **Review condition**: scan published versions whose body exceeds 110 KB for a
  refused scheme in a destination attribute and for a composed dynamic
  destination. `cid:` and `tel:` are deliverable, so the scan is looking for
  `data:`, `blob:`, `javascript:` and `sms:`. The same removal fixes the
  opposite defect for free, and the shape matters: an XHTML namespace
  declaration that appears **late** in the document, as an embedded SVG emits
  one, was read as a link above the threshold and refused as `www.w3.org`;
  measured refusing at 150 KB and 300 KB before, approving after. A declaration
  in the opening tag was always found, because the blind window depends on the
  stretch with no match before the first one and not on body size.
