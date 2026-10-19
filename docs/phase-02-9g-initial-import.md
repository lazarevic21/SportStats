# Phase 2.9G: Developer-controlled PL create-only importer

Implementation follows the [readiness review](phase-02-9f-initial-import-readiness.md).
This is a one-shot developer tool, separate from API startup. It does not schedule
work, expose public write endpoints, calculate standings or update canonical metadata.

**Release status: apply remains disabled; not certified for live imports.**
The original 74 guarded PostgreSQL cases now pass in the securely configured test
environment. Additional transaction ownership and identity safeguard verification
is recorded below. Durable commit/replay has not been executed or approved.
Neither the CLI gate nor `EfInitialImportStore.ApplySafetyVerified` is enabled.

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

External identity indexes remain non-unique in the configured database and EF
model. A read-only, repeatable-read audit found **zero duplicate key groups in
all four mapping tables**. This describes the audited snapshot, not a reservation
or a database uniqueness guarantee.

The staged [uniqueness SQL](sql/phase-02-9g-external-identity-uniqueness.sql)
adds one unique index per table on `(ProviderKey, IdentityScope, ExternalId)`.
It locks all four tables before re-auditing, rejects duplicates (including identical
targets), and creates all indexes atomically. It never repairs mappings. Different
providers/scopes and external aliases for the same target remain allowed. Existing
foreign keys and lookup indexes remain intact. The script is separate from automatic
EF migrations; the original migration, snapshot, and legacy ambiguity fixtures retain
their behavior. It has **not** been applied to permanent tables. Deployment requires
a reviewed target-specific migration and independent catalog verification. Until
then uncoordinated writers can still introduce duplicate mappings.

Standings remain outside scope. Other leagues require validated identity scopes,
payload fixtures and removal of PL-specific checks throughout normalization/planning/
persistence, not a copied importer or only a different HTTP competition code.

## Verification

Final check results for this continuation (2026-10-19):

| Check | Result |
| --- | --- |
| Complete unit suite | 851 passed, 0 failed, 0 skipped. |
| Offline integration, `Category!=PostgreSQLLive` | 179 passed, 0 failed, 0 skipped. |
| Guarded PostgreSQL, `Category=PostgreSQLLive` | 82 passed, 0 failed, 0 skipped; original 74 plus 5 transaction-owner and 3 identity safeguard cases. |
| Solution build, `dotnet build SportStats.slnx --no-restore` | Passed, 0 warnings/errors. |
| Standalone disposable verification project build | Passed, 0 warnings/errors. |
| Unapproved durable runner invocation | Refused before database access. |
| Disposable workflow PowerShell syntax | Passed; workflow not executed. |
| `git diff --check` | Passed. |
| Durable commit / post-commit replay | Not executed; explicit user approval required. |

Existing rollback-only
fixtures still reject commits, use verified restricted `sportstats_test` /
`sportstats_app` sessions, and never migrate permanent tables. The pre-existing
scenario fix loading season competition/sport was preserved. No provider calls,
metadata updates, frontend changes, commits or pushes occurred.

The public `ApplyAsync` entry remains blocked by its release gate. Its transaction
lifecycle is shared with an internal verification entry. Guarded tests exercise
that lifecycle using the real EF/Npgsql transaction owner, rather than only a fake
atomic session. They cover late exception, cancellation, returned natural-key
conflict with zero created counts, rejection of caller-owned transactions, and
commit refusal with an indeterminate outcome. They assert
transaction disposal, empty tracking, and all-table rollback. The fixture's commit
interceptor is retained. Refusal occurs before a server COMMIT; this does not
certify durable completion or loss of a server commit response. Offline atomic
session tests continue to cover commit/rollback uncertainty.

The identity safeguard tests use temporary copies of all four actual tables,
including their original migration indexes. They execute the staged SQL against
those copies and roll back. Same-target duplicates and conflicting-target inserts
are rejected with PostgreSQL `23505`; aliases and different providers/scopes remain
valid. A legacy duplicate aborts the audit before creating indexes. This verifies
additive compatibility with the original schema without altering permanent tables.
The existing full-import concurrency case verifies cooperative writer serialization;
these new conflict cases do not certify cross-session unique-index contention.

## Disposable durable verification and release gates

A separately isolated workflow is implemented under
[`tools/SportStats.InitialImport.Verification`](../tools/SportStats.InitialImport.Verification/README.md).
Its README provides the exact approval-gated invocation and limits. It must not be invoked until the
user explicitly approves disposable database commits. It creates a fresh isolated
PostgreSQL server and does not accept the guarded suite, development, or production
connection as its target. Existing rollback-only fixture safeguards are untouched.

The workflow verifies real commit, fresh-session identical replay, stable canonical
IDs/metadata/memberships, conflicting replay rollback, and staged uniqueness
safeguards. Building the runner or passing rollback-only tests is not evidence that
these durable checks passed. Execution remains pending.

Remaining release blockers:

- Explicit approval and successful isolated durable commit/replay execution.
- Reviewed deployment of identity uniqueness safeguards and independent verification
  of all four installed unique indexes on the intended target.
- Review of the resulting evidence and a separate decision to enable both apply
  gates. No test opt-in or workflow result automatically enables apply.

A failed or lost commit response remains indeterminate: no success is claimed and
a fresh preview is required before retrying. No production or development database
was used for destructive testing. No permanent schema changes were executed.

### Disposable preflight corrections

Both disposable runner contexts now configure
`MigrationsHistoryTable("__EFMigrationsHistory", "platform")`, matching application
and design-time configuration. The wrapper rejects `DOCKER_HOST` / `DOCKER_CONTEXT`
overrides, accepts only `default` / `desktop-linux` contexts with the two explicitly
allowlisted local Windows Docker named pipes, verifies a Linux daemon, and pins
the inspected endpoint with `--host` for every container command. TCP, SSH, Unix
sockets and unknown contexts are rejected. See the workflow README for exact endpoints.

Cleanup verifies the full returned container ID and invocation marker before removal.
Errors are terminating and visible; no unrelated container deletion or volume pruning
is attempted. Comments now correctly describe PostgreSQL's anonymous data volume.
Trusted local Docker Desktop remains a prerequisite. Abrupt termination or ambiguous
creation output can leave disposable resources requiring manual inspection. Container
network egress is not blocked by these endpoint checks.

Correction verification: standalone and solution builds passed with zero warnings/errors;
851 unit and 179 offline integration tests passed, zero failures/skips. Docker preflight
and cleanup regression checks passed 22 cases using a mocked Docker function,
never a real daemon. `git diff --check` passed.
Security and Database specialists reviewed the corrections. Durable verification
remains unexecuted and requires explicit user approval; both apply gates remain disabled.

## Files changed in this continuation

- `docs/phase-02-9g-initial-import.md`: results, safeguards and release blockers.
- `docs/sql/phase-02-9g-external-identity-uniqueness.sql`: staged, audit-first uniqueness safeguards.
- `src/SportStats.Infrastructure/Persistence/InitialImport/EfInitialImportStore.cs`: internal verification entry sharing the gated public lifecycle.
- `src/SportStats.Infrastructure/Properties/AssemblyInfo.cs`: explicit verification executable friend assembly.
- `tests/SportStats.IntegrationTests/ExternalIdentityAuditPostgreSqlTests.cs`: read-only data audit and temporary-schema conflict tests.
- `tests/SportStats.IntegrationTests/InitialImportOwnerPostgreSqlTests.cs`: five actual transaction-owner scenarios.
- `tests/SportStats.IntegrationTests/SportStats.IntegrationTests.csproj`: embeds staged SQL for verification.
- `tools/SportStats.InitialImport.Verification/SportStats.InitialImport.Verification.csproj`: standalone verification executable, outside ordinary solution/test discovery.
- `tools/SportStats.InitialImport.Verification/Program.cs`: guarded disposable migration, uniqueness, durable commit and replay checks.
- `tools/SportStats.InitialImport.Verification/Verify-DisposableImport.ps1`: explicit-approval fresh-container lifecycle.
- `tools/SportStats.InitialImport.Verification/README.md`: invocation, isolation and evidence limits.

Pre-existing modified file preserved:
`tests/SportStats.IntegrationTests/InitialImportPostgreSqlScenarios.cs` loads the
season's competition/sport navigation in the ambiguous-mapping fixture. This
continuation did not change that edit.
