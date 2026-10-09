# SportStats

SportStats is a football-first full-stack portfolio project, initially focused on the English Premier League. It demonstrates maintainable architecture, secure authentication, automated testing, measurable performance, and a polished responsive UI.

## Project status

Phase 0: repository setup. Application implementation has not started. Phase 1 requires completion and approval of Phase 0.

## Planned technology stack

- Backend: C# and ASP.NET Core Web API
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

Setup and run commands will be documented when the application projects are implemented. There is no runnable application yet.

## Longer-term roadmap

- What If? League Simulator
- Smart Match Analysis
- Team DNA
- Player Evolution
- Additional supported football competitions
- Basketball, if pursued later
