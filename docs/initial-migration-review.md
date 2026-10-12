# InitialCreate generation and review

Generated on 2026-10-12 with user approval for tooling and generation only. Work remains uncommitted on `phase/02-backend`. PostgreSQL was not contacted or modified; no database update or generated SQL execution occurred.

## Tooling and generation

Repository-local `dotnet-tools.json` pins dotnet-ef 10.0.12 without roll-forward. Restore it with `dotnet tool restore`. Infrastructure and API reference EF Design 10.0.12 privately: Infrastructure owns the factory/migrations, while API is the startup project required by tooling. EF Relational is explicitly pinned to 10.0.12 to resolve an assembly-version conflict exposed by adding Design; final build has no warnings. Core stays 10.0.12 and the Npgsql provider stays 10.0.3.

`SportStatsDesignTimeDbContextFactory` configures Npgsql without any connection string or configuration lookup, so generation requires no credentials, host startup, or database connection. It disables sensitive logging/detailed errors and configures `platform.__EFMigrationsHistory`. Runtime still resolves configuration lazily through its existing sanitized helper; the factory is not registered as a runtime service.

Command executed from repository root (using the installed .NET executable and a process-local PATH containing its directory):

```powershell
dotnet tool run dotnet-ef migrations add InitialCreate --project src/SportStats.Infrastructure --startup-project src/SportStats.Api --context SportStatsDbContext --output-dir Persistence/Migrations
```

Generated files:

- `src/SportStats.Infrastructure/Persistence/Migrations/20261012165516_InitialCreate.cs`
- `src/SportStats.Infrastructure/Persistence/Migrations/20261012165516_InitialCreate.Designer.cs`
- `src/SportStats.Infrastructure/Persistence/Migrations/SportStatsDbContextModelSnapshot.cs`

## Review results

| Schema | Application tables |
| --- | --- |
| platform | sports, competitions, seasons, competition_external_ids, season_external_ids |
| football | teams, season_teams, matches, standing_snapshots, standing_rows, team_external_ids, match_external_ids |

All 12 expected tables are present. There are 12 UUID-based primary keys, including the two composite participation/standing-row keys; no UUID defaults or identifier sequences are generated. There are 17 foreign keys: 16 Restrict and one snapshot-to-row Cascade. Composite match participation FKs and row-to-participation/snapshot FKs enforce season consistency. The snapshot (Id, SeasonId) alternate key supports the latter relationship.

There are 22 explicit indexes, including three unique metadata indexes (Sport Code, Competition SportId/Code, Season CompetitionId/Key). Provider lookup indexes are nonunique on ProviderKey/IdentityScope/ExternalId. Fixture, participation, snapshot retrieval, and FK indexes match the approved model. Standing positions have no uniqueness constraint.

The three match checks enforce different teams and nonnegative known goals. HomeGoals/AwayGoals remain independently nullable integer columns. KickoffUtc is nullable timestamp with time zone; RetrievedAtUtc is required timestamp with time zone. Season dates are nullable PostgreSQL date columns. Domain checks continue to require UTC timestamps and football ownership in supported construction paths.

The migration contains only two schema operations, twelve table operations, and twenty-two index operations. No unsupported aggregates, players/scorers, raw SQL, seed rows, extensions, grants, roles, or unexpected objects are introduced. Down drops tables in dependency-safe order; it does not drop the schemas. The provider's model-level identity-by-default annotation does not create identity columns or sequences for client-assigned UUID keys.

EF migration history is intentionally separate from the 12 application tables: its schema is provider configuration rather than a CreateTable operation in InitialCreate or an entity in the model snapshot. Both runtime and design-time options specify `platform.__EFMigrationsHistory`. No history table was created.

## Verification and application boundary

Restore passed (NuGet downloads required approved network access). Final solution build passed with zero warnings/errors. All 62 tests passed: 51 unit and 11 integration. New offline tests inspect generated migration operations, confirm snapshot/model consistency, and verify credential-free factory/history/diagnostics configuration. Tests ran outside the sandbox for the established Windows Event Log permission limitation. Git status/branch and diff whitespace checks passed.

No unresolved generated-migration blocker was found by the Tech Lead or independent Database reviewer. Previously documented provider-contract validation and effective privilege/ownership checks still apply before import/application. This generation-only factory deliberately has no application connection; a future approved application workflow must provide migrator credentials securely, without exposing them in CLI arguments, tracked files, or API runtime configuration. Do not use the runtime application credentials for DDL.

Next: review these artifacts, audit effective database privileges through secure read-only access, review the future application SQL, and approve any required grants separately. Migration application requires explicit approval. No PostgreSQL objects, roles, grants, or data were changed, and no Git commit/push/merge/branch switch occurred.
