# Read-only season standings API

`GET /api/seasons/{seasonId}/standings` reads persisted football standings.
Phase 2.9I remains **UNRESOLVED**; migration and deployment remain **NO-GO**.

## Contract

`seasonId` must be a nonempty GUID. The only supported query parameters are
`page` (default 1) and `pageSize` (default 20, maximum 100). Page must be positive
and its computed offset must fit Int32. Parameter names are case insensitive.
Malformed values, repeated parameters and unsupported parameters return sanitized
`400 application/problem+json` before resolving persistence services.

The endpoint selects exactly one snapshot: greatest `retrievedAtUtc`, then greatest
snapshot ID for a timestamp tie. Selection is across all providers, table types
and scopes for the season. No preferred provider or canonical TOTAL table is
defined by the model. Snapshot metadata identifies the selected table; rows from
different snapshots are never combined. An empty latest snapshot is not replaced
by an older populated snapshot.

Rows sort by `position` ascending, then team ID ascending to resolve tied ranks.
Pagination applies within the selected snapshot. One extra row determines
`hasNextPage`; no count query or total count is returned. Concurrent snapshot or
row changes may shift page contents between requests.

Missing seasons and seasons without standings both return `200` with
`snapshot: null`, empty `items` and `hasNextPage: false`. No existence query is
needed to distinguish them. An existing snapshot with no rows, or a page beyond
its rows, returns its metadata with empty `items` and `hasNextPage: false`.

## Example

`GET /api/seasons/22222222-2222-2222-2222-222222222222/standings?page=1&pageSize=20`

```json
{
  "seasonId": "22222222-2222-2222-2222-222222222222",
  "snapshot": {
    "id": "11111111-1111-1111-1111-111111111111",
    "providerKey": "example-provider",
    "retrievedAtUtc": "2026-10-23T18:00:00Z",
    "tableType": "TOTAL",
    "scope": "REGULAR_SEASON"
  },
  "items": [
    {
      "position": 1,
      "team": { "id": "33333333-3333-3333-3333-333333333333", "name": "Example FC" }
    }
  ],
  "page": 1,
  "pageSize": 20,
  "hasNextPage": false
}
```

The metadata values above are illustrative; the API preserves persisted values.
The model stores position and team identity only. Played, wins, draws, losses,
goals and points are omitted because no corresponding mapped fields exist.
These statistics are not calculated from matches or represented as fabricated
zeros. Snapshot retrieval time records when data was retrieved, not necessarily
when the provider last updated its table.

For an unavailable snapshot, the response is:

```json
{
  "seasonId": "22222222-2222-2222-2222-222222222222",
  "snapshot": null,
  "items": [],
  "page": 1,
  "pageSize": 20,
  "hasNextPage": false
}
```

For `?pageSize=101`:

```json
{
  "title": "Invalid request.",
  "status": 400,
  "errors": { "pageSize": ["Page size must be between 1 and 100."] }
}
```

Unexpected failures use the existing sanitized 500 problem response. HTTP request
cancellation propagates to the reader and EF asynchronous materialization.

## Persistence boundaries

The implementation uses no-tracking scalar projections, server-side snapshot and
row selection, and bounded row lookahead. It does not load full entity graphs or
make per-team requests. Existing indexes can narrow by season/snapshot, but newest
snapshot selection and rank ordering may still sort; large offsets can be costly.
No new indexes or schema changes are included. Real PostgreSQL execution and query
plans remain unverified by design.

No migrations, database connections, SQL execution, live harness runs, importer
writes or security configuration changes are part of this milestone. Existing
unrelated working-tree changes and disabled-by-default harness controls are preserved.

## Offline verification and reviews

Validated on 2026-10-23 with .NET 10:

| Command | Result |
| --- | --- |
| `dotnet test tests/SportStats.UnitTests/SportStats.UnitTests.csproj --no-restore --verbosity minimal` | 1,066 passed |
| `dotnet test tests/SportStats.IntegrationTests/SportStats.IntegrationTests.csproj --no-restore --filter "Category!=PostgreSQLLive" --verbosity minimal` | 327 passed, including 35 standings API cases |
| `dotnet test tests/SportStats.ReadOnlyIntegration.OfflineTests/SportStats.ReadOnlyIntegration.OfflineTests.csproj --no-restore --verbosity minimal` | 65 passed |

Reader tests evaluate the query over synthetic snapshots and rows, including
timestamp and ID ties, season isolation, row ordering, page lookahead and an empty
latest snapshot that must not fall back to older data. Offline Npgsql translation
confirms latest-snapshot selection precedes a bounded lateral left join, preserving
metadata on empty pages. Rejecting connection interceptors enforce no connections
in reader tests. API tests cover malformed routes, unknown/duplicate parameters,
sanitized failures and cancellation with synthetic readers.

Independent Backend, Database and QA reviews found no blocking issues. Live
database correctness and execution plans remain outside this offline evidence;
Phase 2.9I and migration/deployment NO-GO are unchanged.
