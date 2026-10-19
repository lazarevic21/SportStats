# Phase 2.9H migration lock-timeout correction

The reported `migration-lock-timeout (guard-or-assertion)` / wrapper
`standalone-runner` failure exposes an exception-shape bug in the verification
assertion. Npgsql marks `55P03` (`lock_not_available`) as transient. Its default
non-retrying EF execution strategy catches that error and throws
`InvalidOperationException` with the original `PostgresException` as its inner
exception. `ExpectMigrationFailureAsync` previously caught only a direct
`PostgresException`, so the expected lock timeout escaped to the runner's generic
guard/assertion classification. The wrapper correctly propagated the nonzero exit;
cleanup succeeded according to the reported run.

The new offline regression exercises the actual installed provider's
`CreateExecutionStrategy().ExecuteAsync` with synthetic `55P03`, confirms its exact
wrapper and disabled retries, and verifies corrected handling without connecting.
This establishes the code defect and reproduces the reported category. The original
exception/stack was suppressed and its container deleted, so historical exception
contents cannot independently be recovered. No live reproduction was attempted.

The writer lock is acquired and awaited inside an open blocker transaction before
migration starts. PostgreSQL's `ROW EXCLUSIVE` conflicts with the migration's
`SHARE ROW EXCLUSIVE`; the blocker is rolled back only after migration execution
completes or fails. There is no sleep-based acquisition race. The EF-owned migration
transaction, `SET LOCAL lock_timeout = '5s'`, statement timeout and runner deadlines
are unchanged. No migration SQL or runner connection configuration changed.

The correction accepts direct PostgreSQL errors or exactly one
`InvalidOperationException` wrapping a transient PostgreSQL error, and still requires
the exact expected SQLSTATE. `P0001` still requires the exact generic audit message.
Unexpected success, wrong SQLSTATE, cancellation and unrelated wrappers fail closed.
Duplicate detection, late index rollback, data/history/schema preservation,
upgrade/Down/Up, concurrent writers and exact catalog checks remain present.

Diagnostics now identify blocker acquisition, expected error, blocker release
failure, preservation checks and catalog checks separately. Assertions identify a
compiler-supplied source line in `DisposableMigrationVerification`; database failures
show only an allowlisted SQLSTATE and direct/wrapped shape. Unknown codes become
`other`. Exception messages, stacks, SQL, connection settings, credentials, data,
details and identifiers are never emitted.

Files changed for this correction: `DisposableMigrationVerification.cs`,
`Program.cs`, `README.md`, this report, and
`tests/SportStats.UnitTests/DisposableMigrationFailureTests.cs`. Other working-tree
changes predate this correction and were preserved.

Validation on 2026-10-19:

- `dotnet test tests/SportStats.UnitTests/SportStats.UnitTests.csproj --no-restore --verbosity minimal`: 888 passed, zero failures/skips.
- `dotnet test tests/SportStats.IntegrationTests/SportStats.IntegrationTests.csproj --no-restore --filter 'Category!=PostgreSQLLive' --verbosity minimal`: 179 passed, zero failures/skips. Live opt-in and test connection environment variables were cleared in that child shell.
- `Test-DisposableDockerPreflight.ps1`: 41 mocked offline checks passed.
- `Test-DisposableImportWrapper.ps1`: four mocked offline checks passed.
- `dotnet build SportStats.slnx --no-restore --verbosity minimal`: zero warnings/errors.
- `dotnet build tools/SportStats.InitialImport.Verification/SportStats.InitialImport.Verification.csproj --no-restore --verbosity minimal`: zero warnings/errors.
- `git diff --check`: passed (Git emitted only existing CRLF conversion advisories).
- Independent Database review: no blockers; 11 targeted tests independently passed.
- Independent Security review: no findings; verified allowlisted diagnostics and unchanged approval/isolation/apply gates.

Code and offline evidence support rerunning the disposable workflow after explicit
authorization. End-to-end PostgreSQL success remains unverified. No Docker or
PostgreSQL verification, existing database changes, importer `--apply`, release gate
enablement, commit or push occurred during this correction.

Provider sources: [default EF strategy (v10.0.3)](https://github.com/npgsql/efcore.pg/blob/v10.0.3/src/EFCore.PG/Storage/Internal/NpgsqlExecutionStrategy.cs),
[transient PostgreSQL classification](https://github.com/npgsql/npgsql/blob/v10.0.0/src/Npgsql/PostgresException.cs),
[PostgreSQL 17 lock conflicts and transaction lifetime](https://www.postgresql.org/docs/17/explicit-locking.html).
