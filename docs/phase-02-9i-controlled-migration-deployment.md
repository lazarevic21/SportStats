# Phase 2.9I: controlled PostgreSQL migration deployment preparation

Status: preparation only. No database connection, database write, migration application,
import, commit or push is authorized by this document. Public importer `--apply` and
`EfInitialImportStore.ApplySafetyVerified` remain disabled. The user reports successful
Phase 2.9H disposable PostgreSQL verification, including cleanup; that evidence does
not establish the state or identity of this existing target.

Privacy note: `<VERIFIED_CLUSTER_SYSTEM_IDENTIFIER>` replaces a private target fingerprint in this historical record. It is documentation only, not a usable approval value. The diagnostic wrapper is intentionally disabled; do not bypass or retarget it. Phase 2.9I remains UNRESOLVED; migration/deployment remain NO-GO.

Current wrapper status: `tools/SportStats.MigrationPreflight/Invoke-ReadOnlyDiagnostic.ps1` intentionally refuses with a fixed message and nonzero exit code, even when its approval switch is supplied. It stops before location/environment changes, builds, credential prompts or process launches. Historical invocation examples below remain non-operational. Reconfiguration of private target selection requires separate review and authorization; no live use is authorized.

## Target and approval boundary

The only supported target is an independently confirmed **nonproduction** database
`sportstats_test` on the explicitly selected IPv4 loopback endpoint, using
`sportstats_migrator`. There is no application/test configuration fallback. Do not
infer that the documented development endpoint is this target. Before any connection,
the operator must independently establish its port and PostgreSQL system identifier
from trusted local instance inventory and confirm it is not production. A forwarded
production server is forbidden even if reachable on loopback. A system identifier
pins a cluster, not an environment classification; cloned clusters can share it.

The current session has no independently verified target identity. Live preflight
is therefore BLOCKED. No credentials were inspected. Supply only nonsecret target
identifiers for review; never send passwords or secret connection strings to chat.
Missing permissions to inspect `pg_control_system()` block certification; this
workflow never grants permissions or changes roles to work around that failure.

The command-line approval flags record approval; they do not obtain user approval.
Separate decisions are required for (1) identifying and auditing this target,
(2) applying the exact reviewed bundle/hash during a maintenance window, and
(3) any future importer enablement or import. Approval of preparation covers none
of the latter decisions.

## Migration chain and configuration

Exactly two migrations exist:

1. `20261012165516_InitialCreate` (expected baseline).
2. `20261019102703_ExternalIdentityUniqueness` (the sole bounded forward change).

History is `platform.__EFMigrationsHistory`, EF product version `10.0.12`.
The design-time factory configures no connection and reads no credentials.
Runtime configuration and the disposable runner must not be reused for deployment.
The historical initial application targets `sportstats_dev`; its scripts are
inappropriate for this populated database. Do not run the historical 2.9G SQL.

The four new indexes are `UX_competition_external_ids_identity` and
`UX_season_external_ids_identity` in `platform`, and
`UX_team_external_ids_identity` and `UX_match_external_ids_identity` in `football`.
Each belongs to its correspondingly named mapping table and covers exactly
`(ProviderKey, IdentityScope, ExternalId)`. Existing lookup indexes and foreign keys
must remain intact. No deduplication, normalization, grants or data repair occurs.

## Offline preparation commands

From repository root, generate only the bounded forward SQL; generation does not
connect to PostgreSQL:

```powershell
dotnet tool run dotnet-ef -- migrations script 20261012165516_InitialCreate 20261019102703_ExternalIdentityUniqueness --project src/SportStats.Infrastructure --startup-project src/SportStats.Infrastructure --output .local/Phase29I.forward.sql
if ($LASTEXITCODE -ne 0) { throw 'SQL generation failed' }
Get-FileHash -LiteralPath .local/Phase29I.forward.sql -Algorithm SHA256
```

Review the full SQL and hash. Require one transaction, the lock and duplicate audit
before all four indexes, and the sole forward history insertion before commit.
Never use `--no-transactions`, direct `database update`, an unbounded migration
range, or execute individual extracted statements. Deployment bundle generation
and read-only preflight commands are specified below after their offline validation.

## Required evidence before deployment approval

After identity is confirmed and the read-only audit is approved, securely inject
`SPORTSTATS_MIGRATION_PREFLIGHT_CONNECTION_STRING` into the operator's process.
Do not put its value in arguments, shell history, transcripts, source files or
reports. Only Host, Port, Database, Username, Password and SSL Mode are accepted;
the explicit target flags must match. No user secrets, application configuration,
service files, ambient provider settings or test connection is consulted.

```powershell
# Set these NONSECRET values from independently verified inventory first.
$targetPort = [int](Read-Host 'Verified nonproduction PostgreSQL port')
$clusterIdentifier = Read-Host 'Verified PostgreSQL system identifier'
dotnet build tools/SportStats.MigrationPreflight --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Preflight build failed' }
dotnet run --project tools/SportStats.MigrationPreflight --no-build -- --host 127.0.0.1 --port $targetPort --database sportstats_test --role sportstats_migrator --expect baseline --system-identifier $clusterIdentifier --approve-read-only-audit
if ($LASTEXITCODE -ne 0) { throw 'Preflight blocked; stop' }
```

The audit uses a dedicated connection, read-only startup settings and an explicit
repeatable-read read-only transaction, bounded by server/client timeouts and an
overall deadline. It rolls back and emits fixed failure messages rather than raw
database exceptions. Passing output is evidence only for that snapshot and target.

Once the forward SQL is reviewed, prepare the target-specific bundle OFFLINE:

```powershell
$forwardHash = (Get-FileHash -LiteralPath .local/Phase29I.forward.sql -Algorithm SHA256).Hash
./tools/SportStats.MigrationDeployment/New-DeploymentBundle.ps1 -InputSql .local/Phase29I.forward.sql -OutputSql .local/Phase29I.deployment.sql -ReviewedSha256 $forwardHash -Port $targetPort -SystemIdentifier $clusterIdentifier -Approval "PREPARE sportstats_test 127.0.0.1:$targetPort sportstats_migrator $clusterIdentifier"
Get-FileHash -LiteralPath .local/Phase29I.deployment.sql -Algorithm SHA256
```

The generator rejects SQL different from the exact checked-in bounded EF SQL
contract, modified input hashes, mismatched approval or output path reuse. Review
the entire generated bundle and its final hash before requesting deployment
approval. Do not treat calculating a hash as human review of the SQL.

Only AFTER explicit user approval naming the endpoint, cluster identifier,
database, role, final bundle SHA256 and maintenance window may an operator execute
the following command. The current request does **not** authorize executing it:

```powershell
# Operator must first compare the file hash with the separately approved hash.
# Use a clean operator shell; remove ambient PG* overrides and do not use transcripts.
$psqlExe = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
& $psqlExe -X -W -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $targetPort -d sportstats_test -U sportstats_migrator -f .local/Phase29I.deployment.sql
if ($LASTEXITCODE -ne 0) { throw 'Deployment failed or uncertain; keep writers stopped and inspect' }
```

Enter the password only at psql's private prompt. Do not pass a connection string
or password argument. The bundle owns its transaction; do not add `-1` or edit its
transaction boundaries. Its identity/history checks precede DDL and its exact
new-index and history assertions precede commit. After acquiring mapping and
history locks, it revalidates baseline columns, indexes, constraints and relation
safety before running the unchanged migration. All helper SQL and the generated
bundle are review inputs, not just the original EF script.

After confirmed commit, open a fresh read-only preflight session using the same
target flags with `--expect migrated` instead of `--expect baseline`. Require all
four exact catalog outcomes, both migration rows, intact baseline columns/FKs/legacy
indexes and zero duplicate groups before deciding whether writers may resume.
No insert/delete probes are allowed on this existing database. A failed fresh
postflight does not undo an already committed deployment; stop for investigation.

Catalog certification is scoped to the four mapping tables and migration history;
it does not certify unrelated tables, physical column order, database collation
configuration or operating-system/disk readiness. The preflight validates migration
IDs and history shape; the deployment additionally requires EF ProductVersion
`10.0.12`. Any version mismatch blocks deployment and needs review.

Preflight must pass in baseline mode: verified cluster/endpoint/database/session
and current role, exactly InitialCreate history, expected mapping columns and their
types/nullability/collations, primary/foreign keys, legacy indexes, no new index
name collisions or unexpected objects, and zero duplicate identity groups in all
four tables. Report counts without identity values. Inspect effective ownership,
CONNECT, schema USAGE/CREATE, SELECT, migration-history INSERT and required catalog
access without attempting DDL or test writes. Authentication is insufficient.

Review mapping row counts and relation sizes, active transactions/blockers, backup
and restore readiness, free disk/WAL capacity and maintenance duration. An audit
cannot certify disk headroom or restore readiness. Stop application writers,
importers, scheduled jobs, migrations and schema maintenance; keep them stopped
through fresh post-commit verification. Preflight is a snapshot, not a reservation;
repeat it immediately before the approved deployment. Any drift, unexpected history,
duplicates or missing privilege blocks application and needs a separately reviewed
plan. Do not delete mappings, mark history applied manually or conceal collisions
with `IF NOT EXISTS`.

## Transactions, locks and recovery

The unchanged migration sets transaction-local `lock_timeout = '5s'` and
`statement_timeout = '60s'`, then takes `SHARE ROW EXCLUSIVE` locks on all four
tables before auditing. Writers and competing maintenance can block or be blocked;
read-only preflight also holds ordinary read locks until its transaction ends.
Standard index builds block writes. These are per-statement timeouts, not an
overall outage deadline; do not silently increase them on failure. Large targets
may require a separately approved migration design. `CREATE INDEX CONCURRENTLY`
cannot simply replace these statements inside the transaction.

A normal PostgreSQL failure before commit rolls back all four new indexes and the
history row. Stop-on-error is mandatory. Never automatically retry a failed or
ambiguous result. On timeout, cancellation or lost connection, keep writers stopped
and use a fresh read-only session to inspect all four indexes and exact history.
Baseline-only state permits consideration of a newly approved retry after diagnosis;
fully migrated state requires verification, not replay. Mixed state is a blocker.
Do not kill blockers or change grants automatically.

`Down` drops only the new safeguards. It cannot undo committed imports or other
data changes, and permits duplicates that would block a later upgrade. Prefer
retaining the schema for application rollback when compatible. Any downgrade or
restore requires separate explicit approval, reviewed recovery SQL and a fresh
backup/data-loss assessment. Never downgrade through InitialCreate.

Read-only transactions prohibit persistent data and schema changes; they do not
mean zero server disk activity (for example, logging or query spill).
See [PostgreSQL read-only transactions](https://www.postgresql.org/docs/18/sql-set-transaction.html),
[locking](https://www.postgresql.org/docs/18/explicit-locking.html),
[index creation](https://www.postgresql.org/docs/18/sql-createindex.html), and
[EF migration script deployment](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying).

## Verification record

Final local results (2026-10-19):

| Check | Result |
| --- | --- |
| `dotnet test tests/SportStats.UnitTests --no-restore` | 908 passed; zero failed/skipped |
| `dotnet test tests/SportStats.IntegrationTests --no-restore --filter 'Category!=PostgreSQLLive'` | 179 passed; zero failed/skipped |
| `powershell -NoProfile -ExecutionPolicy Bypass -File tools/SportStats.MigrationDeployment/Test-DeploymentBundle.ps1` | 23 offline guard checks passed |
| `dotnet build SportStats.slnx --no-restore` | Passed; zero warnings/errors; includes preflight via test project reference |
| `dotnet build tools/SportStats.InitialImport.Verification --no-restore` | Passed; zero warnings/errors |
| Bounded EF SQL generation above | Passed offline |
| `git diff --check` | Passed (Git reports only its normal LF/CRLF conversion notice) |
| Independent Database specialist review | Passed for preparation; findings resolved |
| Independent Security specialist review of preflight | Passed for preparation; findings resolved |
| Existing database audit / migration deployment | NOT RUN; target identity unconfirmed |

The Database reviewer independently reviewed both preflight and deployment SQL.
The Security specialist implemented the offline bundle generator and independently
reviewed the other specialist's preflight implementation; the Tech Lead verified
integration and checks. Reviews corrected ANY-versus-ALL privilege checks, history
relation safety, RLS/triggers/rules/inheritance drift, locked baseline validation,
and the documented argument order. No unresolved blocking code findings remain.
Neither review constitutes live database certification. Offline regression checks
exercise selection/configuration/history/catalog comparison and bundle rejection,
not PostgreSQL execution of the newly prepared wrapper SQL.

Changed files:

- `tools/SportStats.MigrationPreflight/`: tool project, `Program.cs`,
  `PreflightContract.cs`, `CatalogContract.cs`.
- `tools/SportStats.MigrationDeployment/`: `New-DeploymentBundle.ps1`,
  `ExpectedMigration.sql`, `BaselineCatalog.sql`, `Test-DeploymentBundle.ps1`.
- `tests/SportStats.UnitTests/MigrationPreflightTests.cs` and its project reference.
- This document. The ignored `.local/Phase29I.forward.sql` is an offline review
  artifact; no actual-target deployment bundle has been prepared.

Next approval sequence: independently identify the nonproduction target and approve
its read-only audit; review passing baseline evidence, maintenance/backup readiness
and final bundle/hash; explicitly approve execution of that exact bundle against
that exact target; then obtain fresh migrated-mode evidence before release decisions.
Importer enablement and imports remain separately prohibited. Live target state
remains unverified until an approved, correctly pinned audit runs. No commits or
pushes were made.

## Failed-audit diagnostic preparation (2026-10-19)

The operator independently confirmed nonproduction endpoint `127.0.0.1:5432`,
`sportstats_test`, role `sportstats_migrator`, and cluster identifier
`<VERIFIED_CLUSTER_SYSTEM_IDENTIFIER>`. This supersedes the earlier identity-unconfirmed preparation
status above. The operator reports that build succeeded and the original preflight
failed with its generic suppressed-error message. That message provides no evidence
of which guard failed. No successful audit or deployment is established.

The updated preflight always emits a fixed failure stage/reason code and exits 1.
It never renders exception text, SQLSTATE values outside the allowlist, SQL,
connection strings, actual/expected identity fields, catalog values or duplicate
identity values. Progress and success output now suppress counts, sizes and target
values. A partial progress message is not an audit pass. Exit 0 and the final
`Read-only audit passed; selected identity/history/catalog verified. No deployment authorization implied.`
message are both required.

Execution order and safe interpretation:

| Stage token (prefix `PF_`) | Guard or operation |
| --- | --- |
| ARGUMENTS / CONFIGURATION | Explicit flags and allowlisted connection configuration; no connection yet |
| CONNECT | Authentication, endpoint reachability and startup session settings |
| BEGIN_READ_ONLY | Explicit repeatable-read, read-only transaction |
| IDENTITY_READ | Read combined identity using `pg_catalog.pg_control_system()` |
| IDENTITY_MATCH | Exact endpoint, database, current/session roles, read-only setting and cluster comparison |
| ROLE_PRIVILEGES | Non-superuser, no CREATEDB/CREATEROLE/REPLICATION, CONNECT |
| SCHEMA_PRIVILEGES | Both USAGE and CREATE on required schemas |
| HISTORY | Read and compare exact migration IDs for selected baseline/migrated mode |
| HISTORY_PRIVILEGES | Ordinary table, role ownership, SELECT/INSERT/DELETE privileges |
| RELATION_SAFETY | No unexpected inheritance, partitions, RLS, rules or triggers |
| HISTORY_CATALOG | Exact history columns, constraints and indexes |
| MAPPING_PRIVILEGES | Mapping table ownership, SELECT, ordinary table and no RLS |
| COLUMNS / INDEXES / CONSTRAINTS / TRIGGERS | Exact mapping catalog checks |
| DUPLICATES | Duplicate group count must be zero; identity values never printed |
| STATISTICS | Existing aggregate reads, with results suppressed |
| ROLLBACK | Explicit rollback; failure still exits 1 |

Reason tokens are fixed: AUTHENTICATION (SQLSTATE 28P01/28000), PERMISSION_DENIED
(42501), QUERY_CANCELED (57014), LOCK_UNAVAILABLE (55P03), DATABASE_UNAVAILABLE
(3D000), SERVER_REJECTED (other PostgreSQL errors), TIMEOUT_OR_CANCELED,
PROVIDER_OR_CONNECTIVITY, GUARD_OR_OPERATION_FAILED, or UNCLASSIFIED.
Unknown stage enum values produce UNKNOWN; arbitrary exception fields are never output.
Provider timeouts may surface as PROVIDER_OR_CONNECTIVITY; QUERY_CANCELED alone
cannot distinguish statement timeout from cancellation. Do not infer a data or
schema mismatch from a failed query. The identity classification below supersedes
the original combined IDENTITY_MATCH classification.

`PF_IDENTITY_READ/PERMISSION_DENIED` is consistent with missing access to
`pg_control_system()`; stop without granting privileges or using a superuser.
Ownership and schema CREATE/history INSERT/DELETE checks are intentional readiness
requirements even though this tool performs no writes. Authentication success does
not establish those privileges. Cluster identity does not prove nonproduction
classification; independent local inventory and no production forwarding remain
prerequisites.

### Next diagnostic command: separate approval required

Do not run this command as preparation. Obtain separate explicit authorization for
one read-only baseline audit of the exact endpoint/database/role/cluster above.
The switch records authorization, it does not obtain it. No automatic retry follows
failure, and no guard may be skipped. Baseline means only InitialCreate is expected;
do not switch to migrated to conceal an unexpected history.

Use a private, non-transcribed operator shell with no debugger, command tracing,
environment logging or screen recording. Existing OS/organization instrumentation
must not capture process memory or credentials. No password belongs in chat, command
arguments or files. From repository root, after approval only:

```powershell
powershell.exe -NoProfile -File .\tools\SportStats.MigrationPreflight\Invoke-ReadOnlyDiagnostic.ps1 -ApproveReadOnlyAudit
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic blocked; stop and review only the fixed failure code.' }
```

The checked-in wrapper builds before credential entry, uses Read-Host -AsSecureString
and a connection-string builder to quote passwords, pins all confirmed values,
selects SSL Mode Disable for the confirmed direct local TCP connection, and invokes
only the preflight DLL. There is no application configuration fallback, alternate
host, superuser retry, role change, migration or importer invocation. Existing
startup read-only settings, explicit transaction, strict comparisons, deadlines and
rollback remain unchanged. Do not use this procedure for a forwarded target.

Failure example (illustrative, not a diagnosis of the prior failure):

```text
Read-only preflight failed [PF_IDENTITY_READ/PERMISSION_DENIED]. No migration was attempted. Stop; raw errors are suppressed.
```

The wrapper follows with fixed `Diagnostic stopped. No migration was attempted;
raw errors are suppressed.` output and exit 1. Report only the fixed stage/reason
code. Do not collect raw exceptions, password values or full process environments.

The wrapper clears the process-local connection variable in finally, zeros the
unmanaged password buffer and disposes temporary secure storage. Its dedicated child
PowerShell process exits after the script, releasing managed credential strings
that cannot reliably be zeroed. It does not set User/Machine environment variables
or write credentials to repository files. If the parent shell retains a credential
from an earlier attempt, clear it without displaying it and close that shell:

```powershell
[Environment]::SetEnvironmentVariable('SPORTSTATS_MIGRATION_PREFLIGHT_CONNECTION_STRING', $null, [EnvironmentVariableTarget]::Process)
exit
```

No database connection or preflight execution is authorized by this diagnostic
preparation section. Missing access, schema drift, duplicates or mismatched history
require review rather than automatic privilege changes or data repair.

## Historical IDENTITY_MATCH investigation (2026-10-19; superseded by the correction below)

The reported `PF_IDENTITY_MATCH/GUARD_OR_OPERATION_FAILED` occurs after the
identity query and Npgsql `GetString` have completed. In the original code this
stage contains only `Exact`: ordinal sorting and sequence comparison, followed by
the guard throw. Authentication, pg_control_system permissions, query execution,
and provider materialization failures would occur at IDENTITY_READ or earlier.
An unexpected in-memory operation failure was also possible under the old generic
classification; the old token alone does not prove an actual identity mismatch.

A deterministic representation defect is established: `inet_server_addr()` returns
`inet`, and its explicit `::text` cast retains the netmask. For the confirmed IPv4
endpoint the field is `127.0.0.1/32`, while the expected field is `127.0.0.1`.
See PostgreSQL 18 [network functions](https://www.postgresql.org/docs/18/functions-net.html)
and [session information](https://www.postgresql.org/docs/18/functions-info.html).
This explains failure even on the intended target; it is not evidence of a different
cluster. It does not establish that every other identity field matches: the original
combined result was suppressed, and no new live query was run.

This change instruments diagnosis only. It retains the SQL, all explicit text
casts, startup settings, transaction, target selection, and cluster identifier.
It deliberately continues rejecting `/32`; no suffix stripping, alternate address,
case folding, numeric coercion, or relaxed matching is introduced. A representation
fix requires subsequent review. The identity row must contain exactly seven fields
and exactly one row. Checks run in database/current-role/session-role/address/port/
read-only/cluster order. The first failure stops execution; later fields remain
uncertified. Missing or additional delimiter-separated fields reject the row.

Allowlisted IDENTITY_MATCH reasons:

| Reason | Meaning |
| --- | --- |
| IDENTITY_ROW_COUNT / IDENTITY_SHAPE | Unexpected row cardinality or wire shape |
| DATABASE_MISMATCH | Exact database comparison failed |
| CURRENT_ROLE_MISMATCH / SESSION_ROLE_MISMATCH | Exact respective role comparison failed |
| ADDRESS_TEXT_REPRESENTATION | Exact selected address plus `/32` observed; still rejected |
| ADDRESS_MISMATCH / PORT_MISMATCH | Exact endpoint comparison failed |
| READ_ONLY_NOT_VERIFIED | Transaction setting is not exactly `on` |
| CLUSTER_MISMATCH | Exact selected cluster identifier comparison failed |
| IDENTITY_TYPE_CONVERSION_FAILED | InvalidCastException at identity read/match |
| IDENTITY_OPERATION_FAILED | Unexpected InvalidOperationException or unknown identity failure enum |
| IDENTITY_UNEXPECTED_EXCEPTION | Other unexpected exception at identity match |

Existing server, cancellation, and provider classifications remain distinct. No
exception messages, inner exceptions, SQL or identity values enter these tokens.
The new specialized exception contains only a fixed message and an allowlisted enum.

Offline verification: unit tests 931 passed, offline integration tests 179 passed
(Category!=PostgreSQLLive), zero failures/skips. Regression coverage includes the
exact original accepted identity, every field mismatch, `/32` representation,
different addresses/masks, malformed/null/extra rows, and operational classification.
Solution and InitialImport.Verification builds passed with zero warnings/errors.
The offline deployment bundle suite passed all 23 checks. `git diff --check`
passed; newly untracked diagnostic files also received whitespace checks.
Independent Database and Security reviews passed with no blockers. Both confirmed
strict rejection and unchanged read-only guarantees; neither connected to PostgreSQL.

### Historical diagnostic procedure (superseded; not authorization to execute)

Obtain explicit authorization for one read-only baseline diagnostic of
`127.0.0.1:5432`, database `sportstats_test`, role `sportstats_migrator`, independently
verified nonproduction cluster `<VERIFIED_CLUSTER_SYSTEM_IDENTIFIER>`. Retain the existing private,
non-transcribed shell and secure password procedure described above. Then run:

```powershell
powershell.exe -NoProfile -File .\tools\SportStats.MigrationPreflight\Invoke-ReadOnlyDiagnostic.ps1 -ApproveReadOnlyAudit
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic blocked; stop and review only the fixed failure code.' }
```

The wrapper builds before prompting, pins all target fields, opens the dedicated
bounded read-only session, and clears its process-local credential afterward.
Expected diagnosis for the known representation defect is
`PF_IDENTITY_MATCH/ADDRESS_TEXT_REPRESENTATION`, exit 1. Report only the fixed
code and stop; no automatic retry or guard bypass. A different code needs review.
This run cannot certify later fields after the first failure. No migration,
privilege change, data/schema modification, importer enablement, commit or push
is authorized or performed by this preparation.

## Endpoint address correction (Phase 2.9I)

The separately authorized read-only diagnostic confirmed
`PF_IDENTITY_MATCH/ADDRESS_TEXT_REPRESENTATION`. PostgreSQL's explicit
`inet_server_addr()::text` returned `127.0.0.1/32`; comparison with bare
`127.0.0.1` rejected the intended endpoint. The historical diagnostic-only behavior
and expected failure above are superseded by this correction.

The identity query now returns two columns: the six unchanged non-address text
fields and the raw PostgreSQL `inet` endpoint. Npgsql materializes the second
column with `GetFieldValue<NpgsqlInet>` so both address and prefix survive.
Acceptance requires all of: selected host exactly `127.0.0.1`, address family
`InterNetwork` (IPv4), netmask exactly `32`, and `IPAddress.Equals` against
`127.0.0.1`. There is no string parsing, suffix stripping, DNS resolution,
IPv4-mapped conversion or subnet containment. Other IPv4 addresses, IPv6,
IPv4-mapped IPv6, null/default inet and every non-/32 prefix fail closed.

Exactly one row and two columns are required, with exactly six non-address text
fields. Database, current role, session role, port, transaction read-only state
and cluster system identifier retain independent exact checks before any catalog
reads. Startup read-only settings, explicit repeatable-read read-only transaction,
rollback, bounded timeouts and connection restrictions are unchanged. The expected
cluster remains `<VERIFIED_CLUSTER_SYSTEM_IDENTIFIER>`. No database privilege, schema or data changes
are part of this fix.

Null and malformed fields reject; unexpected provider/type conversions propagate
to the fixed allowlisted diagnostics. The obsolete `ADDRESS_TEXT_REPRESENTATION`
reason is removed: a valid typed /32 endpoint now passes the address guard, while
invalid endpoints produce `ADDRESS_MISMATCH`. Provider and conversion failures
retain their separate fixed classifications. Raw values, exception messages,
inner errors and connection strings remain suppressed.

### Offline evidence and independent reviews

| Check | Result |
| --- | --- |
| `dotnet test tests/SportStats.UnitTests --no-restore` | 945 passed; zero failed/skipped |
| Targeted `IdentityVerificationTests` | 28 passed; zero failed/skipped |
| `dotnet test tests/SportStats.IntegrationTests --no-restore --filter 'Category!=PostgreSQLLive'` | 179 passed; zero failed/skipped |
| `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/SportStats.MigrationDeployment/Test-DeploymentBundle.ps1` | 23 offline guard checks passed |
| `dotnet build SportStats.slnx --no-restore` | Passed; zero warnings/errors |
| `dotnet build tools/SportStats.MigrationPreflight --no-restore` | Passed; zero warnings/errors |
| `dotnet build tools/SportStats.InitialImport.Verification --no-restore` | Passed; zero warnings/errors |
| `git diff --check` plus explicit checks of changed untracked files | Passed whitespace/conflict-marker checks |

Regression tests cover exact IPv4 /32, wrong IPv4, IPv6 loopback and mapped IPv6,
all IPv4 prefix values /0 through /31, null/default inet, selection aliases,
malformed/null/multiple identity rows, each independent identity mismatch, typed
reader materialization, DBNulls, wrong column count, wrong field types, and
provider/conversion failures with suppressed details. DataTableReader exercises
the shared typed materializer offline; it does not certify a live provider/server
round trip. That remains for the separately authorized audit below.

Independent Database and Security specialists reviewed production changes and
Security additionally reviewed regression coverage. Both approved with no blocking
findings: explicit family/prefix/address checks retain strict identity and all
other guards, and failures remain sanitized. Neither reviewer connected to
PostgreSQL or changed files. No unresolved implementation findings remain.
### Next separately authorized read-only baseline audit (not executed)

Obtain explicit separate authorization for one audit of `127.0.0.1:5432`, database
`sportstats_test`, role `sportstats_migrator`, baseline history, and independently
verified nonproduction cluster `<VERIFIED_CLUSTER_SYSTEM_IDENTIFIER>`. Use the private,
non-transcribed shell and secure password procedure already documented above.
Only after that authorization, run:

```powershell
powershell.exe -NoProfile -File .\tools\SportStats.MigrationPreflight\Invoke-ReadOnlyDiagnostic.ps1 -ApproveReadOnlyAudit
if ($LASTEXITCODE -ne 0) { throw 'Read-only audit blocked; review only the fixed failure code.' }
```

The wrapper builds before prompting, pins these target fields, opens a bounded
read-only session and clears its process-local credential afterward. A successful
full baseline audit returns exit 0 and the fixed read-only audit success message;
the address correction alone does not certify the remaining guards. On any
failure, report only the fixed stage/reason code and stop without automatic retry
or bypass. No migration, permission change, importer `--apply`, commit or push is
authorized by this procedure. This task did not connect to PostgreSQL or execute
this audit.

## HISTORY_CATALOG diagnosis (Phase 2.9I)

The reported `PF_HISTORY_CATALOG/GUARD_OR_OPERATION_FAILED` is ambiguous: all
three exact comparisons and provider operations previously shared one stage.
Offline analysis establishes a PostgreSQL 18 compatibility defect, but cannot
prove which operation failed on this target without a separately authorized run.
No PostgreSQL connection or live audit was made during this investigation.

PostgreSQL 18 stores column NOT NULL constraints in pg_constraint (`contype=n`):
[release notes](https://www.postgresql.org/docs/18/release-18.html) and
[catalog reference](https://www.postgresql.org/docs/18/catalog-pg-constraint.html).
The unfiltered ConstraintsSql selects every constraint for the relation but
expects exactly one primary-key row. Normal newly created PG18 history tables
can therefore fail despite matching EF's logical schema. The synthetic offline
regression reproduces this extra-row rejection; it does not read this database.
Actual target drift, earlier column mismatch or an operational exception remains
unresolved. No NOT NULL rows were filtered, ignored or normalized in this change.

EF's history model uses MigrationId (primary key, varchar(150)) and required
ProductVersion (varchar(32)); see [EF history implementation](https://github.com/dotnet/efcore/blob/v10.0.0/src/EFCore.Relational/Migrations/HistoryRepository.cs).
The repository references EF Core 10.0.12 and Npgsql EF provider 10.0.3. The
expected columns and PK/index definitions match that logical structure; their
catalog representation is version-sensitive. BaselineCatalog.sql also expects
one history constraint and mapping constraints exclude NOT NULL expectations;
these are related deployment compatibility risks, left strictly blocking.
A later separately scoped correction must validate PG18 metadata exactly across
all affected guards rather than bypassing or dropping constraint rows.

### Complete operation and assertion inventory

All operations use the same parameterized relation name, shared deadline,
NpgsqlCommand construction, AddWithValue(string), ExecuteReaderAsync,
ReadAsync, GetString(0), and async reader/command disposal. These can throw
provider/server, cancellation/timeout, InvalidOperationException or conversion
exceptions; cleanup failures can also surface. No endpoint inet materialization
occurs here: each query returns a single concatenated PostgreSQL text value.
Server text casts and catalog deparsing precede Npgsql GetString. There is no
client numeric, bool, OID or inet conversion in HISTORY_CATALOG.

| Operation | Query/assertions (unchanged) |
| --- | --- |
| COLUMNS | pg_attribute LEFT JOIN pg_attrdef; positive live attributes only; exact name, format_type including typmod, attnotnull, identity/generated markers, deparsed default, default collation or OID text. Exactly the two expected history rows. |
| CONSTRAINTS | All pg_constraint rows for to_regclass(parameter); exact name, validated, deferrable, initially deferred and pg_get_constraintdef. Exactly the expected PK row. PG18 NOT NULL rows are additional metadata and still reject. |
| INDEXES | pg_index joined to pg_class and pg_am; exact name, unique/primary/valid/ready/live flags, access method and pg_get_indexdef. Exactly the expected unique primary btree index. |

Rows are compared as ordinal sorted multisets, preserving duplicate counts.
Missing relations resolve to no rows and reject; missing/extra/changed/duplicate
objects reject. Query row order is irrelevant. Quoting, search_path-dependent
index rendering, collation, defaults, unexpected attributes and metadata changes
can produce mismatch without being provider failures. SQL references remain
valid in PG18; the constraint expectation is incomplete for PG18 metadata.

### Fixed diagnostic branches and interpretation

`PF_HISTORY_CATALOG/<operation>_<classification>` now identifies COLUMNS,
CONSTRAINTS or INDEXES. MISMATCH is emitted only after successful row retrieval
and an unequal exact comparison. PERMISSION_DENIED corresponds to SQLSTATE
42501; QUERY_CANCELED to 57014; LOCK_UNAVAILABLE to 55P03; other server errors
become SERVER_REJECTED. TYPE_CONVERSION_FAILED identifies InvalidCastException;
OPERATION_FAILED identifies InvalidOperationException during retrieval;
PROVIDER_OR_CONNECTIVITY identifies NpgsqlException; TIMEOUT_OR_CANCELED covers
client timeout/cancellation; UNCLASSIFIED covers other failures. Unknown enum
values produce only UNKNOWN. No message, SQL text, inner error string, values,
counts, credential or connection string reaches diagnostic output.

A CONSTRAINTS_MISMATCH supports the PG18 explanation but cannot distinguish
extra NOT NULL metadata from actual constraint drift by itself. A COLUMNS_MISMATCH
or INDEXES_MISMATCH identifies a different exact comparison requiring review.
PERMISSION_DENIED separates privileges from mismatch. SERVER_REJECTED cannot
alone distinguish SQL syntax, catalog access or other server errors; it requires
further separately reviewed diagnostics, never raw error output. The original
generic InvalidOperationException classification does not establish missing
privileges or a provider type error (those normally use different classifications).

Identity checks, read-only startup, explicit repeatable-read READ ONLY transaction,
parameterization, timeouts, stop-on-first-failure and rollback/disposal are unchanged.
This instrumentation changes diagnostics only; it does not repair the PG18
compatibility defect or authorize deployment.

### Offline validation

Unit tests: 951 passed; offline integration tests: 179 passed (filter
`Category!=PostgreSQLLive`); zero failed/skipped. Deployment bundle suite: 23
offline checks passed. Solution and InitialImport.Verification builds passed with
zero warnings/errors. git diff --check and explicit whitespace checks for the
untracked changed files passed. Regressions cover unchanged query ordering and
relation, strict multiset checks for every operation, PG18-style extra NOT NULL
metadata, first-failure stopping, all new exception branches for all operations,
unknown enum sanitization and exact fixed output with synthetic sensitive errors.
These tests do not certify PostgreSQL/Npgsql round trips.

### Next separately authorized diagnostic (not executed)

Obtain separate explicit authorization for one read-only baseline audit of
127.0.0.1:5432, sportstats_test, sportstats_migrator, independently verified
nonproduction cluster `<VERIFIED_CLUSTER_SYSTEM_IDENTIFIER>`. Use the private non-transcribed shell
and secure password procedure above. Only after authorization, run:

```powershell
powershell.exe -NoProfile -File .\tools\SportStats.MigrationPreflight\Invoke-ReadOnlyDiagnostic.ps1 -ApproveReadOnlyAudit
if ($LASTEXITCODE -ne 0) { throw 'Read-only diagnostic blocked; report only the fixed stage/reason code.' }
```

The existing wrapper builds before acquiring credentials, pins every target field,
clears process credentials and runs the bounded read-only preflight. It may proceed
to later guards if all history checks pass. Report only the fixed code; stop on
failure without retry, bypass, permission changes or raw metadata/error capture.
A CONSTRAINTS_MISMATCH is the leading expected diagnostic, not a proven target
result. No migration, importer --apply, role/schema/history change, commit or push
is authorized by this procedure or was performed by this task.

Independent Database and Security reviews passed with no blockers in this
diagnostic-only change. Database confirmed PG18 constraint representation and
text materialization compatibility, plus the related mapping/deployment risks.
Security confirmed fixed output, strict comparisons and unchanged read-only and
identity guarantees. Both reviewed source/tests without changes or database
access; they did not independently rerun tests. Sensitive inner exceptions remain
internal and must never be logged or rendered.

## Phase 2.9I PostgreSQL 18 compatibility correction

This section supersedes the diagnostic-only status and pending-correction statements
above. The latest separately authorized preflight returned the confirmed fixed code
`PF_HISTORY_CATALOG/CONSTRAINTS_MISMATCH`. That proves a successfully retrieved
constraint multiset differed; the code alone does not identify the target's extra
rows. Offline inspection confirmed the incompatible expectation: PostgreSQL 18
catalogs contain NOT NULL constraints, while history expected only its PK and the
mapping guards expected only PK/FK rows. No database connection or live audit was
made for this correction.

The shared preflight query now retains every constraint row. NOT NULL rows are
normalized by column identity rather than server-generated constraint name, and
must exactly match one row for each required column. Validation requires a single
live positive attribute with attnotnull, validated/enforced/nondeferrable/not
deferred/local/not inherited/not NO INHERIT metadata and no parent constraint.
Missing, duplicate, malformed, unexpected or altered rows reject. The history
contract requires MigrationId and ProductVersion; each mapping requires Id, its
target ID, ProviderKey, IdentityScope and ExternalId. Other constraints retain exact
names/definitions/flags and now also require enforcement. Column type, typmod,
nullability, collation/defaults, indexes, ownership, identity and relation safety
checks remain active. PostgreSQL 18 catalog semantics are documented in the
[official catalog reference](https://www.postgresql.org/docs/18/catalog-pg-constraint.html).
These guards intentionally target PostgreSQL 18; missing conenforced on older
servers fails closed rather than silently selecting a weaker contract.

Deployment BaselineCatalog.sql now separately validates the complete NOT NULL
multiset against independently verified columns for history and all mappings,
while retaining strict non-NOT-NULL checks. History PK name/deferred-state checks
are tightened. The initial-migration-verify.sql path previously excluded NOT NULL
rows; it now validates their full duplicate-preserving contract across the schema,
including enforcement/inheritance flags. Its diagnostic listing includes them.
DisposableMigrationVerification.SchemaSnapshotAsync already includes every
constraint and compares before/after snapshots; no equivalent exclusion exists.
Migration SQL contains no new table/role/data changes from this correction.

Changed files for this correction:
- tools/SportStats.MigrationPreflight/CatalogContract.cs
- tools/SportStats.MigrationPreflight/HistoryCatalogVerification.cs
- tools/SportStats.MigrationDeployment/BaselineCatalog.sql
- tools/SportStats.MigrationDeployment/Test-DeploymentBundle.ps1
- docs/initial-migration-verify.sql
- tests/SportStats.UnitTests/HistoryCatalogVerificationTests.cs
- tests/SportStats.UnitTests/MigrationPreflightTests.cs
- this runbook

Offline regression evidence: valid PG18 history and all four mapping contracts;
missing/extra/changed/duplicate rows; unexpected constraints; missing/incorrect PK;
incorrect column nullability; every NOT NULL flag; column/index drift; first-failure
stopping; provider/server/type failures and sanitized diagnostics. Deployment tests
also assert required SQL guard structure and ordering. These synthetic row tests
and SQL structural checks do not execute SQL or certify a server/provider round
trip; the target's complete baseline remains unverified until separately authorized.

Validation performed:
- Unit tests: 954 passed, zero failed/skipped.
- Offline integration: 179 passed, zero failed/skipped (`Category!=PostgreSQLLive`).
- Offline deployment guard suite: 42 checks passed; no database calls.
- Solution and InitialImport.Verification builds: zero warnings/errors.
- git diff --check passed (line-ending warnings only); changed untracked files
  additionally checked for whitespace because git diff omits them.

### Next separately authorized read-only baseline audit

Do not run as part of this correction. After separate explicit authorization,
use the private non-transcribed shell and secure password procedure described
above, from the repository root:

```powershell
powershell.exe -NoProfile -File .\tools\SportStats.MigrationPreflight\Invoke-ReadOnlyDiagnostic.ps1 -ApproveReadOnlyAudit
if ($LASTEXITCODE -ne 0) { throw 'Read-only baseline audit blocked; report only the fixed stage/reason code.' }
```

The wrapper pins 127.0.0.1:5432, sportstats_test, sportstats_migrator, baseline
history and cluster `<VERIFIED_CLUSTER_SYSTEM_IDENTIFIER>`. It builds before credentials, uses the
bounded read-only preflight and clears process credentials afterward. On failure,
stop without automatic retry, bypass or raw-error capture. This task did not
modify the database, apply migrations, enable importer --apply, commit or push.

Independent reviews for this correction: Database and Security both completed
read-only source review with no blocking findings. Database additionally ran the
9 HistoryCatalogVerification tests; Security ran 60 focused catalog, preflight,
identity and diagnostic tests, all passed. Both confirmed strict NOT NULL row
validation, preserved PK/FK/column/index checks and unchanged identity/read-only
protections. Database confirmed the remaining snapshot path retains all rows.
Neither connected to PostgreSQL; SQL received static review only. Final explicit
whitespace/conflict checks for all eight correction files and git diff --check
passed. Pre-existing uncommitted/untracked Phase 2.9I work was preserved.
