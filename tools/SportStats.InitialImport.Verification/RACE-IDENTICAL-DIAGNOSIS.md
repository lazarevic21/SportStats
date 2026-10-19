# Phase 2.9H migration-race-identical diagnosis

A deterministic verification cleanup defect explains the reported wrapper-free
`guard-or-assertion` failure. Npgsql 10.0.3 retains a transaction's Connection after
commit/rollback. The old finally block treated non-null Connection as proof the
transaction was active, then called RollbackAsync on the completed winner. Npgsql
rejects this with InvalidOperationException. The first successful commit race
therefore failed during cleanup. The winner-rollback scenario also attempted to
roll back completed transactions, including its committed second writer.

The correction records successful CommitAsync/RollbackAsync completion explicitly
and rolls back only incomplete transactions. Completion is recorded only after the
await succeeds. Cleanup still releases the blocker first, observes the pending
command before connection disposal, and rolls back the active losing transaction
with a separate bounded token. Cleanup errors remain fatal and identify the failed
rollback stage. Existing working-tree changes were preserved.

The original container was deleted and raw exceptions were intentionally suppressed.
Thus the exact historical losing INSERT result cannot be recovered: the cleanup
error could also have masked an earlier error. The code defect is established;
end-to-end PostgreSQL success and historical 23505 remain unverified.

Independent Database review confirmed the two physical sessions, uncommitted first
insertion, genuinely concurrent pending second insertion, and observer's
pg_blocking_pids proof are appropriate at default ReadCommitted isolation. Raw
Npgsql insertion errors do not use the EF execution strategy wrapper. The expected
error remains a direct 23505 naming the exact expected UNIQUE index. Both identical
and different target mappings conflict on the same three identity columns. No
migration, isolation, synchronization, index or expected SQLSTATE change is justified.

Fixed sub-stages now distinguish session setup, first insertion, provisional write,
blocking proof, first commit/rollback, second outcome/commit, persisted mapping,
provisional row and cleanup failures. The outcome validator distinguishes both
successes, both failures, wrong winner, exactly one success with unexpected exception,
SQLSTATE or constraint. In the ordered blocking protocol, the first insert must
succeed before the second starts; a first-insert failure does not fabricate a second
failure. The both-failed validator branch is tested offline. An early second result
is observed and classified, and even an expected rejection cannot pass without
observed blocking. A passing commit race records exactly-one-succeeded-23505.

A fresh observer still requires one row, the exact surviving mapping ID and target,
and the expected provisional-write outcome. Zero and multiple mapping rows have
separate fixed diagnostics; ID/target mismatches identify the persisted-mapping
stage and assertion line. No unexpected error or missing contention is accepted.
Diagnostics emit only fixed stage names, enum outcomes, assertion lines and existing
allowlisted SQLSTATE/shape classifications. They never print messages, details,
SQL, credentials, connection strings, database identities or constraint values.

Files changed for this correction:
- DisposableMigrationVerification.cs: completion tracking and safe stage/outcome diagnostics.
- tests/SportStats.UnitTests/DisposableRaceTests.cs: focused offline regression coverage.
- README.md and this diagnosis: current status and evidence.

Validation on 2026-10-19:
- Unit tests: 894 passed, zero failures/skips (six new cases).
- Offline integration tests: 179 passed, zero failures/skips; PostgreSQLLive excluded
  and live opt-in/connection environment variables cleared in that child process.
- Mocked Docker preflight: 41 passed; mocked wrapper: four passed.
- Solution and standalone verification runner builds: zero warnings/errors.
- git diff --check: passed.
- Independent Database review: cleanup defect confirmed; concurrency and strict
  uniqueness/state assertions preserved; no database blocker found.
- Independent Security review: fixed diagnostics preserve redaction and existing
  isolation/approval/apply gates; no security defect found.

Safe to rerun the disposable workflow after explicit authorization for that run.
No Docker/PostgreSQL verification was run for this correction; no existing database
was changed, no importer --apply or release gate was enabled, and nothing was
committed or pushed. A rerun is needed to certify the live final state.
