# Phase 2.9H: External identity uniqueness release preparation

Status: prepared, not deployed. No database was accessed or changed in this phase.
Public importer `--apply` and `EfInitialImportStore.ApplySafetyVerified` remain disabled.
The user reports that the preceding 2.9G disposable durable commit, fresh-session
replay, conflict preservation and automatic cleanup passed. That evidence applies
to the preceding implementation, not execution of this new migration.

## Proposed schema change

EF migration `20261019102703_ExternalIdentityUniqueness` adds these unique indexes:

| Table | Unique index |
| --- | --- |
| `platform.competition_external_ids` | `UX_competition_external_ids_identity` |
| `platform.season_external_ids` | `UX_season_external_ids_identity` |
| `football.team_external_ids` | `UX_team_external_ids_identity` |
| `football.match_external_ids` | `UX_match_external_ids_identity` |

Each covers exactly `(ProviderKey, IdentityScope, ExternalId)`, using the existing
PostgreSQL column types and collations. All three columns are already `NOT NULL`.
Same-key duplicates are prohibited even when they reference the same canonical ID.
Different providers, scopes and external aliases remain possible. No normalization,
case folding, canonical-target uniqueness or changes to foreign keys are introduced.

The original `20261012165516_InitialCreate` and its designer are unchanged. Existing
nonunique `IX_*` indexes remain; the model uses separately named indexes on the same
properties. The generated designer and snapshot include both. Migration history
remains `platform.__EFMigrationsHistory`. No application startup migration is added.

The migration sets transaction-local lock/statement timeouts to 5s/60s, locks all four
tables in `SHARE ROW EXCLUSIVE` mode, audits all four identity triples, and raises
an exception if any duplicate exists. It then creates all four indexes in the same
EF transaction. Inserts, updates and deletes cannot race between audit and index
creation. Locks can delay writers and conflict with maintenance; schedule downtime.
Timeouts, duplicate audit failure, index name collisions or index creation failure
abort deployment without repairing data. A normal transactional migration rolls
back all four indexes and its history entry together. Never suppress transactions
or execute extracted statements individually. The statement timeout is per server
statement, not an overall deployment deadline; client command timeouts also apply.

`docs/sql/phase-02-9g-external-identity-uniqueness.sql` is retained solely as a
historical verification fixture. Its test-database guard is deliberate. It is
superseded for deployment and must not run after this migration. If its indexes
were installed manually on a target, this migration fails closed on name collisions:
stop and review schema/history reconciliation separately. Do not mark the migration
applied manually or add `IF NOT EXISTS` to conceal drift.

## Importer concurrency and conflicts

Cooperating writers acquire transaction-scoped football coordination before
discovery/revalidation; the importer owns one outer `ReadCommitted` transaction.
Metadata/relationship conflicts return failure and roll back all stages. Database
unique violations from noncooperating inserts propagate through the same outer
rollback and clear tracking; they are not converted into success, updates or
upserts. Identical replay is successful only after fresh discovery finds the same
stored identities and metadata. A collision requires a fresh preview before a
separately authorized retry. There is no automatic retry. A failed/lost commit
response remains indeterminate and requires independent inspection.

Identity uniqueness does not prevent unrelated writers changing canonical metadata.
Quiesce such writers for the initial import. Do not enable apply based on index
presence alone. Existing legacy ambiguity fixtures intentionally use the original
schema; they cannot all run unchanged against a migrated target.

## Deployment procedure (requires separate approval)

1. Approve isolated PostgreSQL verification of this exact migration first: clean
   upgrade, same-target and conflicting-target legacy duplicates, failure rollback
   with unchanged rows and absent migration history, lock timeout, and concurrent
   two-session duplicate inserts/import collisions with whole-import rollback.
   Re-run durable import/replay using the updated disposable runner. The extended
   runner prepares the migration failure/history and mapping-level concurrency
   checks described below; actual importer collision certification remains separate.
   Existing temporary-table tests exercise real
   migration SQL but cannot certify migration history or cross-session contention.
2. Select and explicitly approve the exact deployment environment/database and
   maintenance window. Verify server/database/session role independently; use
   deployment credentials separate from the restricted importer. Review backup
   and restore readiness, table size, available disk and timeout suitability.
   Stop competing writers and migration jobs. Application roles need no DDL grants.
3. In an explicitly read-only session, inspect the target's migration history,
   expected tables/columns/types/collations/NOT NULL/FKs and existing indexes. The
   expected baseline is InitialCreate with none of the four `UX_*` objects. Audit
   each triple with `GROUP BY "ProviderKey", "IdentityScope", "ExternalId"
   HAVING count(*) > 1`; record counts without exporting identity values.
   Any duplicates or drift block deployment. Resolution needs its own reviewed,
   explicitly approved plan; this migration never resolves them.
4. Generate and review the bounded forward SQL offline (no connection required):

   ```powershell
   dotnet ef migrations script 20261012165516_InitialCreate 20261019102703_ExternalIdentityUniqueness --project src/SportStats.Infrastructure --startup-project src/SportStats.Infrastructure --output phase-02-9h-reviewed.sql
   ```

   Confirm one transaction, the audit before all four unique indexes, and the
   `platform.__EFMigrationsHistory` insert before commit. Do not use
   `--no-transactions`. After deployment approval, an operator may execute this
   reviewed script against the verified target with stop-on-error behavior
   (for psql, `ON_ERROR_STOP=1`) and protected connection configuration. Never
   paste credentials into commands or logs. The baseline script is not idempotent;
   inspect actual schema/history after any uncertain result before retrying.
5. Independently inspect catalogs after commit: each index must belong to its
   exact schema/table, be unique, valid and ready, use exactly the three ordered
   key columns, and have no predicate or expression. Verify all four history/index
   outcomes together, unchanged foreign keys and retained legacy indexes, and
   re-audit duplicate counts. Do not test inserts in an existing database. Record
   sanitized deployment evidence before allowing writers to resume.
6. Review all evidence and obtain a separate release decision for enabling both
   importer gates. Deployment does not authorize an import or enable either gate.

## Failure and rollback limits

Duplicate audit failure aborts with a generic message; no identity values or mapping
repairs are emitted. Fixing data is outside this release preparation. Index name
collisions fail rather than accepting an unverified object. On timeout or connection
loss, inspect target history/catalogs in a new session; do not assume success or
blindly rerun. Large targets may require a separately designed deployment strategy;
this bounded blocking migration is not a zero-downtime concurrent-index rollout.

`Down` drops only the four added unique indexes and preserves rows and legacy
indexes. It removes the safeguard and cannot undo committed imports. Any downgrade
requires stopping importer/writers, explicit approval and reviewed transactional
reverse SQL; never downgrade through InitialCreate. Old application code can read
the new schema, so retaining the safeguards is preferable to automatic rollback.
Duplicates introduced after downgrade will block a subsequent upgrade.

## Verification and independent reviews

Five new offline unit regressions check model/index/nullability invariants,
unchanged baseline, audit/lock ordering, absence of data rewrites, Down operations,
and transactional generated SQL/history ordering. The existing model test now
expects uniqueness while retaining the legacy lookup index.

Two new guarded PostgreSQL tests run actual migration operation SQL against TEMP
copies of the original tables inside the existing rollback-only fixture. Successful
installation must reject identical/conflicting keys with `23505` and allow aliases,
providers and scopes; duplicate audits must preserve all duplicate rows and create
no new unique indexes. These tests were added and compiled, not executed. They
require the legacy baseline schema, like the existing staged-SQL fixtures. No live
test, Docker workflow, provider call or durable database write ran in this phase.

Independent Database and Security specialists reviewed schema/history compatibility,
duplicate rejection and importer conflict handling. Both require actual migration
failure/history and concurrent collision certification plus target-specific index
verification before apply can be enabled. Their final review and local check results
are recorded below.

Remaining blockers: approved disposable migration failure/concurrency certification;
approved target deployment and catalog verification; separate apply enablement review
and explicit approval for the initial import. No commits or pushes were made.

Final local results (2026-10-19):

| Check | Result |
| --- | --- |
| Complete unit suite, `dotnet test tests/SportStats.UnitTests --no-restore` | 856 passed, zero failed/skipped |
| Offline integration, `dotnet test tests/SportStats.IntegrationTests --no-restore --filter 'Category!=PostgreSQLLive'` | 179 passed, zero failed/skipped |
| `dotnet build SportStats.slnx --no-restore` | Passed, zero warnings/errors |
| Standalone disposable verification project build, `--no-restore` | Passed, zero warnings/errors |
| Bounded EF migration SQL generation | Passed offline; one transaction verified |
| `git diff --check` | Passed |
| Live PostgreSQL / disposable runner execution | Not run |
| Independent final Database review | Passed for preparation; no blocker in changes |
| Independent final Security review | Passed for preparation; no blocker in changes |

In the original preparation review, both reviewers noted the runner's index count query was weaker than the
deployment catalog certification above: it does not prove exact table/schema
ownership, readiness, or absence of expressions/predicates. Do not reuse it as
target certification. The extended workflow below replaces that query with exact
catalog checks; it still cannot certify a different deployment target.

## Disposable migration workflow prepared for approval

The workflow remains unexecuted. No Docker daemon, provider or database was contacted
while preparing this extension. The original migration and its Down remain unchanged.
The existing wrapper's local endpoint allowlist, unique container name, random
credentials, loopback ephemeral port, server marker, full-ID ownership validation,
environment restoration and cleanup behavior are unchanged.

The runner now pins the real EF migrator to InitialCreate, seeds complete canonical
FK dependencies and one distinct mapping per table, and exercises:

- Eight duplicate audits: each table independently with identical-target and
  conflicting-target mappings; exact `P0001` audit failure, unchanged full data and
  migration-history snapshots from fresh sessions, no added unique indexes.
- Late index-name collision (`42P07`) after earlier index creation, and five-second
  writer lock timeout (`55P03`); unchanged data/history and no partial safeguards.
- Successful upgrade and Down/Up round trip, preserving existing mappings,
  canonical data, legacy indexes, columns and FK/constraint definitions.
- Twelve real two-session mapping races: identical and conflicting targets wait
  behind a writer that commits, then reject with the exact unique index's `23505`;
  a rollback variant lets the waiting writer commit. Blocking is observed through
  `pg_blocking_pids`, not inferred from a sleep. The losing transaction's unrelated
  provisional row must also disappear. Pending tasks are observed and transactions
  cleaned up with bounded independent tokens.
- Distinct external IDs/aliases, providers and scopes remain insertable in every
  table. Exact index schema/table/name, uniqueness/validity/readiness, three ordered
  columns, no included columns, predicates or expressions are checked with catalogs.
- Existing durable import, fresh-session identical replay and metadata-conflict
  preservation checks remain in the same newly created database.

The configuration guard is covered offline and is checked before opening a connection.
Every migration scenario connection independently verifies marker, database, current
user and session user. No fallback to any application/test connection is present.
The initial guard now requires both application schemas to be absent. Overall runner
deadline is three minutes; each race is bounded to 15 seconds with a 12-second
server statement timeout. Errors remain fixed stage/category messages.

Independent Database and Security reviews passed for this prepared extension. The
Database/QA review identified missing seed membership tracking; it was corrected
and an offline EF tracking regression now verifies all match membership dependencies.

Local verification of the extension (2026-10-19):

| Check | Result |
| --- | --- |
| Complete unit suite | 882 passed, zero failed/skipped; 26 new workflow cases |
| Offline integration, `Category!=PostgreSQLLive` | 179 passed, zero failed/skipped |
| Mocked Docker preflight/ownership checks | 41 passed; no Docker access |
| Mocked wrapper lifecycle scenarios | 4 passed, with new isolation assertions |
| Solution and standalone runner builds, `--no-restore` | Passed, zero warnings/errors |
| `git diff --check` | Passed |
| New disposable migration database scenarios | Prepared, not executed |

Exact command requiring separate explicit user approval, from repository root:

```powershell
./tools/SportStats.InitialImport.Verification/Verify-DisposableImport.ps1 -Approval 'I APPROVE DISPOSABLE IMPORT COMMITS'
```

That approval would cover creating this invocation's fresh local PostgreSQL
container/database, fixture commits/deletes, migrations and Down/Up, concurrent
mapping transactions, durable import/replays, and ownership-checked cleanup. The
mandatory argument records approval; it does not obtain it. Do not execute until
approval is given. Do not invoke the executable directly against any existing server.

Remaining blockers: successful approved execution and review of its sanitized
evidence/cleanup; actual importer collision during multi-stage persistence with
whole-import rollback (the new races prove mapping transaction behavior only);
target-specific deployment approval plus schema/data audit and catalog certification;
separate apply enablement/release decision and initial import authorization. No
workflow result enables either importer gate. Lost commit responses and production
scale/timeout behavior remain uncertified. No commits or pushes were made.

Files changed for this extension (earlier preparation changes remain intact):

- `tools/SportStats.InitialImport.Verification/DisposableMigrationVerification.cs`:
  real migrator scenarios, fresh snapshots, bounded races and exact catalogs.
- `tools/SportStats.InitialImport.Verification/DisposableVerificationContract.cs`:
  configuration, catalog and history invariants shared with offline regressions.
- `tools/SportStats.InitialImport.Verification/Program.cs`: runs guarded migration
  verification before the existing restricted-role importer checks.
- `tools/SportStats.InitialImport.Verification/SportStats.InitialImport.Verification.csproj`:
  removes obsolete historical SQL copy.
- `tools/SportStats.InitialImport.Verification/Test-DisposableImportWrapper.ps1`:
  mocked unique-name/credential/marker, listener/image/mount and forwarding checks.
- `tests/SportStats.UnitTests/DisposableVerificationContractTests.cs` and its project:
  guard/catalog/history and offline seed dependency tests, linked helper sources;
  the verification executable remains outside ordinary test discovery.
- `tools/SportStats.InitialImport.Verification/README.md` and this document:
  scenario coverage, approval command, evidence limits and release blockers.

## Changed files

- `src/SportStats.Infrastructure/Persistence/EntityConfigurations.cs`: retains legacy
  lookup indexes and adds separately named unique model indexes.
- `src/SportStats.Infrastructure/Persistence/Migrations/20261019102703_ExternalIdentityUniqueness.cs`:
  transactional audit/lock/index migration and narrowly scoped downgrade.
- Its generated `.Designer.cs` and `SportStatsDbContextModelSnapshot.cs`: updated model.
- `src/SportStats.Infrastructure/Persistence/InitialImport/EfInitialImportStore.cs`:
  updated concurrency comment only; release gate unchanged.
- `tests/SportStats.UnitTests/ExternalIdentityUniquenessMigrationTests.cs`: five new regressions.
- `tests/SportStats.UnitTests/DatabaseModelTests.cs`: updated uniqueness expectation.
- `tests/SportStats.IntegrationTests/ExternalIdentityAuditPostgreSqlTests.cs`: two
  guarded migration-SQL TEMP tests; stronger duplicate preservation assertions.
- `tools/SportStats.InitialImport.Verification/Program.cs`: relies on EF uniqueness,
  removes duplicate application of historical SQL.
- `tools/SportStats.InitialImport.Verification/README.md`: evidence and runner status.
- `docs/sql/phase-02-9g-external-identity-uniqueness.sql`: historical-only notice.
- `docs/phase-02-9g-initial-import.md`: points to current release status.
- This release procedure and evidence report.
