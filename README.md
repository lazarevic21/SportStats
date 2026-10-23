# SportStats

SportStats is a football-first full-stack portfolio project, initially focused on the English Premier League. It demonstrates maintainable architecture, secure authentication, automated testing, measurable performance, and a polished responsive UI.

## Project status

Milestone 2.1 adds the .NET 10 backend solution, layer registration, configuration, health endpoint, and initial tests. See [backend setup](docs/backend-setup.md) for commands and scope. Phase 1 evidence remains documented in [the validation summary](docs/phase-01-summary.md).

Milestone 2.2 adds platform and football domain entities, an Infrastructure-owned EF Core/Npgsql context, Fluent mappings, typed external identity mappings, and domain/model tests. Startup and `/health` remain database-independent. See [database model and unresolved decisions](docs/database.md). Standing aggregates remain deferred pending provider evidence. `20261012165516_InitialCreate` has been successfully applied locally to `sportstats_dev` using `sportstats_migrator`. User-reported verification confirms 12 application tables across `platform` and `football`, exactly one EF Core migration history entry, reviewed constraints and 36 indexes, a successful build, all 62 tests passing, and `git diff --check` passing.

The historical [final pre-migration review](docs/pre-migration-review.md) records accepted model decisions and the earlier privilege-audit limitation. The historical [initial migration review](docs/initial-migration-review.md) records generation-stage artifacts, tooling, and verification. Their unapplied-state statements describe those earlier stages.

The [application record and guarded instructions](docs/initial-migration-application.md) document successful local application, private password prompting, verification, and the UTF-8 BOM correction. The retained initial application workflow requires an approved empty target; do not rerun it against the populated local database.

## Planned technology stack

- Backend: C# and ASP.NET Core Web API (.NET 10)
- Frontend: React, TypeScript, and Vite
- Database: PostgreSQL and Entity Framework Core
- Authentication: ASP.NET Core Identity with JWT
- Testing: xUnit, PostgreSQL integration tests, Vitest, and Playwright
- Architecture: modular monolith with Domain, Application, Infrastructure, and API responsibilities

## V1 scope

- Responsive Premier League dashboard
- Fixtures and delayed match results
- Standings
- Team pages and match details using available provider fields
- Search and filtering
- User authentication and favorite teams
- Loading, error, empty, and stale-data states

The primary provider is [football-data.org API v4](https://docs.football-data.org/general/v4/index.html), starting with its free plan and Premier League (`PL`). Scores and schedules are delayed; V1 does not promise real-time scores. Squads, player profiles, lineups, and advanced statistics are outside the guaranteed scope until actual free-plan access is validated.

Football data provided by the Football-Data.org API.

Provider requests will run on the backend through an adapter, with background synchronization and caching. API credentials must use environment variables or .NET user secrets, never frontend configuration or tracked files. Any synthetic demo functionality will be clearly labeled and kept separate from provider data.

## Git workflow

Repository: https://github.com/lazarevic21/SportStats

Permanent branches:

- `main`: stable, reviewed versions; intended GitHub default branch
- `dev`: integration branch

Phase branches, each created from the latest `dev`:

1. `phase/01-api-validation`: validate provider access, fields, and free-plan restrictions
2. `phase/02-backend`: API, persistence, synchronization, and backend tests
3. `phase/03-frontend`: responsive UI and frontend tests
4. `phase/04-auth`: account lifecycle and favorite teams
5. `phase/05-testing-polish`: end-to-end testing, accessibility, resilience, and performance

The initial Phase 0 setup is the bootstrap commit on `main`; `dev` is then created from it. All subsequent development takes place on phase branches, never directly on `main` or `dev`.

Before a phase branch is created, update `dev`:

```powershell
git fetch origin
git switch dev
git pull --ff-only origin dev
git switch -c phase/01-api-validation
```

Repeat with the appropriate phase name only after the preceding phase is completed and approved. If a fast-forward pull fails, stop and review the divergence instead of resetting or force-pushing.

Use meaningful Conventional Commits, for example:

```text
chore(repo): initialize project documentation and ignore rules
feat(matches): add Premier League fixture queries
fix(sync): preserve missing scores during imports
test(auth): verify favorite team ownership
```

After each phase, run relevant checks and review the diff. Open a pull request from the phase branch into `dev`. Promote `dev` into `main` through a separate reviewed pull request after approval.

Only the Tech Lead coordinates commits, merges, and pushes. Confirmation is required before creating the GitHub repository, pushing, or merging. Never force-push or delete branches without explicit permission, including automatic branch deletion after PR merges.

## Local development

Install the .NET SDK specified by `global.json`, then follow [backend setup](docs/backend-setup.md) for the original setup workflow. Read-only [GET /api/competitions](docs/competitions-api.md) and [GET /api/competitions/{competitionId}/seasons](docs/competition-seasons-api.md) are implemented with offline unit/API verification. Their PostgreSQL execution remains unvalidated; `/health` remains database-independent. Earlier setup/database documents describe historical milestones. The Phase 2.9I access-boundary finding remains UNRESOLVED, and migration/deployment remain NO-GO. Live database access, configuration and provisioning require separate authorization.

## Longer-term roadmap

Read-only [GET /api/teams](docs/teams-api.md) adds bounded football-team identity responses with offline verification. Competition filtering is deferred to an explicitly defined season-membership query; unsupported filters are rejected. No live database authorization is added.

Read-only [GET /api/matches](docs/matches-api.md) adds bounded match details with optional season, team and UTC date filters. Phase 2.9I remains unresolved; migration and deployment remain NO-GO.

Read-only [GET /api/seasons/{seasonId}/standings](docs/standings-api.md) returns a bounded page of ranks and teams from the season's latest persisted standings snapshot, with its source metadata.

- What If? League Simulator
- Smart Match Analysis
- Team DNA
- Player Evolution
- Additional supported football competitions
- Basketball, if pursued later
