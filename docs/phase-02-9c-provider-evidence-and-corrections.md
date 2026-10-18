# Phase 2.9C — Provider evidence and correction rules

Status: proposed design and investigation, reviewed on 2026-10-18 for `phase/02-backend`.
This document changes no runtime behavior. `Approved` remains unreachable and
`IsUpdateAuthorized` remains false. No writer, migration, database write, automatic
approval, frontend change, commit or push is included. Proposed product rules below
require explicit approval before implementation.

## Decision and repository baseline

football-data.org observations do not currently provide a verified supersession contract.
Keep ordinary changes at `InsufficientEvidence`, exceptional corrections at
`RequiresReview`, and invalid identities/representations at `Rejected`. A successful
request, later timestamp or repeated response cannot unlock approval. A future
operator adjudication may accept a specific representable value despite unknown
chronology; it must not label that chronology verified.

The design refines [Phase 2.9A](match-synchronization-policy.md) and preserves the
implemented [Phase 2.9B](phase-02-9b-match-synchronization-policy.md) behavior.

| Actual contract | Finding and consequence |
| --- | --- |
| [Observation](../src/SportStats.Application/Football/Ingestion/MatchProviderObservation.cs) | Missing/Null/Value, original status and provenance are retained. Public records and caller retrieval times authenticate nothing. Missing preserves a baseline; zero is a value. |
| [Normalizer](../src/SportStats.Infrastructure/FootballData/FootballDataObservationNormalizer.cs), [scope](../src/SportStats.Infrastructure/FootballData/FootballDataNormalizer.cs) | Match tuple is `football-data.org / competition:PL/season:{queryYear} / externalId`; teams use `competition:PL`. These are importer boundaries, not verified global uniqueness. Only `score.fullTime.home/away` is observed; duration/period semantics are not retained. |
| [Policy contracts](../src/SportStats.Application/Football/MatchSynchronization/MatchSynchronizationContracts.cs), [engine](../src/SportStats.Application/Football/MatchSynchronization/MatchSynchronizationPolicyEngine.cs) | Input contains observations and trusted identity snapshot only. Evidence is hard-coded unverified, authority false. Internal evaluator performs identity/difference/conflict checks. Candidates remain per observation and atomic; partial observations are never spliced. |
| [HTTP client](../src/SportStats.Infrastructure/FootballData/FootballDataClient.cs), [response contract](../src/SportStats.Application/Football/Ingestion/IFootballDataClient.cs) | Fixed HTTPS origin, bounded response/time, single attempt; only status/server date/size retained. No conditional requests, ETag, Last-Modified or durable transport receipt. Composition disables redirects; preserve that boundary. |
| Request versus normalization | `GetMatchesAsync` does not send season/queryYear; normalization checks returned season against caller queryYear. A future receipt must bind the actual request, response and normalization context rather than assume a historical query occurred. |
| [Identity reader](../src/SportStats.Infrastructure/Persistence/MatchMetadata/EfMatchMetadataIdentityReader.cs), [evaluation service](../src/SportStats.Infrastructure/Persistence/MatchMetadata/MatchMetadataEvaluationService.cs) | Sequential no-tracking reads are best effort, without a transaction/locks. Their snapshot is diagnostic, never a write permit. |
| [Match](../src/SportStats.Domain/Football/FootballMatch.cs), [EF mappings](../src/SportStats.Infrastructure/Persistence/EntityConfigurations.cs) | No metadata update operation/version, accepted history, correction audit or idempotency ledger. Mapping tuple indexes are nonunique. Private setters and constraints do not provide concurrency protection. |

Existing canonical season and ordered participants are immutable in this workflow.
Require exactly one persisted match/season/team mapping for each exact provider tuple;
never rematch on names, kickoff, score or matchday, or hide duplicate mappings.

## Official provider investigation

Only official provider documentation supports provider claims here. Public documentation
was inspected on 2026-10-18; no authenticated API calls or new empirical probes were made.
“Unverified” means no sufficient guarantee was located in the reviewed material, not
proof that a feature does not exist. Existing live validation in
[provider-validation.md](provider-validation.md) establishes observed behavior only.

Official sources inspected:

- [V4 overview and release notes](https://docs.football-data.org/general/v4/index.html).
- [V4 resource design](https://docs.football-data.org/general/v4/resources.html).
- [V4 match resource and status workflow](https://docs.football-data.org/general/v4/match.html).
- [V4 API policies](https://docs.football-data.org/general/v4/policies.html).
- [V4 competition/match collection](https://docs.football-data.org/general/v4/competition.html).
- [V4 lookup tables](https://docs.football-data.org/general/v4/lookup_tables.html).
- [Legacy V2 reference](https://www.football-data.org/documentation/api), used only to
  distinguish older statements from V4 guarantees.

Targeted official-domain searches for ETag, Last-Modified, revisions, ordering and
correction history supplied no sufficient additional contract. Search absence is not a guarantee.

| Question | Documented finding | Classification / implication |
| --- | --- | --- |
| Monotonic revision identifiers? | Reviewed V4 pages expose no guaranteed per-match monotonic revision contract. | **Unverified**; no sortable revision can be minted from local captures. [Overview](https://docs.football-data.org/general/v4/index.html), [resources](https://docs.football-data.org/general/v4/resources.html). |
| `lastUpdated` ordering? | Match JSON includes `lastUpdated`; precision, ties, rollback, field coverage and supersession guarantees were not located. | **Unverified** ordering, including unequal timestamps. [Match](https://docs.football-data.org/general/v4/match.html). |
| Stable identity? | V4 supports addressing a resource by ID. Legacy V2 describes resource IDs as unique. Neither reviewed contract establishes V4 lifetime non-reuse, correction/replacement behavior or immutable participants. | Addressability documented; permanence and cross-scope identity **unverified**. [Resources](https://docs.football-data.org/general/v4/resources.html), [legacy reference](https://www.football-data.org/documentation/api). |
| Correction/revision history? | Current match and head-to-head resources are described, but no complete correction ledger or prior-revision API guarantee was located. | **Unverified**; head-to-head is not revision history. [Match](https://docs.football-data.org/general/v4/match.html). |
| ETag / Last-Modified / conditional GET? | No reviewed documentation specifies their support or scope. | **Unverified**. Even an observed validator would not prove ordered match revisions or correction authority. [Resources](https://docs.football-data.org/general/v4/resources.html), [policies](https://docs.football-data.org/general/v4/policies.html). |
| Status semantics? | Workflow describes SCHEDULED, TIMED, IN_PLAY, PAUSED, FINISHED. LIVE is a filter combining play/pause; enum also includes exceptional statuses and AWARDED. | Documented vocabulary, not guaranteed irreversible progression. Adapter collapses schedule/time and play/pause; AWARDED remains unsupported. [Match workflow](https://docs.football-data.org/general/v4/match.html#_status_workflow_explained). |
| Score meaning? | Match example includes duration and score components. No reviewed material supplies the complete state-specific correction/ordering contract needed by this adapter. | Presence is documented; sufficient live/final/extra-time/shootout applicability remains **unverified**. [Match](https://docs.football-data.org/general/v4/match.html). |
| Missing/null fields? | Policies permit unavailable values and representation differences exist between resource embeddings. | Never infer deletion or complete evidence from absent fields. [Policies](https://docs.football-data.org/general/v4/policies.html), [resources](https://docs.football-data.org/general/v4/resources.html). |

Provider questions requiring authoritative documentation: exact match ID lifetime/reuse
and replacements; revision stream/epoch and field coverage; timestamp equality/precision,
rollback and correction behavior; cache validator resource/representation scope; complete
live/final/duration semantics; official correction provenance and history completeness.
No provider communication is initiated by this phase.

## Minimal evidence model and trust boundaries

Keep four distinct results: **observation → verified evidence → policy eligibility →
explicit persistence authorization**. None implies the next. The following are conceptual
contracts, not new DTOs or database tables implemented in this phase.

| Contract / trusted owner | Minimum content |
| --- | --- |
| Observation receipt / registered ingestion boundary | Server-issued receipt ID; exact provider/match identity and competition/season/ordered-participant dependencies; actual fixed-origin request context and capture ID; successful transport facts; immutable original presence-aware observation; adapter version; payload evidence reference and byte digest; normalized digest with algorithm/schema version. |
| Verified evidence assessment / trusted verifier | Receipt references; verifier/verification version; reviewed provider contract reference and applicability; exact entity, endpoint/representation, stream/epoch and covered fields; comparison to a specific accepted baseline; ordering relation; score-semantic applicability; action/field authority configuration version; conflicts and optional adjudication reference. Unknown remains explicit. |
| Policy baseline and eligibility / pure policy | Immutable tuple, canonical metadata and expected local version; mapping dependencies/generation; accepted-history head and evidence-state version; policy/authority versions; exact atomic candidate/digest, evidence refs, outcome and reasons. `IsUpdateAuthorized = false`. |
| Persistence authorization / separate authenticated application boundary | Server-issued grant ID; authenticated actor/service and allowed action/fields; exact candidate/decision digest; expected metadata/mapping/evidence/config versions; supporting review refs; validity, revocation and single-use conditions. Never constructed from provider JSON or policy output alone. |

Evidence validation must resolve server-held immutable receipts and reviewed contracts,
verify digest/identity binding and adapter applicability, validate presence/ranges/status,
then check comparison scope and all known relevant conflicts. Freeze collections and
use deterministic canonical serialization that includes Missing versus Null versus Value,
identity, field names and schema version. Retain raw-body digest separately: JSON formatting
and unrelated fields may change without a semantic change. Equality hashes prove neither
authenticity nor freshness. Receipt authenticity attests our capture, not a provider signature.

HTTPS with certificate validation and a fixed origin establishes transport origin under
the transport trust assumptions. The API token authenticates our client to the provider;
it does not sign responses. Registered handler configuration, redirect prevention, secret
redaction and bounded parsing remain part of the trusted composition. Imported JSON,
fixtures, manually entered source labels and successful normalization are not capture proof.

Use typed ordering `Unverified | Newer | SameRevision | Older | Incomparable | Contradictory`
and authority `Unverified | Permitted | Denied`, computed by a verifier. A public boolean,
`VerifiedBy` label, timestamp or receipt-shaped object grants nothing. Provider timestamps,
HTTP Date, arrival order, retries, score direction and status ranking cannot establish
ordering. A reviewed revision comparator may operate only inside its documented stream,
epoch and field coverage. Opaque ETags establish no numeric order. Equal timestamps are
not equal revisions without a contract; differing hashes alone establish no temporal order.

Authority is a separate versioned product configuration scoped to provider, identity,
competition, fields and actions. Technical origin/order cannot grant overwrite authority.
Contradictory relevant receipts across requests/providers must remain visible; do not
select a winner by majority, arrival, priority label or filtering out conflicting rows.

## Proposed correction policy

Preserve whole-match atomic candidates: overlay one coherent observation onto the
baseline, keep explicit unchanged fields, and block every proposed field if any blocker
remains. Do not splice two partial score pairs or complementary provider observations.
Any adjudicated candidate is a separately recorded proposal referencing all conflicts.

Gate order: invalid identity/representation or immutable changes → `Rejected`;
unresolved contradictions → `RequiresReview`; valid unchanged proposal → `NoChange`;
verified older changed proposal → `Rejected`; substantive corrections → `RequiresReview`;
ordinary changes with missing evidence → `InsufficientEvidence`; only fully evidenced,
authorized-scope candidates could eventually be `Approved`. Retain all applicable reasons.
An explicit authority denial is rejection; an unknown authority is insufficient evidence.
These future ordering/adjudication branches do not exist in 2.9B.

- **Normal progression:** Scheduled → Live, Live → Finished, or Scheduled → Finished
  after missed polling are ordinary candidates only with verified supersession and
  action authority. Finalization requires an explicit nonnull score pair in one coherent
  observation and verified final semantics; never retain stale live goals as final evidence.
  Scheduled → Live may retain null/null, but any retained/supplied score needs live semantics.
  No elapsed-time or kickoff inference may manufacture a status.
- **Kickoff rescheduling:** while Scheduled, require field authority and supersession.
  During Live/Finished, require adjudication. Never change identity because kickoff changed.
  Explicit nullable clearing is reviewable, not ordinary rescheduling.
- **Matchday corrections:** same gates as kickoff. No inferred season change or reparenting.
- **Live scores:** require explicit nonnull home/away pair and verified semantics for that
  state. Increases and decreases use identical gates, including VAR-like decreases.
  A partial pair is insufficient; supplied unchanged counterpart counts as present.
  Goals against Scheduled without a qualifying transition require review.
- **Finished corrections:** every metadata change or exit from Finished requires specific
  correction evidence and adjudication under approved product authority. A higher ordinary
  revision alone is insufficient. Score correction requires a coherent pair. No time-based
  finality window is assumed. Operator acceptance does not prove provider ordering.
- **Postponed:** entry, exit, reschedule or score change requires reviewed disposition;
  do not clear kickoff/goals automatically or infer a new fixture. Resumption must identify
  whether the same mapped match is involved; replacement identity uses another workflow.
- **Suspended:** preserve stored values until reviewed resolution; do not infer score
  retention, restart, resumption or finalization from the status name.
- **Cancelled:** no automatic final score, deletion, reopening or reset. Reinstatement
  requires explicit reviewed evidence. AWARDED is unsupported and rejected, even for operators.
- **Reversals/Unknown:** observed Null/Unknown/undefined status is invalid. Initialization
  from canonical Unknown and representable reversals require review. Metadata review cannot
  bypass identity invariants or extend the score/status representation.
- **Cross-provider disagreements:** retain all relevant conflicts and require adjudication;
  no global provider ranking or cross-provider revision comparison. Agreement does not
  establish independent authority, ordering or permission.
- **Missing/contradictory evidence:** preserve canonical values. Missing makes no proposal;
  explicit nullable clears require review. Invalid mandatory status is rejection, not a clear.

An unchanged proposal remains `NoChange` after validation/conflict gates, including an old
observation. It cannot adopt a baseline, advance a watermark, consume a correction grant
or renew finality. A future evidence-only advancement would need its own reviewed rules.

## Accepted-revision history and replay

Use a local, append-only acceptance chain per canonical match, not a fabricated provider
revision and not an event-sourcing platform. Proposed minimum durable acceptance entry:

```text
AcceptanceId, MatchId, PreviousAcceptanceId
ExpectedMetadataVersion, ResultingMetadataVersion
BeforeMetadata, AfterMetadata, PresenceAwareCandidateDigest, DigestFormatVersion
ExactIdentityAndMappingDependencies
ObservationReceiptIds, VerifiedEvidenceIds, ResolvedConflictIds
ProviderIdentity, StreamOrEpoch?, CoveredFields, ProviderRevisionToken?
RevisionComparatorContractVersion?  // null unless independently verified
PolicyVersion, AuthorityConfigurationVersion, EvidenceStateVersion
AcceptanceKind = ProviderSupersession | OperatorAdjudication | BaselineAdoption
ReviewAndGrantReference?, OperationId, CommittedAtUtc, ActorOrServiceReference
```

Before/after values must be reconstructable; a hash alone is not history. A unique
`(MatchId, ResultingMetadataVersion)` and predecessor linkage prevent two accepted heads
at the same version. Store per-field supporting evidence where a candidate retained old
fields: a new local acceptance does not claim that a partial provider revision covers them.
An optional current-head projection is sufficient later; initially derive from the chain.

Maintain a separate receipt/decision/conflict journal for nonaccepted observations.
Acceptance history alone cannot remember a previously quarantined contradictory response.
The protected evidence-state generation changes when relevant new conflicts are recorded,
invalidating outstanding decisions/grants; unprocessed concurrent receipts cannot be claimed
as considered. Serialize their admission into the evidence state with future write validation.

- Initial canonical data has no accepted provider baseline. Explicit baseline adoption must
  bind the existing version and record its known/unknown provenance; an observed large token
  or unchanged response cannot establish supremacy. Adoption is a separate reviewed action.
- Exact replay of a committed operation returns the recorded result without another version
  increment/acceptance. The same idempotency key with different bound content is rejected.
- A verified older changed observation is rejected. An equal verified revision with divergent
  covered semantic content is quarantined for review; do not retry it as a transient failure.
  Compare only covered fields and preserve all presence distinctions required by its contract.
- Unknown epochs, alias streams, scopes and providers are incomparable unless a reviewed
  contract establishes a shared revision space. Provider token resets never reset the local chain.
- Repeated unverified observations may be deduplicated for storage, but never advance a
  provider watermark. Retain distinct capture references when needed to reproduce decisions.
- Operator corrections create local acceptance history with chronology still unverified and
  a protected authority barrier for affected fields. Later provider evidence cannot silently
  undo them; explicit reviewed handback or another adjudication is required.

## Future concurrency and persistence requirements

All items here are prerequisites for a separately authorized writer, not implementation work.
Prefer an explicit increasing local bigint metadata version over timestamps or value equality;
every canonical metadata mutation, including manual repair, must participate. This detects
ABA (values changed and then restored). CAS on a match row alone does not protect mappings,
accepted heads, authority revocation, evidence conflicts or grants.

1. Authenticate the command; resolve its server-held grant/idempotency key. Open a dedicated
   transaction/context. Use one documented lock order compatible with existing identity
   coordination, then canonical MatchId across every provider/alias, then evidence/grant state.
2. Reload canonical values/version, immutable tuple, exact mappings including duplicates,
   accepted head/evidence generation, authority/policy configuration and grant validity under
   transactional protection. Locking existing mapping rows alone cannot prevent duplicate
   insertion phantoms because current tuple indexes are nonunique. Require all relevant
   writers to participate in coordination, or a separately reviewed serializable/constraint
   design. Do not assume existing ingestion advisory locks protect a metadata writer.
3. Resolve/revalidate evidence and reevaluate the exact candidate using the protected state.
   Recheck action/field permission and current revocation/expiry. Do no provider HTTP calls
   while holding this transaction; capture evidence beforehand and invalidate on new conflicts.
4. Conditionally update by MatchId and expected metadata version (also check immutable tuple
   and baseline values defensively); increment version once. Zero affected rows means conflict,
   never success. A policy `Approved` result still cannot substitute for a valid grant.
5. Commit metadata, acceptance history/head, evidence-state update, single-use grant consumption,
   unique idempotency result and audit atomically. Failure of any part rolls back everything.
   A savepoint/repository success is provisional until the transaction owner commits.
6. On version/mapping/evidence/config conflict, roll back and load/reverify/redecide. Use bounded
   retries only for safe transient contention. Never silently rebase an operator grant; changed
   baseline requires new review. On lost commit acknowledgement, first look up the durable
   operation result. A retry must not duplicate acceptance or grant consumption.

Future schema work includes metadata version, acceptance and evidence references, protected
evidence generation/head, correction review/grant records, immutable audit and unique operation
keys. Exact constraints, mapping uniqueness cleanup, lock protocol and retention migrations
require a separate database review and approval. No schema names above are final migrations.

## Operator-reviewed correction pathway

An operator submits a correction request, never a trusted evidence flag. The server captures
the principal through the supported authentication system, checks competition/match/action/field
permissions, loads the baseline and records an exact presence-aware candidate with justification,
supporting receipts and every known conflict. Uploaded timestamps or documents are unverified
until separately checked; a source URL or “official” label alone supplies no authority.

A reviewer with explicit correction permission examines the diff, provenance, score semantics,
identity, chronology limitations and conflicting evidence. Record approve/reject/defer with
authenticated reviewer, server time and rationale. Recommend separate requester/reviewer for
finished corrections, subject to product approval; absent an approved privilege model, no grant
can be issued. Review cannot override invalid identity or unsupported representation.

After review, a separate authorization boundary may issue an expiring, revocable, single-use
grant binding request ID, match/immutable tuple/mappings, candidate/decision digest, expected
metadata/evidence/configuration versions, policy version, supporting/conflicting evidence,
actor or executor scope, permitted action/fields and reviewer record. Authentication assurance
(including MFA/step-up), dual control, lifetime and handback rules require explicit decisions.
Use a server clock for expiry; client timestamps never control validity.

The future executor rechecks authorization and atomically consumes the grant with the write.
Wrong candidate/actor/scope, stale baseline, expired/revoked/used grant or missing audit causes
no write. A review record itself is not a persistence grant. Replay returns a prior committed
operation only if its binding matches; it never executes the correction again.

Audit receipt capture, verification, conflict discovery, decision, review, grant issuance/
revocation/consumption, failed attempts and commit results. Record actor/service IDs, before/
after values, evidence/contract/policy/configuration versions, digests, request/operation IDs,
reasons and server times. Accepted-write audit is transactional; failed-attempt diagnostics
are separate and must not imply a commit. Restrict append/update/delete privileges and payload
access; omit tokens/arbitrary headers, sanitize raw text, and define bounded retention and
payload licensing before storage. Append-only access control does not imply protection from
a database administrator; stronger tamper evidence is a later risk-based decision.

## Threat model

| Threat / boundary | Required defense and residual limit |
| --- | --- |
| Caller fabricates timestamps, source, revision or authority flag | Resolve trusted capture and contract records server-side; no deserialized trust. TLS cannot establish correctness or chronology. |
| Token leakage, redirect or substituted transport handler | Fixed HTTPS origin, redirects disabled, trusted composition, secret-free logs and bounded/sanitized failures. No arbitrary operator URL fetch in the writer. |
| Identity substitution, aliases or duplicate mapping phantoms | Exact tuple and immutable relationships; protect mapping set transactionally; never natural-key fallback. |
| Old payload, equal-token equivocation or incomparable stream | Scoped verified comparison, persistent conflict journal, no arrival-time selection. Unverified order stays blocked. |
| Partial-pair splice or unsupported duration/status | Preserve presence, require coherent score evidence and semantics; reject unsupported representations. |
| Cross-provider oscillation / undoing manual corrections | Canonical-match serialization, scoped authority and explicit manual-correction barrier/handback. |
| Approval substitution/replay, stolen or revoked privilege | Authenticated scoped review/grants, expiry/revocation checks, exact digest/baseline binding, atomic single-use consumption. Compromised authorized reviewers remain a risk addressed by assurance/dual-control policy. |
| TOCTOU, ABA, concurrent conflict/config change | Local version, dependency protection, transactional revalidation, CAS and bounded fresh reevaluation. |
| Duplicate commit after timeout / partial audit failure | Durable unique idempotency binding and all-or-nothing commit; look up unknown outcomes before retry. |
| Evidence deletion, audit tampering or payload injection | Restricted immutable records, bounded retention, reproducible normalized values, sanitized display. Missing required evidence fails closed. |

## Decision matrix

“Eventually” is conditional on approved product rules, verified evidence, separate scoped
authorization and all future transaction/audit safeguards. **Persistence is false for every
row today.** Future `Approved` means eligibility only. Identity rejection dominates other rows;
unresolved conflicts dominate unchanged/ordinary changes.

| Scenario | Available evidence | Expected policy result (current → proposed future) | Could persistence eventually be authorized? | Remaining requirements |
| --- | --- | --- | --- | --- |
| Ordinary Scheduled kickoff or matchday change | Current authentic-looking payload, later `lastUpdated` | InsufficientEvidence → InsufficientEvidence | No from this evidence | Capture, supersession contract/baseline, field authority |
| Scheduled reschedule, verified newer scoped revision | Hypothetical verified receipt, adopted baseline and authority | InsufficientEvidence → Approved | Conditional yes | Product approval, grant, protected dependencies/CAS |
| Scheduled → Live | Current status only | InsufficientEvidence → InsufficientEvidence | No from this evidence | Ordering/action authority; any score needs semantics |
| Live → Finished or missed Scheduled → Finished | Coherent explicit final pair, otherwise current evidence | InsufficientEvidence → Approved only after all gates | Conditional yes | Final-score contract, supersession, authority and write safeguards |
| Finalization with missing/null score member | No coherent explicit nonnull pair | InsufficientEvidence; changed nullable clear RequiresReview | No for the incomplete candidate | Complete supported evidence/new candidate |
| Live goals increase or decrease | Complete pair, no verified semantics/order | InsufficientEvidence → InsufficientEvidence | No from this evidence | Same semantics/order gates for both directions |
| Live score change, verified pair/semantics/newer revision | Hypothetical complete evidence/authority | InsufficientEvidence → Approved | Conditional yes | Product approval and separate write safeguards |
| Partial live pair from two receipts | Complementary values | InsufficientEvidence → InsufficientEvidence | No via splicing | One coherent pair or exact adjudicated candidate |
| Score against Scheduled without qualifying transition | Goals do not establish status | RequiresReview → RequiresReview | Only after supported adjudication | Score/status interpretation and exact new decision |
| Finished score, kickoff, matchday or status correction | Higher timestamp or even ordinary verified revision | RequiresReview → RequiresReview | No on revision alone | Specific correction authority/adjudication; pair if scores change |
| Finished correction with exact authorized review | Verified adjudication, semantics, baseline/conflicts bound; chronology may remain unknown | RequiresReview → Approved in future pathway | Conditional yes | Approved privilege model, separate single-use grant, CAS/audit |
| Postponed/Suspended/Cancelled entry/exit/change | Status name and retained scores | RequiresReview → RequiresReview | Only for explicitly reviewed disposition | Same-match/restart/score-retention product rules and evidence |
| Nullable clear / supported reversal / canonical Unknown initialization | Explicit representable proposal | RequiresReview → RequiresReview | Conditional after adjudication | Clear/transition authority and baseline-bound review |
| AWARDED, observed Unknown/Null status, negative goals, identity change | Unsupported/invalid evidence | Rejected → Rejected | No in this workflow | Separate representation or identity design |
| Cross-provider disagreement or batch contradiction | Conflicting relevant proposals, even one equal to baseline | RequiresReview → RequiresReview | No until resolved | Retained conflicts and exact adjudication; no global ranking |
| Changed verified older revision | Hypothetical verified comparator and accepted head | InsufficientEvidence or review today → Rejected | No as supersession | A correction requires separate supported adjudication, not relabeling old evidence |
| Same verified revision, differing covered semantic content | Provider-contract contradiction | Review if visible batch today → RequiresReview | No until resolved | Quarantine and persistent conflict evidence |
| Equal timestamp or unknown epoch with changed payload | No verified revision relation | InsufficientEvidence or substantive review → same | No from this evidence | Contract or scoped adjudication |
| Valid unchanged/old/Missing-only observation | No contradictory evidence | NoChange → NoChange | No write needed | No watermark advancement or baseline adoption |
| Existing canonical data, first receipt | No accepted baseline | InsufficientEvidence or substantive review → same | Only separate adoption/correction | Explicit legacy baseline adoption; token magnitude irrelevant |
| Later provider value conflicts with manual correction | Local authority barrier | Review if visible today → RequiresReview | No silent overwrite | Explicit reviewed authority handback/adjudication |
| Exact committed operation replay | Durable matching idempotency result | Future executor returns prior result; no new policy approval | No second write | Durable lookup/unique operation binding |
| Reused key with altered content / expired grant | Invalid authorization | Future executor rejects independently of policy | No | New valid request/review; do not rebase grant |
| Stale metadata/mapping/evidence/config version | Otherwise eligible candidate | Eligibility invalidated; executor conflict | No on old decision | Rollback, reload, reverify, new review where bound |

## Multiple providers without speculative infrastructure

Keep the existing scoped `ProviderIdentity` and one registered adapter/verifier per provider.
Store small reviewed contract artifacts and versioned scoped authority configuration; use
provider-specific comparators only when warranted. Keep canonical acceptance serialization
per match, with provider identity on supporting evidence. Do not add a generic event bus,
universal revision clock, confidence-scoring framework, majority voting or provider ranking.
A second provider must establish its own authenticity, semantics and ordering applicability;
the common policy consumes typed assessments, not provider-specific JSON or raw status parsers.

## Blockers and recommended sequence

| Category | Decision or blocker |
| --- | --- |
| Can decide now | Preserve read-only fail-closed behavior, exact identity/presence/atomicity, four boundaries, no timestamp authority, no automatic exceptional correction, no NoChange watermark advancement. |
| Provider documentation needed | Ordering/revision coverage and ties/resets, identity lifetime, score/status applicability and correction history/authority. Conditional HTTP support is optional transport optimization and cannot unlock synchronization by itself. |
| Explicit product approval needed | Proposed transition/correction rules; authority scopes; baseline adoption; operator privilege/authentication assurance and dual control; expiry, retention/licensing, exceptional score handling and manual-correction handback. Unknown documentation does not force accepting these risks. |
| Database work later | Local version and participation by all writers; accepted chain; capture/conflict/evidence state; grant/audit/idempotency records; mapping phantom protection/constraints and transaction protocol. |
| Security/application work later | Trusted receipt/verifier construction, authenticated correction boundary, server-side authorization and reproducible evidence validation. Existing DTOs cannot supply these guarantees. |

Recommended next phase: **2.9D — reviewed evidence contracts and operator-correction
specification**, still without a writer. First obtain product decisions and authoritative
provider answers; record remaining unknowns explicitly. Specify baseline adoption,
presence-aware serialization and receipt/verifier contracts with adversarial fixtures.
If ordering remains undocumented, keep automatic provider eligibility blocked and design
only the exact operator-adjudication route; do not fabricate a revision stream.

After separate approval: implement trusted capture and a pure evidence assessment boundary
with tests, preserving false persistence authorization. Then separately design/review schema,
authentication/grants and transactional concurrency tests. Only after those prerequisites,
explicitly authorize a narrowly scoped writer phase. No next phase is automatically approved
by this recommendation.

## Independent specialist reviews and verification

Four independently delegated reviews used read-only repository access; no specialist changed
files or executed database/provider requests.

- **Backend:** confirmed the absence of an evidence input and unreachable approval. Recommended
  receipt → verified assessment → eligibility → separate grant; highlighted actual-request versus
  caller queryYear binding, PL identity scope and fullTime-only semantics. Incorporated above.
- **Database:** confirmed missing version/history/idempotency and nonunique mapping indexes.
  Recommended local acceptance chain, separate nonaccepted-conflict history, canonical-match
  coordination, phantom protection, CAS and atomic audit/grant/idempotency commit. Incorporated above.
- **Security:** confirmed existing fixed-origin/redirect/secret controls and false authorization.
  Required trusted receipt resolution, no boolean/timestamp authority, exact replay-protected grants
  and manual-correction barrier. Authentication assurance, dual control and retention remain open.
- **QA:** confirmed current tests cover conservative outcomes, presence, timestamps, identity,
  batch conflicts, atomicity and unreachable approval. Evidence authenticity, durable history,
  concurrency and operator authorization remain unimplemented; required tests follow.

Future adversarial checks must cover:

1. Forged Approved decisions/evidence records, authority labels, source labels and timestamps;
   altered payload after capture; wrong provider/scope/ID, season or ordered participants.
   Every case fails closed at the relevant boundary.
2. Batch permutations and manipulated retrieval/header dates preserve outcomes/reasons;
   complementary partial score pairs never splice, and disputed fields block the whole candidate.
3. Accepted verified r10 then changed r9 is rejected; r10 replay makes no mutation; r10 divergent
   content is quarantined. r11 with equal canonical values does not silently advance provenance.
   Epoch resets, timestamp ties and incomparable streams remain unverified/incomparable.
4. Cross-request conflicts and operator barriers persist. New receipts/conflicts invalidate
   decisions through protected evidence generations, including during a concurrent acceptance.
5. Two writers expecting v5 produce at most one commit; ABA v5→v6→v7 with restored values still
   rejects v5. Mapping phantoms/remaps, evidence/configuration/revocation races abort or revalidate.
6. Wrong candidate, principal, match, provider, baseline, policy, action, field or clear semantics;
   unauthenticated/unauthorized, expired, revoked or reused grants never authorize a write.
7. Inject failures/cancellation at every future transaction step: metadata/history/audit/grant/
   idempotency all roll back. A lost commit acknowledgement resolves through durable lookup;
   same key/different operation rejects, retries are bounded and stale human approval is renewed.

Use pure deterministic fixtures for evidence/policy checks and mocked fixed-origin transport
for receipt binding; empirical provider captures cannot prove universal guarantees. In a later
explicitly approved persistence phase, use isolated PostgreSQL integration tests with controlled
concurrent transactions and failure injection for CAS, phantoms, revocation and atomic rollback.
Offline policy tests alone cannot establish those database guarantees.

Verification scope: documentation and source inspection only. The reported existing baseline
is 744 passing unit tests and 179 passing offline integration tests, as recorded in Phase 2.9B;
this documentation phase does not claim a fresh test run. No live database checks are needed
or authorized. Documentation verification: `git diff --check`, explicit untracked-file whitespace
and Markdown local-link checks passed; only this new document is changed.
