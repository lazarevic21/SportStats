# Read-only matches API

`GET /api/matches` reads persisted football matches. It does not call a provider or
write application state. This milestone does not resolve Phase 2.9I: migration and
deployment remain **NO-GO**. The live integration harness remains disabled by default.

## Query contract

| Parameter | Default | Meaning |
| --- | --- | --- |
| `page` | `1` | Positive one-based integer; computed offset must fit Int32. |
| `pageSize` | `20` | Integer from 1 through 100. |
| `seasonId` | omitted | Nonempty hyphenated GUID (D format) identifying a persisted season. |
| `teamId` | omitted | Nonempty hyphenated GUID (D format) matching either the home or away team. |
| `from` | omitted | Exact `yyyy-MM-dd`; inclusive UTC midnight kickoff lower bound. |
| `to` | omitted | Exact `yyyy-MM-dd`; exclusive UTC midnight kickoff upper bound. |

Filters combine with AND. When both dates are supplied, `from` must precede `to`.
For all matches on October 23, use `from=2026-10-23&to=2026-10-24`.
Unknown kickoff dates are excluded when either date bound is supplied.
Parameter names are case insensitive; repeated parameters, empty values, malformed
values and unsupported parameters (including status and competition filters) return
sanitized `400 application/problem+json` before resolving persistence services.
Unknown valid IDs and pages beyond available results return `200` with empty items.

Results sort by known kickoff first, kickoff ascending, then match ID. Matches with
unknown kickoff sort last by ID. Offset pagination is deterministic for unchanged
data; concurrent changes can shift page boundaries. `hasNextPage` uses one lookahead
row; no total count is returned.

## Examples

`GET /api/matches?page=1&pageSize=20&from=2026-10-23&to=2026-10-24`

```json
{
  "items": [
    {
      "id": "11111111-1111-1111-1111-111111111111",
      "seasonId": "22222222-2222-2222-2222-222222222222",
      "homeTeam": { "id": "33333333-3333-3333-3333-333333333333", "name": "Home FC" },
      "awayTeam": { "id": "44444444-4444-4444-4444-444444444444", "name": "Away FC" },
      "kickoffUtc": "2026-10-23T18:00:00Z",
      "status": "Finished",
      "matchday": 10,
      "homeGoals": 2,
      "awayGoals": 1
    }
  ],
  "page": 1,
  "pageSize": 20,
  "hasNextPage": false
}
```

Kickoff, matchday and each goal total may independently be `null`. Null goals mean
unknown, not zero. Goal totals carry no additional full-time, extra-time or penalty
interpretation. Status uses existing names: `Unknown`, `Scheduled`, `Live`,
`Finished`, `Postponed`, `Cancelled`, `Suspended`. The existing persistence converter
maps unrecognized stored status text to `Unknown`.

An empty result is `{"items":[],"page":1,"pageSize":20,"hasNextPage":false}`.
For `?pageSize=101`, the validation response is:

```json
{
  "title": "Invalid request.",
  "status": 400,
  "errors": { "pageSize": ["Page size must be between 1 and 100."] }
}
```

Unexpected errors use the existing sanitized `500` problem response. Request
cancellation propagates to the reader and EF asynchronous execution.

## Persistence and verification boundaries

The reader uses `AsNoTracking`, a bounded scalar projection and two required team
joins, with no season join, entity graph loading, N+1 requests or count query.
The existing season/kickoff indexes can assist season-scoped reads; team-only and
global chronological reads may scan/sort more data. No schema/index changes are
included. Large offsets remain potentially expensive despite bounded responses.

Offline tests validate request contracts, synthetic projection/filter/pagination,
Npgsql query translation without opening a connection, and HTTP behavior with
synthetic readers. They do not establish real PostgreSQL execution plans or live
database correctness. No migrations, SQL execution, PostgreSQL connections,
importer writes or security configuration changes are authorized by this milestone.

## Offline validation and reviews

Validated on 2026-10-23 with .NET 10:

| Command | Result |
| --- | --- |
| `dotnet test tests/SportStats.UnitTests/SportStats.UnitTests.csproj --no-restore --verbosity minimal` | 1,041 passed |
| `dotnet test tests/SportStats.IntegrationTests/SportStats.IntegrationTests.csproj --no-restore --filter "Category!=PostgreSQLLive" --verbosity minimal` | 292 passed, including 41 match API cases |
| `dotnet test tests/SportStats.ReadOnlyIntegration.OfflineTests/SportStats.ReadOnlyIntegration.OfflineTests.csproj --no-restore --verbosity minimal` | 65 passed |

Synthetic reader tests cover both team roles, combined filters, inclusive/exclusive
date boundaries, null kickoff ordering, ID tie-breaking, later-page offsets,
lookahead and nullable projections. HTTP tests cover validation before reader
resolution, cancellation and sanitized failures in Production and Development.
The existing unmapped-route regression now uses `/api/not-a-route` because
`/api/matches` is implemented.

Independent Backend, Database and QA reviews approved the implementation with no
blocking findings. Database review's status-conversion concern was checked through
offline query translation: the converted enum scalar is retained, with final
string formatting in the result projection. Live execution and query plans remain
unverified; the NO-GO decision is unchanged.
