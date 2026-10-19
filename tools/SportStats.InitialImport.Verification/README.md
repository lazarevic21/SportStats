Phase 2.9H race-identical correction: see
[root cause, strict race diagnostics and offline validation](RACE-IDENTICAL-DIAGNOSIS.md).
The latest reported run passed the lock-timeout scenario, then exposed a completed
transaction rollback in race cleanup. Completion tracking is corrected; 894 unit,
179 offline integration and 45 mocked harness checks pass. Live rerun remains pending.
# Disposable migration and durable importer verification

Phase 2.9H lock-timeout assertion correction: see
[root cause, safe diagnostics, tests and independent reviews](LOCK-TIMEOUT-DIAGNOSIS.md).
The reported disposable run failed because the expected transient PostgreSQL error
was wrapped by EF's provider strategy. Offline reproduction and correction checks
pass; the corrected disposable workflow has not been rerun.

This executable is intentionally outside the solution and ordinary test discovery.
It exercises the same internal lifecycle used by public apply after its release gate.
Public `--apply` remains disabled. Rollback-only fixtures and their commit refusal are unchanged.

**Do not run this workflow until the user explicitly approves disposable database commits.**
The script's mandatory approval argument records that approval; it does not obtain it.
After approval, from the repository root:

```powershell
./tools/SportStats.InitialImport.Verification/Verify-DisposableImport.ps1 -Approval 'I APPROVE DISPOSABLE IMPORT COMMITS'
```

Requirements: local Docker Desktop with Linux containers on Windows, PowerShell,
.NET 10 SDK, and access to
`postgres:17.6-bookworm` and NuGet. The script creates a new random container with no
host bind mounts or persistent named volumes and binds an ephemeral port on loopback.
The preflight rejects any `DOCKER_HOST` or `DOCKER_CONTEXT` override. The active
context must be exactly `default` or `desktop-linux`, and its inspected endpoint
must be `npipe:////./pipe/docker_engine` or
`npipe:////./pipe/dockerDesktopLinuxEngine`. TCP (including loopback), SSH, Unix
sockets, other context names and malformed inspection output are rejected.
It checks that the allowed local daemon reports Linux, then pins that endpoint
with `--host` for every container lifecycle command. Trusted local Docker Desktop
and its named pipes are prerequisites; this does not provide outbound network isolation.
It never accepts an existing database endpoint or uses the normal test connection setting.
A random PostgreSQL server marker, exact database/user checks, absence of existing
application schemas, and an independent approval token must all pass before migration
or writes. Generated credentials are passed through environment variables and are
never printed; provider diagnostics are suppressed. Database commands have bounded
timeouts, and verification has a three-minute cancellation deadline. Each migration
scenario connection independently rechecks the server marker, database and session
identity. All connections derive from the one fresh container; no other database is
created, selected or accepted.

The runner uses `platform.__EFMigrationsHistory`, matching application and design-time
configuration. The actual EF migrator is pinned first to
`20261012165516_InitialCreate`, then to
`20261019102703_ExternalIdentityUniqueness`; historical SQL is not applied. Before
the durable importer checks, it verifies:

1. Baseline history and absence of all four new indexes; valid canonical fixtures
   and one existing mapping per table are seeded using only the fresh server.
2. Eight independent duplicate failures: each of the four tables with same-target
   duplicates and with different-target duplicates. Each must raise the migration's
   exact generic audit error (`P0001`). Fresh sessions must see identical full-column
   snapshots of all application tables and history, unchanged baseline schema,
   and no new unique indexes. Only after those assertions are the fixture duplicate
   IDs removed; the migration itself never repairs duplicates.
3. A conflicting index name at the last creation step causes `42P07`, proving
   earlier index creation rolls back. A held writer lock causes the migration's
   five-second lock timeout (`55P03`). Data/history and baseline schema must survive.
4. Successful upgrade, downgrade to InitialCreate and upgrade again preserve all
   fixture rows, foreign keys, columns and legacy indexes; exact history is checked
   after every transition. Downgrade removes only the four new unique indexes.
5. Each unique index is bound to its exact schema/table/name, unique, valid and ready,
   with exactly the three ordered key columns, no included columns, no predicate and
   no expression. Catalog checks and legacy-schema snapshots replace the former
   weaker global index-count check.
6. Distinct IDs, aliases for one canonical target, different providers and different
   scopes insert successfully in all four tables.
7. Twelve two-session races: per table, an identical-target and conflicting-target
   insertion wait behind an uncommitted winning insertion. `pg_blocking_pids`
   proves actual contention before commit; the loser must receive `23505` naming
   the expected index. The whole losing transaction, including an unrelated
   provisional team row, rolls back. A third race per table rolls back the first
   writer and confirms the blocked second writer can then commit. Fresh sessions
   verify the exact surviving mapping and provisional-row outcome. Each race has
   a 15-second cancellation bound, a 12-second server statement timeout, and task
   observation/rollback cleanup independent of the cancelled operation token.

The runner then creates a restricted application role with SELECT and INSERT only
and verifies:

1. A lifecycle-owned transaction commits a complete import and clears its context.
2. A fresh context/physical connection replays identical input with zero creates.
3. Another fresh context rejects changed metadata, rolls back, and clears tracking.
4. Full-column snapshots of all nine import tables, including mappings and season
   membership, are identical after both replays, preserving IDs and metadata.

The script removes only the full container ID returned by its creation command,
after checking the invocation's marker label, in `finally`; `--rm` also removes
anonymous volumes. Cleanup failures terminate with a visible error and require
manual inspection. Abrupt termination or an ambiguous creation failure can leave
disposable resources behind; cleanup never falls back to deleting unrelated containers
or pruning volumes. Docker images, caches and build artifacts remain.
No production or development database is touched.

The new races certify mapping-level database transactions, not a collision during
the importer's multi-stage lifecycle. The existing importer checks still cover
commit, fresh-session identical replay and conflicting metadata preservation.
Concurrent importer collision/whole-import rollback, target-specific deployment
and independent target catalog certification remain release blockers. A passing
disposable run does not authorize any existing-database changes or enable apply.

Build-only verification (does not start Docker or write to a database):

```powershell
dotnet build tools/SportStats.InitialImport.Verification/SportStats.InitialImport.Verification.csproj
```

Offline Docker preflight/cleanup checks (Docker is mocked; no container or database
is created):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/SportStats.InitialImport.Verification/Test-DisposableDockerPreflight.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/SportStats.InitialImport.Verification/Test-DisposableImportWrapper.ps1
```

See [failure diagnosis](FAILURE-DIAGNOSIS.md) for the reported failure's execution
path, diagnostic changes, validation and read-only resource inspection commands.
Readiness now checks the container TCP listener explicitly. Runner failures report
fixed stage names and error categories, and wrapper/cleanup outcomes are separate.

Correction checks: 22 offline preflight/cleanup cases, 851 unit tests and 179 offline
integration tests passed with zero failures/skips. Solution and standalone runner
builds passed with zero warnings/errors. Security and Database reviews found no
remaining blockers in these corrections.

Status: the user reports the Phase 2.9G disposable durable workflow passed, including
cleanup. The extended Phase 2.9H workflow is prepared for approval and has not been
executed. A passing run certifies the disposable migration scenarios above, EF
unique indexes, successful commit and replay;
it does not certify an existing target's data audit/index deployment, production
permissions, network-loss commit uncertainty, or enable any release gate.
