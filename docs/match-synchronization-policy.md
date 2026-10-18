# Match synchronization policy — Phase 2.9A

Status: proposed architecture and policy design for `phase/02-backend`. This document
does not implement or authorize a synchronization writer, metadata updates, migrations,
jobs, provider requests or database writes. Writer implementation requires explicit approval.

## Repository evidence and limits

This design follows the actual Phase 2.7–2.8 contracts, rather than treating a successful
provider request or metadata difference as permission to change canonical data.

| Source | Implemented guarantee or limit |
| --- | --- |
| [Observation contract](../src/SportStats.Application/Football/Ingestion/MatchProviderObservation.cs) | `Missing`, `Null`, `Value` for kickoff, matchday, status, both goals and provider last-update; provider identity/source, raw status and UTC retrieval time. Provenance is not authorization. |
| [Observation normalizer](../src/SportStats.Infrastructure/FootballData/FootballDataObservationNormalizer.cs) | Current observation adapter supports PL; validates timestamps, nonnegative goals and positive matchdays. `SCHEDULED`/`TIMED` → `Scheduled`; `IN_PLAY`/`PAUSED` → `Live`; `FINISHED`, `POSTPONED`, `SUSPENDED`, `CANCELLED` map to their canonical equivalents. Other tokens, including `AWARDED` and the `LIVE` query-filter token, become `Unknown`. |
| [Domain status](../src/SportStats.Domain/Football/FootballMatchStatus.cs) | `Unknown`, `Scheduled`, `Live`, `Finished`, `Postponed`, `Cancelled`, `Suspended`. There is no `InPlay` or `Awarded` member. `Live` loses the distinction between playing and paused. |
| [Match](../src/SportStats.Domain/Football/FootballMatch.cs), [score](../src/SportStats.Domain/Football/FootballScore.cs) | Private setters, no metadata update operation; independently nullable nonnegative home/away goals. No period, extra-time, shootout or awarded-result semantics. |
| [Evaluator](../src/SportStats.Application/Football/MatchMetadata/MatchMetadataChangeEvaluator.cs), [contracts](../src/SportStats.Application/Football/MatchMetadata/MatchMetadataContracts.cs) | Exact existing mappings; no natural-key fallback or identity creation. Immutable canonical ID, season and ordered participants. Null/unknown/undefined observed status is invalid. Typed changes are proposals; freshness is always unverified and update authorization always false. |
| [Identity reader](../src/SportStats.Infrastructure/Persistence/MatchMetadata/EfMatchMetadataIdentityReader.cs), [service](../src/SportStats.Infrastructure/Persistence/MatchMetadata/MatchMetadataEvaluationService.cs) | At most six sequential no-tracking queries; no owned transaction or locks. Best-effort consistency; batch evaluation preserves alias contradictions and input result order. |
| [EF configuration](../src/SportStats.Infrastructure/Persistence/EntityConfigurations.cs) | Distinct-team and nonnegative-goal checks, required status string, season/team membership foreign keys. External identity tuple indexes are nonunique. No metadata concurrency token, accepted revision, synchronization audit or idempotency ledger. |
| [Identity resolution contract](../src/SportStats.Application/Football/IdentityResolution/IdentityResolutionContracts.cs) and persistence implementation | Existing match creation/linking is separate from metadata synchronization. Caller-owned ReadCommitted transaction and cooperating advisory-lock protocol do not automatically protect read-only evaluation. |
| [HTTP contract](../src/SportStats.Application/Football/Ingestion/IFootballDataClient.cs) | Allowlisted HTTP status, server date and response size; no revision/ETag ordering contract. Transport success does not validate match semantics. |

The observation normalizer reads only `score.fullTime.home/away`. Their availability or
name does not prove an authoritative live-score or final-score interpretation for every
status. Omitted score containers produce Missing children; explicit null containers are
rejected because they do not establish individual goal clears. Individual null goal
members are preserved. Raw provider status is diagnostic: the policy must not add a
second parser that contradicts the registered adapter's normalized status.

See [read-only evaluation](match-metadata-evaluation.md), [PostgreSQL evaluation](phase-02-8f-postgresql-evaluation.md),
[HTTP client](football-data-client.md) and [provider validation](provider-validation.md).
Successful live validation and a pair of observations establish observed behavior, not
monotonicity, correction authority or a complete provider revision contract. Some earlier
planning documents describe intended features; the contracts above govern this design.

## Separate responsibilities

1. **Trusted identity resolution:** load persisted exact `(ProviderKey, IdentityScope,
   ExternalId)` mappings and canonical relationships. Require exactly one mapping for
   match, season, home and away; preserve duplicate rows as evidence of failure. Never
   infer identity from kickoff, names, score or matchday, and never create/reparent here.
2. **Metadata change detection:** use the existing pure evaluator to identify typed
   differences and batch contradictions. This proves neither freshness nor permission.
3. **Evidence assessment:** trusted application services verify provenance, ordering
   contracts, comparison scope and action authority against an accepted baseline.
4. **Policy decision:** a pure, deterministic function evaluates a complete candidate
   against a fixed baseline, supplied verified evidence and a versioned ruleset.
5. **Persistence authorization:** a separate future boundary authenticates the actor,
   validates a narrowly scoped grant and rechecks identity, evidence and current state
   atomically with any write. A policy result is never such a grant.

Provider payloads and arbitrary callers cannot designate their own trusted snapshots,
verified revisions, authority, actor identity, policy version or approval. Current records
are in-process data contracts, not authentication mechanisms. Registered ingestion code
must establish provenance; provider labels alone cannot establish trust.

## Outcomes and deterministic precedence

Keep the proposed five names in a new policy contract; do not rename the evaluator's
existing `Unchanged`, `ChangesDetected`, `Conflict`, `InvalidInput` dispositions.

| Policy outcome | Exact meaning |
| --- | --- |
| `NoChange` | Valid identity/input, no unresolved batch contradiction, and all explicit proposals equal the canonical values. Missing makes no proposal. No write, freshness claim or accepted-revision advancement follows. |
| `Approved` | The entire exact candidate satisfies this policy against the exact baseline, with sufficient verified evidence and action authority (or a specific authorized adjudication). Advisory eligibility only; `IsUpdateAuthorized` remains false. |
| `RequiresReview` | A representable proposal has a substantive conflict, exceptional transition or correction requiring adjudication/product rules. Review is not permission to write. |
| `Rejected` | Invalid/unsupported input, identity failure, immutable relationship change, proven stale changed proposal, or a policy prohibition. A metadata reviewer cannot override identity invariants or unsupported representation. |
| `InsufficientEvidence` | A representable ordinary change lacks evidence needed to establish ordering, provenance, field semantics or authority. Fetching it again does not repair that gap by itself. |

Apply these gates in order, retaining all safe diagnostic reasons:

1. Input/adapter validity and exact identity/immutable relationship checks. Failures →
   `Rejected`. Map identity-related evaluator conflicts to this gate, distinguishing them
   by failure details from contradictory metadata. Missing mappings require the separate
   identity workflow, not a guessed match or metadata override.
2. Contradictory metadata within a batch or unresolved relevant evidence across requests
   → `RequiresReview` for the whole match. No selection by arrival order or timestamps.
   Existing evaluator batch conflicts cannot be bypassed by filtering out a losing row.
3. No effective changes → `NoChange`, even if chronology is unknown or the observation is
   old. Record evidence limitations; do not advance a watermark or refresh a finality claim.
4. Verified older changed proposal → `Rejected`. Same verified revision with divergent
   content → `RequiresReview` (provider contract violation), not a retry or overwrite.
5. Finished corrections, reversals, nullable clears and exceptional transitions described
   below → `RequiresReview` unless an exact, verified adjudication resolves their reasons.
6. Ordinary changed candidates with missing/unverified/incomparable evidence →
   `InsufficientEvidence`. Denied authority → `Rejected`; unknown authority is insufficient.
7. Only a coherent, fully evidenced candidate satisfying all applicable rules → `Approved`.

A review resolution is a new evidence input and produces a new decision; the old decision
is never toggled to approved. A valid resolution may settle conflicting observations, but
must identify them, explain the choice and retain the original conflict. Re-evaluate a
separately adjudicated candidate; do not reinterpret the original evaluator conflict as success.
Unsupported states and immutable changes require another design/workflow, not an override.

Determinism means identical baseline, observations, evidence and policy version yield the
same outcome, candidate and reason set. Inject decision time/ID; do not use the wall clock,
input ordering or generated IDs to choose winners. Sort diagnostic sets by stable keys.

## Proposed typed contracts (design only)

The following is contract notation, not compiled C# or an instruction to add these types.
All collections must be immutable snapshots. Evidence assertions are constructed only
through trusted verification boundaries, not deserialized as trusted facts.

```text
MatchSynchronizationOutcome = NoChange | Approved | RequiresReview |
                              Rejected | InsufficientEvidence
OrderingRelation = Unverified | Newer | SameRevision | Older |
                   Incomparable | Contradictory
AuthorityDisposition = Unverified | Permitted | Denied

ObservationReference {
  ObservationId, ProviderIdentity, Source, IngestionContextReference,
  NormalizedObservation, PresencePreservingDigest,
  PayloadEvidenceReference?, TransportMetadataReference?
}
PolicyBaseline {
  MatchMetadataSnapshot,
  ExpectedMetadataVersion?, ExactMappingDependencies,
  AcceptedEvidenceReferences, AuthorityConfigurationVersion
}
EvidenceAssessment {
  OrderingRelation, ComparisonScope, VerifiedContractReference?,
  BaselineEvidenceReference?, ProposedRevision?, EvidenceReferences,
  AuthorityDisposition, AuthorizedActionAndFieldScope,
  ScoreSemanticContractReference?, AdjudicationReference?, ReasonCodes
}
AtomicMatchCandidate {
  ImmutableMatchTuple, ResultingMatchMetadataSnapshot,
  PresenceAwareProposal, TypedChanges, SupportingObservationReferences
}
FieldAssessment { MatchMetadataField, Presence, TypedBefore, TypedAfter,
                  EvidenceReferences, ReasonCodes, BlockingReasons }
MatchSynchronizationDecision {
  DecisionId, MatchId?, PolicyBaseline?, ObservationReferences,
  EvaluatorResults, EvidenceAssessment, FieldAssessments,
  AtomicMatchCandidate?, Outcome, ReasonCodes, PolicyVersion, DecidedAtUtc,
  IsUpdateAuthorized = false
}

// Separate FUTURE application authorization; not a property of provider input.
PersistenceAuthorization {
  GrantId, AuthenticatedActor, DecisionAndCandidateDigest,
  ImmutableMatchTuple, ExpectedMetadataVersion, MappingDependencies,
  ExpectedEvidenceState, PermittedActionAndFields,
  ApprovalEvidenceReferences, PolicyAndAuthorityVersions,
  ValidityAndRevocationConditions
}
```

Use the existing generic `MatchMetadataChange<T>` and `MatchObservationField<T>` for
typed values, not strings or untyped object bags. Retain the original observations,
including explicit unchanged fields: a diff alone cannot prove presence or coherence.
`ProviderLastUpdatedUtc` remains evidence with Missing/Null/Value, not a sixth mutable
canonical metadata field. Nullable version notation above reflects today's missing
contract; a future version-based writer must require a real expected version.

Suggested stable reason families: `Identity.*`, `Input.*`, `Ordering.*`, `Authority.*`,
`Conflict.*`, `Semantics.*`, `Transition.*`, `Correction.*`, `Concurrency.*`. Examples:
`Ordering.Unverified`, `Conflict.SameRevisionDifferentContent`,
`Correction.FinishedRequiresAdjudication`. Text diagnostics do not control decisions.

## Trust, ordering and authorization

Freshness here is a scoped relation to the evidence supporting the current canonical
state, not an age or a status ranking. A newer observation is not necessarily correct,
complete, or authorized to overwrite that state.

| Evidence | Permitted conclusion |
| --- | --- |
| Local retrieval time, HTTP server date, request arrival/completion order | Operational provenance only; never match ordering. |
| Status progression, increasing goals, repeated identical responses, agreement/majority | Neither ordering nor independent authority. Goals can decrease after corrections. |
| Provider `lastUpdated` today | Unverified provider assertion, even if it increases or two live requests agree. Missing/null cannot be treated as zero, oldest, newest or a clear instruction. |
| Explicit provider guarantees | Eligible for trust only after recording the exact authoritative guarantee, endpoint/entity/field scope, precision, ties, correction behavior, reset rules and applicability. A generic last-update description is insufficient. |
| Verified version/revision contract | May establish Newer/SameRevision/Older inside its verified stream/epoch and coverage, compared with a persisted accepted baseline. Opaque ETags or versions establish no order unless specified; equal versions with different semantic content are contradictions. |
| Independently confirmed authoritative correction | May resolve a particular candidate when the source, authority, independence and affected fields are verified; does not establish global chronology. |
| Explicit operator adjudication | May resolve an exact representable proposal if the authenticated actor has that permission. Records acceptance despite unknown chronology; never changes Unverified to Newer by assertion. |

Evidence promotion requires a reviewed, versioned contract artifact, verified source
binding, applicability tests and an explicit authority configuration. Record who verified
it and why; empirical examples supplement, but cannot prove, universal guarantees.
Track revisions by provider, identity/alias scope, stream/epoch, covered fields and match.
Never compare revision numbers across providers or aliases without a verified common
revision space. A collection revision is not automatically a per-match revision.

Technical ordering and overwrite authority are separate checks. A provider can be ordered
but unauthorized for final corrections; an operator can authorize a precise correction
without proving provider ordering. Such adjudication must bind the current state and all
conflicting evidence, rather than relabel a proven old observation as fresh.

Canonical data currently has no persisted accepted-evidence baseline. An initial adoption
must therefore establish that baseline through explicit authoritative confirmation or
operator adjudication; a numerically large first revision is not proof it supersedes
existing canonical data. Preserve manual-correction provenance so later provider revisions
cannot silently undo it. Missing, contradictory, unverifiable or incomparable evidence
preserves existing canonical values and blocks automatic acceptance of changed values.

## Atomicity and metadata rules

Approve **one atomic candidate per canonical match**, with per-field diagnostics. Overlay
Missing onto the baseline, preserve Null as an explicit nullable clear, and Value as a
replacement including zero. Never apply the approved-looking fields from a blocked
proposal. This is conservative when an unrelated matchday change accompanies a disputed
score, but prevents mixed status/score states and makes approvals/audits reproducible.
Matches in a batch may have separate decisions; no cross-match all-or-nothing rule is
implied. Do not splice complementary alias/provider observations into a synthetic proposal
without verified coherent revision coverage or explicit adjudication.

For the proposed initial policy:

- `Scheduled → Live`: ordinary candidate only with trusted superseding evidence and
  authority covering the transition. Missing goals can retain a null/null baseline; if
  goals are supplied or retained nonnull, their applicable score meaning must be verified.
- `Live → Finished` and `Scheduled → Finished`: representable, including missed live
  polling, but require both goals explicitly supplied as nonnull Values in one coherent
  final observation, with verified final-score meaning and action authority. Do not
  finalize by silently retaining potentially stale live goals. A missing member, or a null
  member already null in the baseline, → `InsufficientEvidence` absent an earlier blocker.
  A null clearing a stored goal → `RequiresReview` under the clear rule. No invented zero
  or draw. This is a new policy requirement,
  stronger than today's domain/evaluator, not a claim that final completeness is enforced.
- Kickoff rescheduling and matchday changes while Scheduled: ordinary candidates subject
  to ordering and field authority. No natural-key rematching. During Live or Finished,
  changing these is an exceptional metadata correction → `RequiresReview`.
- Score changes while Live: ordinary candidates only after verifying that the observed
  score fields apply to that state. Require a coherent explicit pair for a changed score;
  a partial pair → `InsufficientEvidence`, even though the domain permits partial scores.
  An unchanged supplied counterpart still counts as explicit evidence. Increasing and
  decreasing goals use the same evidence gates; neither direction proves freshness.
- Score changes while Scheduled or with missing observed status against a Scheduled
  baseline: `RequiresReview` pending score/status semantics; do not infer Live from goals.
- Any change to a Finished match (including kickoff/matchday), exit from Finished, or
  changed score with Status Missing against Finished: `RequiresReview`. A higher ordinary
  revision alone is insufficient. A correction needs specific authoritative confirmation
  or authorized adjudication, full coherent score pair when scores change, and the same
  concurrency/authorization gates. Finished is not proof of permanent finality.
- Entry to, exit from, or score changes in Postponed/Suspended/Cancelled: representable
  but `RequiresReview` until product rules specify allowed actions and score retention.
  A postponed fixture may resume as Scheduled or Live; a suspended match may resume Live;
  neither is automatically safe. Cancellation is not an instruction to clear scores.
  Kickoff/matchday-only changes in these states also require review initially.
- Other supported reversals (for example Live → Scheduled), or initialization from a
  canonical Unknown baseline: `RequiresReview`. Do not rank the enum numerically or
  extrapolate an unapproved transition graph. Unlisted transitions default to review.
- Observed Unknown, unsupported, undefined or Null status: `Rejected`, even alongside
  otherwise usable metadata. Do not salvage a subset. `AWARDED` requires a future domain,
  score, adapter and product decision; it must not be forced to Finished or Cancelled.
- A changed explicit Null for kickoff, matchday or either goal: `RequiresReview`, with
  field-specific clear authority and coherence checks. A null clear can be legitimate,
  but is not made safe by recency. Status Null remains invalid. Already-null explicit
  nullable fields contribute no change; valid all-equal observations yield `NoChange`.
- Status Missing preserves the baseline; it is not Unknown. Apply that baseline's rules
  to any score proposal. Missing all fields cannot establish freshness or completeness.

These proposed score-completeness rules avoid claiming unsupported period semantics.
Unknown duration/extra-time/penalty interpretation remains `InsufficientEvidence` where
it affects an ordinary candidate, or a review blocker on exceptional corrections.
An authorized adjudication must state the representable score interpretation explicitly;
it cannot squeeze an awarded or shootout result into an invented canonical meaning.

## Conflicts and repeated observations

The current batch evaluator marks all otherwise valid observations for one canonical
match as conflicting if any explicit metadata proposals disagree, including a proposal
equal to the baseline. Missing imposes no proposal; explicit Null versus Value conflicts.
This covers aliases and different providers when their exact mappings target that match.
It does not establish conflict history across requests.

A future evidence store must check relevant outstanding observations and accepted
provenance across requests. Do not let sequential ingestion bypass a conflict by placing
observations in separate batches. Conflicting aliases are not independent votes. Different
providers have no implicit priority: a reviewed provider/field/action authority policy or
specific adjudication is required. Cross-provider revisions are incomparable by default.
Even an authoritative provider choice does not prove chronology over a manual correction.

Proven stale changed observations are rejected; incomparable ordinary proposals are
insufficient; explicit contradictions require review. If a verified stream proves an
earlier revision was superseded, a separate reviewed evidence-selection step can record
that fact and form a new candidate. The policy must not silently bypass current batch
conflict behavior or discard an unfavorable alias.

Repeated valid identical proposals produce `NoChange`. They do not accumulate authority,
advance watermarks, extend approvals or generate writes. Revision-only evidence adoption,
if later needed, is a separate explicitly designed operation; metadata NoChange is not
permission for that side effect.

## Future persistence and concurrency boundary

Read-only evaluation is best effort under normal ReadCommitted behavior. Mapping, match
and relationship statements may observe different committed states. No tracking does
not mean a repeatable snapshot; even a repeatable snapshot would not prove freshness,
actor permission or that the state remains current at write time. An old `Approved`
decision cannot be reused as unquestioned write authorization.

Recommended future design, requiring separate implementation and schema approval:

1. A dedicated context and explicit transaction with clear commit ownership. Acquire
   the existing football coordination/identity advisory locks in their established order
   before resolving mappings, then canonical-match/evidence protection in a documented
   common order. Retain protection through commit; all aliases/providers serialize on
   the same canonical match. Review compatibility with all mapping writers first.
2. Reload exact mappings, canonical state and required relationships inside this boundary.
   Recheck duplicate/ambiguous mappings and immutable tuple. Protect mapping dependencies:
   locking existing rows alone does not prevent a duplicate mapping phantom because tuple
   indexes are nonunique. Require cooperating advisory coordination for every mapping
   writer, or a separately approved constraint/isolation design. Fail closed if that
   coordination guarantee is unavailable; do not claim row locks alone solve it.
3. Prefer an explicit monotonically increasing metadata version, covering every metadata
   writer. Validate the expected version **and** immutable tuple/full expected state.
   Lock/check accepted evidence, authority configuration and grant/revocation dependencies
   too. Match version alone does not protect them. A null-safe full-state conditional
   update is an alternative requiring review; it misses intervening changes restored to
   the same values (ABA) and cannot provide durable audit/idempotency by itself.
4. Re-evaluate evidence and policy against the protected current state. Validate the
   authenticated actor's separate persistence grant, exact candidate/decision digest,
   field/action scope, policy version and validity/revocation conditions. A review grant
   for a different baseline cannot be reused. No provider call inside the transaction.
5. Use one conditional match update checking expected version and relationships; increment
   version once and validate the entire status/score candidate atomically. Zero affected
   rows means concurrency conflict, never success. Do not use the insert/link identity
   persistence session as a metadata writer or attach an old evaluation snapshot as Modified.
6. Commit metadata, accepted-evidence state, consumed grant/idempotency result and audit
   event atomically. Any failure rolls back the complete operation. An outer transaction's
   owner must commit before reporting durable success; savepoint success is provisional.
7. On conflict, roll back and perform a fresh bounded re-evaluation. Never replay an old
   approval or blindly retry the write. Human grants tied to the previous version require
   renewed review. Technical retries must rerun all gates and stop after a configured
   finite limit, returning a conflict for review; the retry limit is a later operational
   choice. Cancellation/failures must not leave partial status/score or audit state.

Durable idempotency requires a unique operation identity binding the canonical match,
provider/revision scope when verified, presence-preserving candidate digest, expected
version and authorization context. Same key/different content is rejected. Same key/same
content returns the recorded committed result without another update/version increment.
Do not use provider lastUpdated, raw payload hash or provider ID alone as a universal key.
If commit acknowledgement is lost, query the durable idempotency result before retrying;
report unknown outcome until resolved rather than claiming failure or applying twice.

Database foreign keys and private domain setters are not database-level immutability or
authorization enforcement. The future writer must allowlist metadata columns and never
write IDs, season or ordered teams. Current role UPDATE privileges, where provisioned,
are technical capability, not product authorization. No privileges change in this phase.

## Decision matrix

All rows assume exact identity/input validity unless stated otherwise. `Approved` always
means advisory eligibility only. “Verified evidence” includes comparison to the accepted
baseline, applicable field semantics and action authority, not just a newer timestamp.

| Scenario | Expected outcome | Reason |
| --- | --- | --- |
| Valid identical response repeated, including zero goals | NoChange | Equal explicit values; repetition proves no freshness. |
| All metadata Missing | NoChange | No replacement or clear proposal. |
| Null kickoff already null; other explicit values equal | NoChange | No effective change, not evidence adoption. |
| Scheduled → IN_PLAY with only increasing lastUpdated | InsufficientEvidence | Canonical Live is supported; timestamp ordering is unverified. |
| Scheduled → Live, null/null baseline goals and no score supplied, verified transition evidence | Approved | No score invented; coherent permitted candidate. |
| PAUSED after IN_PLAY with same goals and metadata | NoChange | Both normalize to Live; no separate pause state. |
| Live → Finished with coherent explicit final pair and verified final evidence | Approved | Complete representable final candidate. |
| Live → Finished with missing away goal, no other blockers | InsufficientEvidence | Cannot retain live score as verified final score. |
| Live → Finished with a null final member already null in the baseline | InsufficientEvidence | No destructive clear, but final score is incomplete. |
| Live → Finished with Null clearing a stored goal | RequiresReview | Destructive-clear review takes precedence over incomplete final evidence. |
| Scheduled → Finished with explicit 2–1 and verified final evidence | Approved | Missing intermediate polling is not a prohibition or freshness proof. |
| Scheduled → Finished with 2–1 but only later retrieval/server time | InsufficientEvidence | Operational times do not prove ordering/authority. |
| Scheduled kickoff moved a week, verified rescheduling authority/order | Approved | Mutable field; canonical identity unchanged. |
| Scheduled matchday changes, evidence unknown | InsufficientEvidence | Matchday difference is only a proposal. |
| Live score 0–0 → 1–0, trusted coherent pair and applicable live-score contract | Approved | All evidence gates pass; increase itself is irrelevant. |
| Live score 1–0 → 0–0 with only newer-looking lastUpdated | InsufficientEvidence | Could be correction or stale data; direction proves neither. |
| Same live reduction with verified superseding correction evidence and ordinary live authority | Approved | Legitimate ordered live correction is representable. |
| Live score changes with other side Missing | InsufficientEvidence | Initial policy requires explicit coherent pair. |
| Finished 2–1 → 1–1, even at a verified newer revision | RequiresReview | Ordinary ordering does not authorize a final correction. |
| Exact Finished correction independently confirmed and authorized for expected version | Approved | New decision records specific adjudication; chronology may remain unverified. |
| Finished kickoff or matchday correction | RequiresReview | Final-state metadata requires adjudication. |
| Scheduled → Postponed; Suspended → Live; Cancelled → Scheduled | RequiresReview | Representable exceptional transitions lack approved automatic rules. |
| Postponed with explicit kickoff Null | RequiresReview | No implicit clear; needs specific clear authority. |
| Live → Scheduled or canonical Unknown → Scheduled | RequiresReview | Reversal/initialization is not an inferred safe progression. |
| AWARDED, unknown token, undefined enum or explicit Status Null | Rejected | Unsupported/invalid canonical proposal; no partial salvage. |
| Goal Null differs from stored goal, other fields ordinary | RequiresReview | Destructive clear needs explicit scope and coherence. |
| Negative goal, malformed timestamp, null score container | Rejected | Adapter/input validation failure before policy approval. |
| Missing/duplicate/ambiguous identity mapping | Rejected | Exact trusted identity prerequisite failed. |
| Provider swaps participants or changes season | Rejected | Immutable relationship violation; reviewer cannot override here. |
| Two aliases conflict, including one equal to baseline | RequiresReview | Whole-match batch conflict; no last-arrival winner. |
| Different providers conflict, later HTTP response favors one | RequiresReview | No implicit precedence or cross-provider ordering. |
| Cross-provider changed observation, no explicit conflict but revisions incomparable | InsufficientEvidence | Cannot prove supersession of current canonical evidence. |
| Verified older revision proposes different kickoff | Rejected | Proven stale changed proposal. |
| Same verified revision has conflicting score content | RequiresReview | Contract contradiction; quarantine/review evidence. |
| Same verified old revision equals canonical data | NoChange | No write or watermark advancement; record old evidence. |
| Compatible partial aliases lack common coherent revision | InsufficientEvidence | Do not synthesize a complete score/final candidate. |
| Whole proposal includes safe kickoff and disputed score | RequiresReview | Atomic match decision blocks partial application. |
| Previously approved candidate meets a changed version at future write boundary | RequiresReview on new assessment | Old result/grant invalid; abort persistence and reassess. An ordinary newly changed candidate may instead lack evidence. |
| Committed idempotency key retried with identical authorized operation | Existing committed result | Persistence behavior, not a new policy approval or second write. |

## Audit and provenance

Future durable decisions and writes need separate linked records. A policy decision is
not a committed mutation. Capture:

- Canonical match/season/ordered participant IDs; exact provider identity tuples and alias
  mappings used; authenticated ingestion context and endpoint/contract scope.
- Observation ID, normalized fields and all presences, raw status safely encoded, provider
  timestamp presence/value, retrieval time and optional allowlisted HTTP metadata. Retain
  a presence-preserving digest and controlled evidence reference; raw payload retention
  requires a separate retention/access decision and is not needed in ordinary logs.
- Evidence references and verifier, contract versions, comparison scope/relation, accepted
  baseline evidence, action/field authority and any independent correction/operator grant.
- Complete before/after candidate, expected/resulting metadata versions, per-field reasons,
  overall outcome, stable reason codes, policy version and decision time.
- Authenticated requesting/approving/persisting actors or service principals, grant identity,
  idempotency identity, write/transaction outcome and committed time when applicable.

Protect audit integrity/access; log review rejections and failed authorization separately
from successful writes. Sanitize/bound provider-controlled strings, and exclude API tokens,
credential headers, connection strings and raw transport exception details. Decision time
and committed time are audit facts, never provider freshness evidence. Retention and
operator access roles remain explicit product/operations decisions.

## Multi-provider and sport scope

Keep provider-neutral policy/evidence contracts in the football application boundary and
provider-specific verification in registered adapters. Configure authority by provider,
competition/season scope, field and action; do not hardcode a global provider winner.
No generic sport-wide status ordinal, universal revision comparer or score algebra is
needed now. Basketball periods, overtime and corrections require basketball-specific
contracts later. Reuse outcome/audit/concurrency concepts only when actual requirements
justify it; never interpret football goals or Live/Finished as universal score semantics.

## Independent reviews and verification strategy

Four specialists reviewed the repository read-only; the Tech Lead integrated their
findings. No specialist changed code or executed database operations.

| Review | Findings and design response |
| --- | --- |
| Backend | Use canonical Live, reject unsupported Awarded, retain full observations alongside typed diffs. Separate evidence/authority from policy and authorization. Adopt atomic match candidate and explicit score-semantic prerequisites. |
| Database | Nonunique mapping indexes allow duplicate phantoms; current FKs/private setters do not enforce update immutability. Add future version/evidence/audit/idempotency persistence only after approval; coordinate mapping writers and atomic conditional updates. ReadCommitted evaluation cannot authorize writes. |
| Security | Source/provider records are caller assertions; do not accept payload-defined trust or approval. Ordering is not overwrite authority. Bind operator grants to exact identity, candidate, baseline, policy and evidence; block sequential alias/provider overwrite through durable evidence checks. |
| QA | Existing evaluator/normalizer coverage validates detection and presence, not policy approval or writer safety. Final draft review identified and resolved null-final-score precedence: destructive clears require review; missing/already-null final goals lack sufficient evidence. Future tests must exercise evidence gates, atomic candidates, exceptional states and concurrency without inventing provider guarantees. |

Existing unit tests in `FootballMatchObservationTests`, `MatchMetadataChangeEvaluatorTests`
and `MatchMetadataEvaluationServiceTests`, plus offline/live evaluation integration tests,
cover normalization, presence, exact identity, contradictions, read-only behavior and
best-effort orchestration. They do not prove this proposed policy or future persistence.

For a separately approved pure policy implementation, use the matrix above as table-driven
acceptance cases, with fixed evidence/time and real existing observation contracts. Add
permutations for alias/provider order, Missing versus Null versus zero, unchanged explicit
counterparts, unknown baselines and every status pair's default rule. Verify that changing
retrieval time, HTTP Date, request order, goal direction or unverified lastUpdated never
promotes a proposal; unchanged repeats never advance accepted evidence. Verify that each
decision remains unauthorizing, and that ordinary revision evidence cannot approve final
corrections or exceptional transitions. Test revision ties/content conflicts, stream resets,
first-baseline adoption, expired/revoked/scoped grants and unresolved cross-request conflicts.

A future writer requires guarded PostgreSQL tests for two competing approvals, alias/provider
contention, duplicate-mapping insertion between validation and update, stale/null-safe expected
state, ABA where relevant, evidence/config/grant changes, atomic status/score+audit rollback,
same/different-content idempotency retries, commit uncertainty, cancellation and bounded
conflict retries. Include an audit failure that rolls back the metadata write. Use only the
existing separately opted-in test-database safety model; no live writes are part of this phase.

This phase changes documentation only. Existing tests were not rerun, as requested for
documentation-only work. The supplied prior totals (653 unit, 179 offline integration and
64/64 live PostgreSQL) are historical context, not a newly executed verification result.

## Readiness, open decisions and recommended sequence

| Requirement | Can implement without schema changes? | Remaining prerequisite |
| --- | --- | --- |
| Pure policy outcome/evidence contracts, presence overlay, reason codes, atomic diagnostics | Yes, in a separately approved read-only policy phase | Approve this design; keep existing evaluator semantics intact. |
| Conservative InsufficientEvidence/RequiresReview/Rejected/NoChange decisions | Yes | Trusted caller/evidence boundary and deterministic tests. |
| Provider-based Approved for changed values | Not from current evidence alone | Verified ordering and action/score contracts; accepted baseline evidence. |
| In-memory adjudication model | Design/pure evaluation only | Product roles, correction/clear powers, authenticated verification; no implicit write grant. |
| Durable cross-request conflicts, baseline adoption and manual-correction protection | No current persisted support | Approved evidence/provenance schema and authority policy. |
| Exceptional transitions and final corrections | Representable subset only | Product-approved transition/retention/score rules and adjudication process. |
| Awarded, pause distinction, period/extra-time/shootout scores | No current representation | Explicit domain/product and adapter contract decisions, separate schema review if needed. |
| Concurrency-safe writer, audit and idempotency | No complete current support | Version/evidence/audit/idempotency design, mapping coordination and explicit writer approval. |

Open product/evidence questions:

1. Which authoritative provider guarantee can order each mutable field, over what stream,
   and with what tie/reset/correction semantics? How is existing canonical evidence adopted?
2. What do `fullTime` values mean during Live and across completed extra-time/penalty matches
   in the supported competition? Which representable final scores does SportStats intend?
3. Which provider/actor may revise each field, clear values, settle conflicts, correct
   Finished data or reverse exceptional statuses? What prevents undoing manual corrections?
4. Should product later relax the explicit score-pair/final-completeness policy? What
   consistent partial-score states would then be allowed? Initial recommendation: retain it.
5. What are approval validity/revocation, reviewer roles, evidence/audit retention and
   initial canonical-baseline adoption procedures? No numerical freshness TTL is inferred.
6. Will every mapping/evidence writer cooperate with the lock protocol, or is a separately
   approved schema/isolation change required? Who owns transaction completion and retry limits?

Recommended next phase: **2.9B — pure read-only synchronization policy contracts and
scenario tests**, after design approval. Implement conservative decisions and verified-evidence
interfaces using synthetic evidence fixtures; do not manufacture provider guarantees or
persist updates. In parallel as a separately scoped activity, establish actual provider
ordering/score/authority evidence and resolve product decisions. Then design durable evidence,
versioning, audit and authorization persistence for review. Only after those gates and explicit
approval should a later phase implement and verify a synchronization writer. Scheduling and
automatic synchronization require their own scope; they do not follow from this document.
