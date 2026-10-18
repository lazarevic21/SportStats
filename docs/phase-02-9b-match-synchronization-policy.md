# Phase 2.9B — Pure match synchronization policy

The application policy engine implements the conservative, read-only portion of the
[Phase 2.9A design](match-synchronization-policy.md). It invokes the existing
`MatchMetadataChangeEvaluator` internally for identity validation, differences and batch
contradictions. It does not accept externally constructed evaluation results as authority.

The contracts and engine live in `SportStats.Application.Football.MatchSynchronization`.
Use `MatchSynchronizationPolicyEngine.Evaluate(MatchSynchronizationPolicyInput)` for a
batch, or the single-observation/`EvaluateBatch` convenience methods. Supply observations
and the existing trusted `MatchMetadataIdentitySnapshot`; no evidence/approval input exists.
An empty batch returns no decisions. Null API arguments throw `ArgumentNullException`,
while malformed observation values receive the evaluator's structured invalid-input result.

The output retains baseline, original observations, evaluator results, per-observation
candidates and generic typed field assessments with Before/After/Presence. Collection
snapshots are read-only. Field reason lists carry the observation's aggregate blockers
across all five fields to explain atomicity; they do not attribute each blocker to that
individual field. `ObservationIndex` identifies the associated retained observation.

## Evidence and authorization limit

There is no independently verifiable provider ordering, score-semantic or overwrite-authority
contract in the repository. Consequently `Approved` is a reserved outcome and cannot be
returned by this implementation. There is no evidence-input flag, timestamp, provider priority
or operator-approval assertion that enables it. Unverified observations cannot become trusted
by repetition, apparent status progression, goal direction, later retrieval or provider
`lastUpdated`. HTTP server date is not a policy input or an ordering source.

Every result has `IsUpdateAuthorized == false` and unverified evidence. Even a future policy
implementation capable of returning `Approved` would only establish eligibility against an
exact baseline; it would require separate persistence authorization and atomic revalidation.
The current trusted identity snapshot remains an application trust boundary, not an
authentication mechanism. Never construct canonical snapshots/mappings from provider payloads.

## Implemented rules

- Invalid input and exact identity failures are `Rejected`; changes to canonical season or
  ordered participants remain immutable relationship failures inherited from the evaluator.
- Contradictory alias/provider proposals in the evaluated batch are `RequiresReview` for
  the match, including when one conflicting proposal equals the baseline.
- Equal explicit proposals and Missing-only observations yield `NoChange` after validation
  and conflict gates. Explicit Null equal to a nullable baseline is also unchanged; zero is
  a Value. NoChange neither advances evidence nor authorizes a write.
- Ordinary Scheduled → Live, Scheduled/Live → Finished, Scheduled rescheduling/matchday
  proposals and Live score changes yield `InsufficientEvidence`. Finalization requires an
  explicit nonnull score pair; a partial live score proposal also lacks required evidence.
  The presence of a complete pair does not make its semantics or authority verified.
- Changed explicit nullable clears, changes to Finished matches, exceptional
  Postponed/Suspended/Cancelled states, supported reversals/initialization from Unknown,
  and kickoff/matchday corrections during Live yield `RequiresReview`. A score proposal
  against Scheduled without an ordinary transition requires review rather than inferring Live.
- Observed Unknown, undefined or Null status is `Rejected`. Unsupported provider statuses
  such as AWARDED must normalize to Unknown; raw status is retained as diagnostics and is
  not independently reparsed by the policy.

Decisions are atomic per known canonical match, with per-observation candidates and typed
per-field details. Candidates overlay only that observation on its baseline; aliases are
never spliced together. A candidate is diagnostic data, not a subset authorized for update.
Known-match rejection dominates review, which dominates insufficient evidence, then no change.
Unresolved observations whose evaluator result has no canonical ID remain separate rejected
decisions; the policy does not repeat identity resolution to guess their match.

## Scope relative to the design

The Phase 2.9A contract examples describe future verified evidence, adjudication, persistence
grants, revision comparisons, audit storage and metadata versions. Those are deliberately
not implemented as caller-populated claims. No policy path can establish Newer, SameRevision
or Older, settle an authoritative correction or advance accepted evidence today. A version
contract and accepted canonical baseline must be established before adding those paths.
Conflicting evidence is currently represented by evaluator batch contradictions; cross-request
conflict history requires future durable evidence support.

The engine has no clock, generated decision IDs, database/HTTP dependencies, persistence
registration or asynchronous I/O. Actor, decision time, durable observation IDs/digests and
retention belong to a future audit boundary. The fixed policy version identifies rules only.
Observation indexes link field details/candidates to the retained per-decision input order;
they never establish ordering evidence. Equivalent reordered inputs have the same outcome
and stable reason set, while their audit indexes and evaluator diagnostics may reflect input order.

## Reviews and next phase

Independent Security review found no blocking issue: evaluator invocation is internal,
Approved is unreachable, outputs cannot authorize persistence and collections are frozen.
Public output records can still be constructed by callers as diagnostic DTOs; a future
writer must never authenticate them by inspecting their outcome. Database review found
no blocker or persistence dependency; unresolved identities cannot be guessed into a
match group, and no snapshot/version is represented as write authority.

Independent QA review found no blockers. The 91 new policy cases cover every canonical
status pair, presence/zero/replay behavior, final and live score completeness, identity and
alias/provider conflicts, atomic decisions, deterministic reasons, frozen collections and
unreachable approval. Reflection checks constrain the public dependency/input surface;
source review additionally confirms no clock, database or HTTP calls. These checks do not
establish provider guarantees or durable conflict history.

Verification for this phase:

| Check | Result |
| --- | --- |
| `dotnet build SportStats.slnx --no-restore` | Passed; 0 warnings, 0 errors. |
| Complete unit suite (`--no-build --no-restore`) | 744 passed, 0 failed, 0 skipped (includes 91 new policy cases). |
| Offline integration suite (`--no-build --no-restore --filter 'Category!=PostgreSQLLive'`) | 179 passed, 0 failed, 0 skipped. |
| `git diff --check` and explicit whitespace checks of new untracked files | Passed. |

No live PostgreSQL or provider requests were made. No existing evaluator, domain,
infrastructure, schema, dependency or runtime registration changed. The existing Phase
2.9A document remains the design baseline; this phase adds its implementation notes,
two application source files and one unit-test file.

The design's future persistence precautions remain prerequisites:
metadata version checks, exact mapping protection (including duplicate phantoms), accepted
evidence/configuration protection, separate scoped authorization, atomic status/score and
audit/idempotency commit, and fresh reevaluation after conflicts. ReadCommitted evaluation
remains best effort and cannot be reused as a write permit. No persisted expected version
or accepted-evidence baseline exists; even retained before/after values cannot detect an
intervening update that restores the same values (ABA). Cross-request conflicts remain
undetectable without future evidence state.

Recommended next phase: establish and review actual provider ordering/score contracts and
product authority/correction rules, then design verified evidence and canonical-baseline
adoption. A synchronization writer still requires explicit approval and a separate scope.
