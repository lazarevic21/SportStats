# Milestone 2.2 database implementation

## Scope and environment

One Infrastructure-owned `SportStatsDbContext` uses a single PostgreSQL database with `platform` and `football` schemas. Domain entities are framework independent. The context is registered lazily: API startup and `/health` do not resolve it or connect to PostgreSQL. Health is application liveness, not database readiness. The first migration, `20261012165516_InitialCreate`, was generated and reviewed offline, then successfully applied locally to `sportstats_dev` using `sportstats_migrator`. User-reported catalog verification confirms 12 application tables across both schemas, exactly one EF Core history entry, reviewed constraints, and 36 indexes. Provider clients, synchronization, caching, resource endpoints, retention jobs, Player, and TopScorer remain outside this milestone.

User-reported development installation: PostgreSQL 18.6 on Windows 10, `127.0.0.1:5432`, database `sportstats_dev`, migration role `sportstats_migrator`, runtime role `sportstats_app`, timezone UTC, listener `localhost`. A user-reported privilege audit preceded application, and the application guards verified object ownership. Authentication alone does not establish effective privileges; future runtime grants remain separately reviewed.

Dependencies remain pinned to EF Core 10.0.12 and Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3. Verification uses the restored dependency graph and offline EF metadata tests.

## Entities and relational model

| Schema | Table / entity | Keys and important constraints |
| --- | --- | --- |
| platform | sports / Sport | Client-assigned UUID; unique immutable domain Code |
| platform | competitions / Competition | UUID; Sport FK; unique (SportId, Code) |
| platform | seasons / Season | UUID; Competition FK; unique (CompetitionId, Key); nullable date bounds |
| football | teams / FootballTeam | UUID; required Name; optional ShortName and CrestUrl |
| football | season_teams / FootballSeasonTeam | Composite (SeasonId, TeamId); Season and Team FKs |
| football | matches / FootballMatch | UUID; Season FK; distinct home/away teams; nullable nonnegative goals |
| football | standing_snapshots / FootballStandingSnapshot | UUID; Season FK; required provider, table type, scope, UTC retrieval time; alternate (Id, SeasonId) key |
| football | standing_rows / FootballStandingRow | Composite (SnapshotId, TeamId); Position; redundant SeasonId for relational membership enforcement |

Matches reference season participation through two composite foreign keys `(SeasonId, HomeTeamId)` and `(SeasonId, AwayTeamId)`. Standing rows reference both `(SnapshotId, SeasonId)` and `(SeasonId, TeamId)`, guaranteeing that their team participates in their snapshot's season. Direct team navigation foreign keys are also explicitly restrictive. All foreign keys use restricted deletion except snapshot-to-row deletion, which cascades. No cascading deletion removes participation, matches, or shared metadata.

Football constructors reject non-football seasons, mismatched participation seasons, identical teams, negative known goals, and non-UTC timestamps. Private setters preserve public API invariants; the database does not independently enforce the cross-schema sport classification or immutable sport Code against direct SQL writes. Domain football classification uses the canonical `football` Sport Code. Domain required text is trimmed, and both known season dates must be ordered start <= end. These domain conventions are review assumptions; database text uniqueness is case sensitive. No league size or fixture count is assumed.

Scores retain independently nullable home/away goals through a football score value object. Null differs from zero. No extra-time, shootout, awarded-result, or arithmetic result assumptions are made. Domain unknown enum values become Unknown; no provider status translator exists yet. Match statuses are persisted as enum names.

Standing rows include only membership and position. Suggested Played, Won, Drawn, Lost, GoalsFor, GoalsAgainst, GoalDifference, and Points columns are deliberately deferred because [provider evidence](api-validation.md) validates no nested aggregate fields. Positions are positive domain values and are not unique within a snapshot. Negative points and goal difference must remain permissible when those fields are validated and added; there are currently no aggregate columns or arithmetic checks. TableType and Scope are opaque required strings supplied by callers, without invented provider vocabularies. Scope semantics and one-row-per-team snapshot identity require review against actual provider payloads.

UUID identifiers are supplied by callers/domain constructors, mapped as PostgreSQL uuid with no database-generated defaults. DateOnly values map to PostgreSQL date; UTC DateTime values map to timestamp with time zone. RetrievedAtUtc is the application retrieval observation and is not a provider update time; no unvalidated provider timestamp column exists. Strings map to PostgreSQL text without invented maximum lengths. Metadata string identifiers use exact stored case; normalization policy remains an application decision.

Indexes support fixture retrieval `(SeasonId, KickoffUtc)` and `(SeasonId, Status, KickoffUtc)`, team-season participation `(TeamId, SeasonId)`, and snapshot retrieval `(SeasonId, ProviderKey, TableType, Scope, RetrievedAtUtc)`. EF foreign-key indexes support membership and identity joins.

## Provider identity mappings

Infrastructure has separate CompetitionExternalIdentity, SeasonExternalIdentity, FootballTeamExternalIdentity, and FootballMatchExternalIdentity tables. Each has a client-assigned UUID key, required ProviderKey, ExternalId string, required opaque IdentityScope, and a real typed entity foreign key. Competition and season mappings live in platform; football team and match mappings live in football. None uses a provider ID as an internal primary key or a polymorphic internal target.

Lookup indexes on `(ProviderKey, IdentityScope, ExternalId)` are deliberately nonunique until provider identity scope is verified. Duplicate mappings remain possible; future ingestion must resolve ambiguity rather than assume a single result. IdentityScope records an explicit caller-supplied scope, but it does not prove provider scope rules. There is no unique reverse mapping assumption either.

## Credential-safe configuration

Use development User Secrets or securely injected environment variables for `ConnectionStrings:SportStats` (environment key `ConnectionStrings__SportStats`). Supply application-role credentials only to the runtime. Keep migration credentials separate from runtime configuration and supply them only through an approved secure migration process. No passwords belong in tracked settings, launch profiles, test fixtures, documentation, or shell-history command arguments. User Secrets are local development storage rather than an encrypted production vault.

Missing configuration fails only when persistence is requested, with a sanitized configuration error. EF sensitive-data logging and detailed errors are disabled. No startup code calls Migrate, EnsureCreated, or EnsureDeleted. Npgsql UTC timestamp handling does not depend on the database session timezone to validate incoming DateTime kinds.

## Approved provisional snapshot retention policy

Append each successfully accepted standings observation as a snapshot. Provisionally retain 30 days of older observations, preserve the latest successful snapshot per season/provider/scope, and preserve necessary final-season history. Keeping the latest per `(SeasonId, ProviderKey, TableType, Scope)` avoids mixing different table types and preserves at least the latest across them. Define successful and complete observations, duplicate handling, final-season preservation criteria, corrections, and audit requirements before implementing any purge. Any future deletion removes snapshot rows through the sole cascade relationship. No retention job or runtime DELETE grant is implemented.

## Pre-migration review decisions and remaining validation

1. **Match score semantics:** approved minimal independently nullable goals; no period semantics. Provider score selection, corrections, awarded/abandoned results, and status translation require evidence before import, without expanding this model speculatively.
2. **Standings snapshot fields:** aggregates remain deferred by approval. Validate row membership/position, scope boundaries, grouping, duplicate team appearances, and completeness before import. The approved row key represents one team per selected snapshot scope; split separate groups/tables into separate snapshots rather than dropping duplicate entries.
3. **Provider identifier scope:** explicit opaque scopes and nonunique lookup indexes remain approved. Scope vocabulary, reuse, normalization, and ambiguity handling need evidence before synchronization; do not add global uniqueness.
4. **Snapshot retention:** provisional 30-day policy approved with latest-successful and necessary final-season preservation. Success/completeness and final-history selection rules remain required before retention implementation. No deletion jobs or DELETE grants are approved here.
5. **Database privileges:** confirm ownership and effective grants through PUBLIC and memberships, schema creation rights, migration-history access, and separate runtime/migration credentials for future targets. Authentication alone is insufficient. Runtime and design-time registration configure `platform.__EFMigrationsHistory`, which now exists locally with exactly one entry for `20261012165516_InitialCreate`.

## Privilege review before migrations

This historical pre-application least-privilege plan remains guidance for future targets and runtime features. Local application has completed after a user-reported audit. The application workflow executes no grants or role changes and does not establish future runtime table access.

| Role | Proposed privileges | Required review |
| --- | --- | --- |
| sportstats_migrator | Database CONNECT; USAGE and CREATE on platform/football; ownership of its tables, constraints, indexes, and platform migration history | Database CREATE is required if it creates the schemas; alternatively have an authorized owner precreate them. Existing objects require ownership or an approved owner role for DDL. Confirm migration-history ownership. |
| sportstats_app | Database CONNECT; USAGE on platform and football; explicitly approved application-table SELECT/INSERT/UPDATE for future runtime features | DELETE requires retention approval. Exclude migration-history access, schema/database CREATE, ownership, migration-role membership, superuser and role-administration privileges. |

Client-generated UUIDs require no identifier sequences. Future sequence use requires separately reviewed privileges. Existing-object grants and default privileges for future objects are separate concerns. Default privileges must target the actual object-creating role and do not retroactively grant existing objects. Object alteration follows ownership. See the previously reviewed [PostgreSQL 18 privilege documentation](https://www.postgresql.org/docs/18/ddl-priv.html) and [default privileges reference](https://www.postgresql.org/docs/18/sql-alterdefaultprivileges.html).

## Verification boundary

Domain tests and relational metadata tests run offline, using synthetic configuration. They verify invariant behavior, keys, required/optional fields, PostgreSQL types, check constraints, indexes, explicit typed provider foreign keys, and deletion rules. Startup/liveness tests verify missing database configuration does not require a connection. Offline tests do not prove execution of PostgreSQL DDL or role permissions. The final implementation report records restore/build/test/diff-check results.

Current applied-state verification on 2026-10-12 (user-reported): `20261012165516_InitialCreate` was successfully applied to `sportstats_dev` using `sportstats_migrator`; 12 application tables, exactly one EF Core history entry, reviewed constraints, and 36 indexes were verified. Post-application build succeeded, all 62 tests passed (51 unit, 11 integration), and `git diff --check` passed. Offline project tests do not independently verify the live catalog or runtime permissions.

Initial migration generation and local application were separately approved and completed. See the [application record and guarded workflow](initial-migration-application.md) for successful local verification and the UTF-8 BOM correction. Do not rerun initial application against the populated database.

The historical [pre-migration review](pre-migration-review.md) records accepted decisions, then-current tooling prerequisites, the earlier secure-access limitation, and proposed privilege SQL. Its unapplied-state statements and earlier test totals describe that stage, not the current database. That review also corrected persisted-status conversion to handle unknown values safely.

The historical [initial migration review](initial-migration-review.md) records approved generation and artifacts before application; its statements about absent database objects refer to that earlier stage. EF Core Relational is explicitly pinned to 10.0.12 alongside Core/Design; Npgsql provider remains 10.0.3. The nonconnecting design-time factory uses no connection string or credentials and keeps the same platform history schema and disabled sensitive diagnostics as runtime.
