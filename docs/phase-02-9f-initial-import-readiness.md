# Phase 2.9F: Initial import readiness review

Investigation and planning only, on `phase/02-backend`, 2026-10-18. No import is
authorized or executed by this document. The previous 2.9E recommendation for a
trusted-evidence design is historical context; this review scopes a separate,
create-only bootstrap, without implementing synchronization updates.

The repository has reusable create-only persistence foundations, but is **not yet
ready to run an initial import**. The smallest safe implementation is a developer
command for one explicitly selected PL season, a strict input/identity validation
and preview layer, and ordered calls to the existing persistence primitives inside
one outer transaction. Fixtures/results can subsequently be exposed from local
storage. Standings remain a separate blocked capability.

## Existing reusable components

| Component | Evidence and current behavior |
| --- | --- |
| Provider transport | [FootballDataClient.cs](../src/SportStats.Infrastructure/FootballData/FootballDataClient.cs), behind `IFootballDataClient`, fetches competition, teams and matches. Fixed HTTPS origin, constrained competition identifier, single attempt, cancellation, bounded body and timeout, sanitized failures. No persistence or season-selection parameter. |
| Normalization | [FootballDataNormalizer.cs](../src/SportStats.Infrastructure/FootballData/FootballDataNormalizer.cs) maps competition/season/team/match payloads without canonical ID allocation; validates counts, duplicate IDs, dates and nested competition assertions. It is PL-specific. Legacy match mapping recognizes only `TIMED` and `FINISHED`; other statuses become `Unknown`. |
| Observation parsing | [FootballDataObservationNormalizer.cs](../src/SportStats.Infrastructure/FootballData/FootballDataObservationNormalizer.cs) preserves Missing/Null/Value and raw status, supports more status tokens, but also returns `Unknown` for unsupported tokens. It is not a create-command mapper. |
| Identity contracts | [IdentityResolutionContracts.cs](../src/SportStats.Application/Football/IdentityResolution/IdentityResolutionContracts.cs) defines typed references, batches, dispositions and safe failures; [IdentityResolutionValidation.cs](../src/SportStats.Application/Football/IdentityResolution/IdentityResolutionValidation.cs) validates structural inputs. `IFootballIdentityResolver` has no production implementation or DI registration. |
| Identity planning | [FootballResolutionPlanner.cs](../src/SportStats.Infrastructure/Persistence/IdentityResolution/FootballResolutionPlanner.cs), discovery, plan comparison and materializer resolve exact opaque identities and validate parents/participants under advisory coordination. Names and kickoff times are never identity keys. |
| Canonical persistence | Internal [FootballResolutionOrchestrator.cs](../src/SportStats.Infrastructure/Persistence/IdentityResolution/FootballResolutionOrchestrator.cs) provides `PersistCompetitionsAsync`, `PersistSeasonsAsync`, `PersistTeamsAsync`, `PersistMatchesAsync`. Corresponding `CompetitionPersistenceSession`, `SeasonPersistenceSession`, `TeamPersistenceSession` and `MatchPersistenceSession` stage new canonical/mapping rows. Teams also ensure season membership. `ExecuteAsync` is read-only; no mixed-entity write entry point exists. |
| Transactions | [FootballResolutionTransaction.cs](../src/SportStats.Infrastructure/Persistence/IdentityResolution/FootballResolutionTransaction.cs) provides operation savepoints, rollback/cleanup and caller-owned transaction checks. Results remain provisional until caller commit. |
| Metadata analysis | [MatchMetadataEvaluationService.cs](../src/SportStats.Infrastructure/Persistence/MatchMetadata/MatchMetadataEvaluationService.cs), evaluator and [policy engine](../src/SportStats.Application/Football/MatchSynchronization/MatchSynchronizationPolicyEngine.cs) provide read-only analysis. `Approved` remains unreachable and `IsUpdateAuthorized` remains false. |
| Fingerprints | [2.9E](phase-02-9e-observation-fingerprinting.md) implements content equality for unverified observations. A hash establishes neither provider authenticity nor update permission; it is not an import idempotency key. |
| Operator pattern | [ProviderValidation](../tools/SportStats.ProviderValidation/Program.cs) uses environment configuration and explicit live opt-in without a DB host. Reuse its safety patterns, not its command as an importer. |

## Missing implementation pieces and blocking decisions

1. A narrowly scoped application import use case and developer command, with separate
   live-fetch, preview and create-only apply gates. Do not expose the internal
   orchestrator as an unrestricted writer or implement unrelated resolver features.
2. An immutable, bounded fetched-input bundle and cross-response validation: selected
   competition identity/code, identical season identity/key/dates, selected year,
   all match participants present in the team set, no duplicate identities or
   contradictory records. Count agreement checks payload consistency, not provider
   completeness; validate the intended season coverage and handle empty/incomplete
   sets explicitly. Do not hard-code 20 teams or 380 matches as universal rules.
3. A strict create mapper. Structural validation currently allows the defined
   `Unknown` status and does not enforce a complete status/score contract. For the
   minimum first release, admit only validated `TIMED`/Scheduled with both goals
   explicitly null, and `FINISHED` with both nonnegative goal Values and validated
   full-time semantics. Reject missing/partial scores, unsupported/unknown statuses,
   invalid matchdays, invalid timestamps and inconsistent relationships. Extending
   this allowlist requires explicit mapping tests; never silently coerce a status.
   Preserve Missing/Null/Value until validation; never replace missing goals with zero.
4. Sanitized preview/report contracts and existing-record comparison. Persistence
   skips existing mapped match writes but does not compare kickoff, status, matchday
   or goals. It cannot alone label a replay as metadata-identical. Compare those
   fields read-only, report differences as conflicts, and keep canonical values.
   Also report differing existing competition/team metadata or season dates without
   updating them. An identical replay is unchanged; a conflicting replay fails apply.
5. Confirm target database identity and application-role effective privileges, existing
   schema and an unambiguous existing `football` Sport. Do not create a Sport, migrate,
   grant privileges or use migrator credentials inside the importer. Confirm current
   provider access, permitted storage and request budget before an eventual live run;
   this review made no provider/account claims or requests.
6. Current HTTP calls do not send a season query. A year option validates the response;
   it does not select historical data. Initially support only a returned season that
   exactly matches the operator's selection across all three responses. Fail closed
   otherwise. Historical imports require a separately validated request contract.

## Proposed import sequence and developer workflow

The following flags describe a proposed interface, not an available command.

1. Require explicit `--competition PL --year YYYY`. Default to preview, require
   `--live` to make provider requests, and require a separate `--apply-create-only`
   to permit writes. Missing flags/configuration fail before side effects. Display
   selected target database and scope safely without connection-string credentials.
2. Read `FootballData__ApiToken` and `ConnectionStrings__SportStats` from process
   environment only. No token argument, secret file creation, raw payload logging,
   secret echo, automatic migrations, scheduler, startup import or public trigger.
3. Fetch competition, teams and matches sequentially: at most three attempts total,
   no automatic retries, no standings/scorers calls, no comparison recapture. Use
   configured conservative pacing at least matching the validator's seven-second
   minimum, subject to the verified account budget; seven seconds is not a quota
   guarantee. Stop on first failure including 401/403/429; report safe failure and
   Retry-After information without automatically retrying. Keep timeout/body caps.
4. Normalize and validate the complete input before opening a write transaction.
   Keep one in-memory input bundle for preview and apply within that invocation;
   do not fetch again while holding database locks. HTTP endpoints are not a
   transactional provider snapshot, so contradictory captures must fail validation.
5. Preview through a dedicated read-only/no-tracking database path, preferably an
   enforced read-only transaction, with no persistence calls, SaveChanges, speculative
   inserts or write-then-rollback simulation. Support symbolic parents for missing
   entities. Internal planning may inform this layer, but its current completion-only
   result is not a usable preview/report API. A preview never reserves IDs or grants
   permission to apply later. A later invocation fetches and validates anew.
6. If apply was explicitly requested and preview has no conflicts, open one fresh,
   caller-owned EF-enlisted PostgreSQL `ReadCommitted` transaction with a dedicated
   context and empty tracker. Resolve the existing football Sport; persist competition,
   then season, then teams/memberships, then matches. Each stage uses existing locked
   discovery/revalidation and fresh results, not externally supplied prepared plans.
   Match persistence requires dependencies already visible in this transaction.
7. Perform conflict comparison within the locked apply boundary, including existing
   matches skipped by persistence. Recheck before commit; the implementation must
   prove comparison and persistence share suitable locks for all cooperating writers.
   On any conflict/failure/cancellation, roll back the entire outer transaction.
   Commit once only after every stage succeeds. There is no partial-success mode.
8. Report per-type created/unchanged/conflicted counts, with separate membership and
   identity-mapping counts, safe field/reason diagnostics and a clear preview versus
   committed label. After rollback, report zero committed creations, distinguishing
   provisional work. On uncertain commit/connection outcome, report indeterminate;
   inspect identities through a fresh preview before retrying rather than claiming
   success or rollback. Exit nonzero on conflict or failure.

## Create-only guarantees and replay behavior

- Trust external identities only as derived by the approved adapter from the fixed
  provider endpoint plus validated selection and existing mappings. Normalized data
  types and fingerprints do not authenticate arbitrary caller input. No operator-
  supplied canonical GUID reassignment or arbitrary source URL is accepted.
- Preserve exact ordinal provider/scope/external-ID tuples and all existing canonical
  IDs. No display-name, kickoff or fuzzy matching, remapping, deletion or reparenting.
  An existing natural-key owner without the expected mapping is a conflict, not an
  invitation to link it. `LinkedExistingEntity` exists in the contract but does not
  establish an implemented automatic-link path.
- Create only missing competition, season, team, membership, match and mapping rows.
  Existing team plus missing membership can add only that membership. A mapped match
  must retain season and ordered participants; disagreement is a relationship conflict.
- Never overwrite existing match metadata, even if the new payload looks newer or
  includes a previously absent score. Report such differences. Do not feed policy
  candidates into persistence, fabricate review evidence or treat creation permission
  as update permission. Future updates remain a separately authorized synchronization
  feature subject to the policy engine; its current denial remains intact.
- Safe exact replay is supported by the foundations for cooperating writers: existing
  mapped entities are reused, existing metadata is not staged, duplicate/ambiguous
  mappings and natural-key ownership conflicts fail closed. The missing importer must
  prove end-to-end replay and reporting; foundations alone are not a runnable importer.

## Database and transaction considerations

[EntityConfigurations.cs](../src/SportStats.Infrastructure/Persistence/EntityConfigurations.cs)
and the existing migration define unique Sport code, competition `(SportId, Code)`,
season `(CompetitionId, Key)` and membership primary keys. Match composite foreign
keys require both teams to participate in the season. Checks reject self-matches
and negative goals; these do not enforce provider status/score semantics.

External mapping indexes on `(ProviderKey, IdentityScope, ExternalId)` are deliberately
**non-unique**. Advisory locks plus duplicate/ambiguity detection protect cooperating
persistence paths, not arbitrary SQL writers. The initial workflow must exclude
uncoordinated writers; broader integrity against them needs separate schema/privilege
design. No migration is established as necessary for this bounded cooperative path.

Each persistence call owns an operation savepoint, not the full import transaction.
A failed final match stage does not by itself undo earlier successful stages. The
importer must always roll back the outer transaction on any stage failure. Lock-phase
or cleanup failures can quarantine it; never continue/re-enlist a failed transaction.
Cleanup uses an independent bounded token; do not claim rollback when unconfirmed.
Success clears tracking and remains provisional until the one outer commit.

Current discovery reads full-table scalar snapshots repeatedly. Bound input size,
lock wait and overall runtime for the initial dataset, and measure before expanding.
Scoped discovery is a future improvement that must retain duplicate/conflict checks.

## Multi-league readiness

Transport already accepts a validated competition code without duplicated HTTP logic.
Full ingestion is **not configuration-only today**:

| PL assumption | Required generalization before another league |
| --- | --- |
| Normalizer rejects code other than `PL`; competition/team scope is `competition:PL`, season/match scope includes year | Inject one immutable validated competition context through shared parsing and identity construction. Preserve existing PL tuples byte-for-byte. |
| Observation source is `/v4/competitions/PL/matches` | Derive provenance from the same validated context and fixed origin, never arbitrary caller source strings. |
| Planner and season/team/match persistence revalidation explicitly require `PL` | Validate the selected canonical football competition consistently at every layer, retaining all parent checks and locks. Changing only HTTP or parsing is insufficient. |
| Provider validation skips non-PL match normalization | Add sanitized contract fixtures for each supported league and exercise the shared parser. |
| Season key uses start-year/end-year and year must equal start year | Verify that this convention fits each supported competition; preserve one explicit season per import. Planner currently limits a plan to one season. |
| Team identities are competition-scoped | Do not widen them to global IDs by assumption. Verify provider identity scope and define canonical cross-competition linking before imports that could duplicate the same club. No name matching or silent rekeying. |
| Status/full-time semantics and competition coverage observed for PL | Verify per-league payload/status/score/coverage behavior. Do not assume team counts, fixture counts, calendar, points rules or tiebreakers. |

Keep the first command PL-only. Generalize these seams before enabling La Liga,
Bundesliga or Serie A through a reviewed configuration allowlist; avoid copied
league-specific business logic. Cross-league identity behavior is a release gate,
not evidence that current competition-local identities are globally unique.

## Future minimum read-only API

[Program.cs](../src/SportStats.Api/Program.cs) currently maps only `/health`. Add
application query services and stable response DTOs in a later phase. Suggested
minimum routes use canonical UUIDs and read only PostgreSQL, with no provider calls:

| Route | Minimum behavior |
| --- | --- |
| `GET /api/competitions` | Football competitions with canonical ID, code and name. |
| `GET /api/competitions/{competitionId}/seasons` | Seasons with ID, key and dates. |
| `GET /api/seasons/{seasonId}/teams` | Season participants with canonical team IDs and display fields. |
| `GET /api/seasons/{seasonId}/matches` | Fixtures/results with ordered participants, nullable UTC kickoff, matchday, status and nullable goals; bounded paging and validated date/status filters. One resource can serve both views. |
| `GET /api/seasons/{seasonId}/standings` | Reserved future contract; expose unavailable explicitly until a validated standings source/calculation exists. Never fabricate a table from incomplete imports. |

Use no-tracking projections, deterministic ordering, capped paging, validated IDs,
safe errors and explicit empty versus missing-resource behavior. Do not promise
freshness: a create-only import becomes stale and cannot later add results to an
already imported fixture. Current canonical matches do not store a successful-import
timestamp; freshness/provenance needs a separate design if promised publicly.

Standings are blocked. `FootballStandingSnapshot` stores provider/retrieval/table
scope, but `FootballStandingRow` stores only position, not played/won/drawn/lost,
points or goal totals. There is no standings HTTP method, normalizer, import service
or calculator. Historical endpoint success is not an implemented contract. Before
computing standings, validate season completeness, final-score semantics, match
eligibility, deductions/adjustments, points rules and league-specific tiebreakers.
Choose validated provider snapshots or a separately tested rules engine; either
requires additional work. Initial fixtures/results availability need not wait for it.

## Test plan and guarded PostgreSQL strategy

Existing unit suites cover per-type persistence, planning, transactions, mapping,
HTTP, policy and fingerprints. Existing PostgreSQL scenarios cover individual
creation/replay/conflict/rollback and lock contention. These do not prove the missing
full import, preview or reporting behavior.

Add offline tests for:

- No live opt-in means zero HTTP calls; absent apply means zero writes. Use write-
  rejecting seams for preview, not only a rollback assertion.
- Exactly three sequential maximum attempts, pacing/cancellation, bounded payloads,
  first-failure stop, sanitized credential/error output and no automatic retry.
- Cross-response competition/season mismatch, duplicate IDs, partial/empty sets,
  missing teams, UTC/year mismatch, unsupported statuses, missing/partial/negative
  scores and invalid relationships prevent persistence entirely.
- Complete request mapping, symbolic preview parents, per-type/membership counts,
  exact replay, drift/conflict counts, zero committed counts after failure and
  indeterminate-outcome reporting. Policy remains unable to authorize updates.

Extend fixed scenarios inside
[PostgreSqlTestDatabaseFixture.cs](../tests/SportStats.IntegrationTests/PostgreSqlTestDatabaseFixture.cs)
and its scenario partials rather than export raw context/connection/transaction
capabilities. Retain `PostgreSQLLive`, explicit `SPORTSTATS_RUN_POSTGRES_TESTS=1`
and `SPORTSTATS_TEST_CONNECTION_STRING`; verify actual `sportstats_test`, current
and session user `sportstats_app`, restricted role/memberships and unchanged owned
session. Existing tests require preexisting football Sport and an unoccupied PL
scope. Never repurpose an occupied development database.

Proposed scenarios:

1. Full ordered graph creation and exact replay in one fixture-owned outer transaction;
   assert stable IDs, exact identity strings, counts and no duplicate memberships.
2. Seed existing mappings with changed metadata; assert explicit conflicts and unchanged
   persisted match fields, including null/zero scores and ordered participants.
3. Fail the final match stage after observing earlier inserts; assert outer rollback
   removes all fixture-created competition/season/team/membership/match/mapping rows.
4. Exercise duplicate/ambiguous mappings, unmapped natural-key owners, wrong parent,
   missing membership and concurrent cooperative imports; assert fail-closed behavior.
5. Exercise cancellation, lock timeout and cleanup failure; assert correct quarantine
   and outer rollback, with no continuation on unusable transactions.
6. Assert preview issues no DML and persists nothing, including mappings/memberships.

Retain the fixture's INSERT-table allowlist and rejection of UPDATE/DELETE/DDL,
`Rollback-only PostgreSQL` collection, `GuardedPostgreSqlFact`, commit refusal,
reopen refusal, bounded cleanup and rollback-only verification;
no database creation, migration or permanent-row cleanup. This infrastructure can
test transactional orchestration with an injected rollback-only completion boundary;
it cannot prove a real successful commit or durable replay after commit. Test the
command's commit-decision logic offline; any future durable-commit acceptance test
requires a separately approved disposable-database design, not disabling the guard.
Cross-league reuse tests follow the identity-scope decision, not an assumed global ID.

## Specialist review and recommended next phase

| Reviewer | Result |
| --- | --- |
| Backend | Reusable internal per-type persistence; no registered end-to-end importer. Flagged missing season query and PL restrictions across adapter and persistence. |
| Database | Cooperative replay foundations exist; non-unique mapping indexes and per-call savepoints require explicit coordination and full outer rollback. Missing read-only metadata comparison is an import reporting blocker. |
| Security | Fixed-origin client and validation CLI provide useful protections. Require separate fetch/apply opt-ins, environment credentials, strict statuses/relationships, bounded calls and no policy bypass. |
| QA | Existing per-entity and read-only tests do not prove whole-import atomicity, summary accuracy or zero-write preview. Extend fixed guarded scenarios without exposing database capabilities or weakening commit refusal; use offline seams for commit decisions. |

Recommended **Phase 2.9G: developer-controlled PL create-only import**: implement
only the strict mapper/validator, no-write preview/report, narrow application boundary
and one-season transactional command, plus the tests above. Keep existing metadata
immutable and synchronization authorization false. First acceptance gate is offline
coverage plus guarded rollback-only PostgreSQL verification; a later explicitly
authorized developer invocation may apply after target/privilege/provider checks.
Follow with read-only competition/season/team/match queries. Defer standings and
metadata synchronization to separate phases with their own data/authority decisions.

## Review verification and scope

This phase inspected source, schema configuration, existing tests and documentation.
The supplied baseline is 794 unit and 179 offline integration tests passed; these
counts were not rerun or independently re-established in this documentation review.
No live provider calls, credential reads, database connections/writes, migrations,
production code changes, synchronization writer, frontend changes, commits or pushes.
Only this document is changed. Documentation whitespace/link checks are recorded
in the final report.
