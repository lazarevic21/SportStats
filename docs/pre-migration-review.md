# Milestone 2.2 final pre-migration review

Review date: 2026-10-12. Branch remains `phase/02-backend`. No migrations or database operations were executed.

## Schema verdict

All eight approved domain entities and their relationships were reviewed. The single Infrastructure context maps twelve entity tables across `platform` and `football`: eight domain entities plus four typed identity mappings. UUIDs are assigned in application code. Natural keys are unique for Sport Code, (SportId, Competition Code), and (CompetitionId, Season Key). Composite participation foreign keys enforce same-season match teams and standing-row membership. Snapshot rows additionally reference the snapshot's (Id, SeasonId) alternate key. Direct team references are restrictive. Snapshot-to-row deletion is the only cascade.

Fixture, participation, snapshot retrieval, and identity lookup indexes are appropriate for the approved model. Snapshot indexes include TableType; a future latest-snapshot query across multiple table types may require an additional index after measuring its actual query plan. Standing positions are not unique. Scores are nullable with nonnegative-known-goal checks; identical match teams are rejected. DateOnly maps to date and explicitly UTC DateTime to timestamp with time zone. Retrieval time remains distinct from any future validated provider update time.

Migration history is configured as `platform.__EFMigrationsHistory` and covered by an offline options test. No such table has been created. One review correction makes unknown persisted match-status strings map safely to Unknown using the existing domain parser, with a converter regression test. Stored status names remain unchanged.

Platform Sport/Competition/Season are sport-neutral. Separate sport schemas and season participation support future sports and leagues without fixed team/match counts. Private setters and constructor guards enforce football classification, immutable Sport Code, date ordering, and positive positions in supported application paths. Raw SQL and direct EF property manipulation remain trusted paths that can bypass those domain rules. Loading a Season for new football record construction requires its Competition/Sport navigation graph; no lazy-loading proxies are configured.

## Accepted decisions and remaining risks

- Keep minimal independently nullable home/away goals. Provider-specific score selection and status translation require evidence before import.
- Keep standing aggregates deferred. A snapshot scope must represent a single table/group with at most one row per team; actual payload membership, position, grouping, and completeness remain unvalidated.
- Keep required opaque provider scopes and nonunique lookup indexes. Ambiguous duplicates are possible; future mapping/import must detect them rather than select an arbitrary record. Normalization and actual provider scope vocabulary remain unvalidated.
- Provisionally retain 30 days of prior standings observations, latest successful snapshots per season/provider/scope, and necessary final-season history. Preserve separate table types. Define success, completeness, duplicate handling, and final-history preservation before implementing retention. No jobs or DELETE grants are included.
- Verify effective privileges before application, including inherited/PUBLIC access, ownership, migration-history access, and default privileges.

No credentials, migrations, generated build outputs, provider clients, or unintended resource endpoints were found in reviewed pending files. Test credentials are synthetic. Existing ignored local build/cache directories are not pending Git changes.

## Database privilege assessment

`psql` exists at `C:\Program Files\PostgreSQL\18\bin\psql.exe`. This session has no configured password file, PGPASSWORD, PGSERVICE, application connection environment value, or API User Secret connection. Only presence/key checks were performed; secret values were not printed. No authenticated catalog query was executed. Consequently no actual missing or excessive privilege can be asserted. The user's earlier successful authentication remains user-reported evidence of connectivity, not an effective-grant audit.

The [read-only audit](database-privilege-audit.sql) inspects role flags, effective database/schema/table rights, memberships and SET ROLE paths, ownership, ACLs and default privileges without querying password catalogs. Run it through a securely authenticated local session:

```powershell
& 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -X -w -h 127.0.0.1 -p 5432 -d sportstats_dev -U sportstats_migrator -v ON_ERROR_STOP=1 -f docs/database-privilege-audit.sql
```

Use a securely provisioned password file or another authorized authentication method; do not put passwords in arguments or chat. The query functions assess both roles from one session. Compare role flags and membership paths as well as effective rights. An absent schema yields null privileges, not a demonstrated missing grant on an existing schema. Review any existing application tables and history before applying an initial migration to avoid object-name collisions. An empty table result alone is not proof of a suitable role setup.

Required migrator rights: CONNECT; ownership of objects it must alter; USAGE/CREATE on existing application schemas; database CREATE if it will create absent schemas. Runtime needs CONNECT and schema USAGE plus only the table access required by approved features. UUID generation requires no sequence grants. PostgreSQL distinguishes object ownership from grantable DML privileges; effective rights can come from PUBLIC or memberships. See [PostgreSQL privileges](https://www.postgresql.org/docs/18/ddl-priv.html) and [privilege inquiry functions](https://www.postgresql.org/docs/18/functions-info.html).

## Conditional SQL for separate approval only

The following is a proposal, not a finding that any grant is missing. Execute nothing until the audit is reviewed. Have an authorized owner execute only the necessary statements. Schema-level grants require existing schemas; table grants require the future reviewed migration to have created those tables. Do not run this as a single pre-migration script.

```sql
-- Pre-application only if the audit establishes these grants are absent:
GRANT CONNECT ON DATABASE sportstats_dev TO sportstats_migrator;
GRANT CONNECT ON DATABASE sportstats_dev TO sportstats_app;

-- Only if the migrator will create absent platform/football schemas:
GRANT CREATE ON DATABASE sportstats_dev TO sportstats_migrator;

-- Only after the schemas exist, if needed:
GRANT USAGE, CREATE ON SCHEMA platform, football TO sportstats_migrator;
GRANT USAGE ON SCHEMA platform, football TO sportstats_app;

-- Post-application proposal for future read features, explicit allowlist:
GRANT SELECT ON TABLE
  platform.sports, platform.competitions, platform.seasons,
  platform.competition_external_ids, platform.season_external_ids,
  football.teams, football.season_teams, football.matches,
  football.standing_snapshots, football.standing_rows,
  football.team_external_ids, football.match_external_ids
TO sportstats_app;

-- Separate future approval for write/import features, currently absent:
GRANT INSERT, UPDATE ON TABLE
  platform.sports, platform.competitions, platform.seasons,
  platform.competition_external_ids, platform.season_external_ids,
  football.teams, football.season_teams, football.matches,
  football.standing_snapshots, football.standing_rows,
  football.team_external_ids, football.match_external_ids
TO sportstats_app;
```

No DELETE, TRUNCATE, ownership, migration-role membership, elevated role attributes, or runtime migration-history access is proposed. Avoid broad all-table/default-table runtime grants because those can include migration history. Apply explicit allowlists for future tables. Any existing excessive direct, inherited, or PUBLIC rights require separately scoped revocation SQL after seeing the audit; a direct REVOKE from runtime alone cannot remove inherited grants. Schema/object owner corrections likewise need concrete ownership evidence before proposing changes.

## Migration readiness and exact next steps

The model is ready for migration-generation approval. The standard CLI workflow is not yet runnable: local/global `dotnet-ef` tool inventories are empty and Microsoft.EntityFrameworkCore.Design is absent. No design-time factory exists; normal host-based context creation will need a securely configured connection string, though generation itself need not connect to PostgreSQL. See [EF CLI prerequisites](https://learn.microsoft.com/en-us/ef/core/cli/dotnet).

1. Review this report and approve generation/tooling setup separately. Install a repository-local dotnet-ef 10.0.12 tool and add private EF Design 10.0.12 tooling dependencies in the chosen startup/design project. Resolve design-time creation through secure configuration or a dedicated nonconnecting factory. No credentials belong in tracked tooling files.
2. After that approval, restore/build/test and generate the initial migration with Infrastructure as target project, API as startup project, and SportStatsDbContext explicitly selected. Review generated migration, snapshot, schemas, FK/check SQL, indexes, and history configuration before application.
3. Securely run the read-only privilege audit and review results. Approve only necessary provisioning/grant changes separately; confirm ownership and existing-object compatibility. This audit can precede step 1 if secure access becomes available.
4. Obtain separate application approval after migration/SQL and privilege review. Apply with the migration role, then verify database constraints and runtime least privilege without using migrator credentials in the API.

Migration application is not ready until authenticated privilege/ownership review and generated SQL review are complete. No migration generation, tooling installation, grants, role changes, or database writes were performed in this review.

Final checks after the status-converter correction: restore succeeded; build succeeded with zero warnings/errors; all 60 tests passed (49 unit, 11 integration). Tests used offline/synthetic configuration and ran outside the sandbox for the established Windows Event Log limitation. Git branch/status and diff whitespace were checked; pending files remain uncommitted for review.
