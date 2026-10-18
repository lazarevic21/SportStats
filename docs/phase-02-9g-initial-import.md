# Phase 2.9G: Developer-controlled PL create-only importer

Implementation follows the [readiness review](phase-02-9f-initial-import-readiness.md).
This is a one-shot developer tool, separate from API startup. It does not schedule
work, expose public write endpoints, calculate standings or update canonical metadata.

**Release status: preview implemented; apply implemented behind disabled release
gates. This phase is not certified for live imports.** Neither guarded-test opt-in
nor its connection string was available in the verification process. Do not remove
the CLI gate or `EfInitialImportStore.ApplySafetyVerified` on the strength of offline
tests. Database identity mapping uniqueness also remains unresolved below.

## Developer entry point

After securely populating process environment configuration, preview with:

```powershell
dotnet run --project tools/SportStats.InitialImport -- --live --competition PL --year 2026
```

The year is an example; explicitly choose the expected season start year. This makes
live provider requests only after verifying the test database target. It prints JSON
with per-type `Created` (planned in preview), `Existing`, `Rejected` and `Conflicted`
counts. Memberships and mappings are counted separately. Malformed provider snapshots
produce a sanitized rejected-snapshot count; identity planning stops at its first
conflict rather than claiming to enumerate all possible conflicts. Exit zero means
successful preview, not a committed import.

`--apply` currently exits before resolving services or making requests. Once all
release gates have been satisfied in a future reviewed change, it will require a new
preview and the interactive text `APPLY PL CREATE ONLY`; redirected input is rejected.
No earlier preview, command argument alone or timeout grants write permission.

## Safety and scope

Only Premier League is enabled. One explicit start year selects the expected returned
season, not a historical provider query: existing HTTP methods do not send a season
parameter. Competition, teams and matches must agree on competition and season.
Fetches are sequential, capped at three attempts, paced and cancellation-aware, with
no retries. The fixed-origin client retains body/timeout limits and credential-safe
error handling. Local pacing does not reserve an account-wide provider quota.

Credentials come from the process environment. Never paste them into commands, chat,
tracked files or test fixtures. Provider access uses `FootballData__ApiToken`;
database access uses `ConnectionStrings__SportStats`. Guarded live tests instead use
`SPORTSTATS_TEST_CONNECTION_STRING` and `SPORTSTATS_RUN_POSTGRES_TESTS=1`.
Do not use production configuration or migrator credentials for development.
Both preview and dormant apply are restricted to `sportstats_test` with current and
session user `sportstats_app`, rejecting elevated role attributes/memberships.
The CLI disables pooling, ambient enlistment and sensitive database diagnostics;
the store checks connection settings even for an already open connection. No grants,
schema changes, database creation or migration occur.

Dry-run planning uses existing identity resolution without allocating canonical IDs,
saving changes, acquiring advisory locks or simulating inserts and rolling them back.
It reports plans against a read-only database snapshot; it does not reserve identities
or authorize any future apply. Missing required references, duplicate/ambiguous or
dangling mappings, conflicting natural-key owners and metadata drift are conflicts.
Absence of an identity for a genuinely new entity is a planned creation, not a guessed
link to an existing row with the same name.
Standalone preview owns a `RepeatableRead` transaction with `SET TRANSACTION READ
ONLY`. The internal fixture can lend its existing transaction; that branch still
performs only reads but does not promise a consistent snapshot at `ReadCommitted`.

Apply requires both explicit selection and a separate confirmation. It must re-read
and revalidate under the existing cooperative football locks in one fresh transaction.
Creation order is competition, season, teams/memberships, matches. All successful
stage results remain provisional until transaction completion; a failed later stage
requires rollback of the entire import, not merely that stage's savepoint.

Existing canonical IDs, names, season dates and match kickoff/status/matchday/goals
remain unchanged. Differences are reported, never repaired. The synchronization
policy remains read-only: `Approved` is unreachable and authorization remains false.
Zero and null scores remain distinct; unsupported statuses and incomplete score pairs
are rejected before persistence. Creating a scheduled fixture does not authorize a
later result update, so create-only data can become stale.
The initial allowlist is exact `TIMED` with both scores explicitly null, or `FINISHED`
with both nonnegative score values; `score.duration` must be `REGULAR`. Matchday,
UTC kickoff and season dates must be valid. Other statuses fail closed, including
otherwise domain-representable live/postponed/cancelled matches. Such payloads will
block an import rather than be silently skipped. Inputs are bounded to 2–100 teams
and 1–5000 matches; these are resource bounds, not a completeness guarantee.

## Concurrency and database limitations

All cooperating import/persistence writers must acquire the existing football
coordination lock before reading/planning and hold it through outer completion.
Natural-key uniqueness protects Sport code, competition sport/code, season parent/key
and season membership; foreign keys enforce relationships. PostgreSQL remains the
final safeguard for those constraints.

External identity indexes are non-unique in the existing schema. They do **not**
prevent duplicate team or match mappings inserted by uncoordinated writers. Planner
checks reject duplicate/ambiguous mappings and the cooperative lock serializes known
writers. Stronger external-identity database uniqueness requires a separately reviewed
data audit/migration; this phase must not claim that guarantee or silently repair data.

Standings remain outside scope. Other leagues require validated identity scopes,
payload fixtures and removal of PL-specific checks throughout normalization/planning/
persistence, not a copied importer or only a different HTTP competition code.

## Verification

CLI apply must stay disabled unless
transaction atomicity and database safety have been verified, including guarded live
PostgreSQL rollback scenarios. Existing fixtures refuse commits, verify the actual
`sportstats_test`/restricted `sportstats_app` session, and do not create/migrate databases
or alter permanent rows. Rollback-only replay does not prove durable post-commit replay.

| Check | Result |
| --- | --- |
| Solution build, `dotnet build SportStats.slnx --no-restore` | Passed, zero warnings/errors. |
| Complete unit suite | 851 passed, zero failed/skipped; 57 new importer cases over the 794 baseline. |
| Offline integration, `Category!=PostgreSQLLive` | 179 passed, zero failed/skipped. |
| Guarded PostgreSQL selection, `Category=PostgreSQLLive` | 0 passed, 0 failed, 74 skipped because opt-in was unavailable; includes 10 new importer cases. No database operations executed. |
| CLI `--apply` smoke check | Refused before requests/database access. |
| `git diff --check` and new-file whitespace/local-link checks | Passed. |

The new guarded scenarios exercise whole-graph preview/creation/replay, metadata
conflict preservation, duplicate/ambiguous/unmapped identities, a PostgreSQL unique
competition violation and dangling-reference FK rejection, final-match failure and
cancellation, and concurrent importer serialization observed through PostgreSQL
blocking information. Every fixture rolls back; before/after IDs and memberships
cover all import tables. These scenarios compile and are discovered but remain
**unexecuted**, so none establishes a live guarantee yet.
The public apply wrapper delegates its lifecycle to `InitialImportAtomicOperation`;
eight offline session tests cover commit ordering, conflict rollback/count reset,
exceptions, cancellation with independent bounded cleanup, begin failure and
indeterminate commit/rollback outcomes. These verify the actual wrapper decision
logic, while PostgreSQL transaction ownership and durable completion remain live gates.

Independent review dispositions:

| Review | Finding and disposition |
| --- | --- |
| QA | Offline checks pass; added full-import tests and corrected late-failure injection to reach the final match mapping. Reviewed root credential/configuration and direct-store boundary tests. Keep apply disabled. |
| Security | No metadata mutator or credential disclosure found. Requested open-session setting validation and preserving commit uncertainty when rollback fails; both fixed. Nonunique mappings and missing public transaction-owner verification remain release blockers. |
| Database | Confirmed shared outer transaction and lock retention. Requested all-table rollback assertions and standalone preview coverage; added as guarded cases. Commit-outcome masking fixed and actual wrapper lifecycle extracted for offline testing. PostgreSQL ownership/commit path remains uncertified. |

Before enabling apply: provide secure guarded test configuration, execute and inspect
all PostgreSQL cases, add verification of the actual public transaction-owner failure/
rollback lifecycle without weakening fixture commit refusal, and resolve the requested
external-identity uniqueness safeguard through a separately reviewed schema/data
decision. Durable commit/replay certification needs a separately approved disposable
database workflow; do not turn off the existing fixture guard. A failed or lost commit
response is indeterminate even if later cleanup fails: the tool claims no success and
requires a fresh preview before retry. No real provider requests or database
connections/writes were made during this implementation.

## Changed files

- `SportStats.slnx`: adds the developer tool project.
- `src/SportStats.Application/Football/InitialImport/InitialImportContracts.cs`
- `src/SportStats.Application/Football/InitialImport/InitialImportFetcher.cs`
- `src/SportStats.Application/Football/InitialImport/InitialImportValidator.cs`
- `src/SportStats.Infrastructure/Persistence/InitialImport/EfInitialImportStore.cs`
- `src/SportStats.Infrastructure/Persistence/InitialImport/InitialImportAtomicOperation.cs`
- `tools/SportStats.InitialImport/SportStats.InitialImport.csproj`
- `tools/SportStats.InitialImport/Program.cs`
- `tools/SportStats.InitialImport/ImportOptions.cs`
- `tools/SportStats.InitialImport/ImportDatabaseConfiguration.cs`
- `tests/SportStats.UnitTests/SportStats.UnitTests.csproj`: references the tool.
- `tests/SportStats.UnitTests/InitialImportConfigurationTests.cs`
- `tests/SportStats.UnitTests/InitialImportStoreBoundaryTests.cs`
- `tests/SportStats.UnitTests/InitialImportAtomicOperationTests.cs`
- `tests/SportStats.UnitTests/InitialImportFetcherTests.cs`
- `tests/SportStats.UnitTests/InitialImportOptionsTests.cs`
- `tests/SportStats.UnitTests/InitialImportValidationTests.cs`
- `tests/SportStats.IntegrationTests/InitialImportPostgreSqlScenarios.cs`
- `tests/SportStats.IntegrationTests/InitialImportPostgreSqlTests.cs`
- `docs/phase-02-9g-initial-import.md`: this document.

The pre-existing untracked `docs/phase-02-9f-initial-import-readiness.md` was read and
left unchanged. No existing schema, provider normalizer, synchronization policy,
frontend or API startup behavior changed. No commits or pushes.
