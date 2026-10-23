# Read-only competition seasons API

`GET /api/competitions/{competitionId}/seasons` reads persisted seasons for one competition across any sport. It does not request provider data or change records. Phase 2.9I remains **UNRESOLVED**; database migration and deployment remain **NO-GO**.

## Request and response contract

`competitionId` must parse as a nonempty GUID. A malformed or empty GUID returns 400, before persistence is resolved. A missing route segment is an unmatched route rather than a collection request.

Pagination follows [the competitions API](competitions-api.md): `page` defaults to 1; `pageSize` defaults to 20 with a fixed maximum of 100. Each must be supplied at most once as an unsigned decimal integer. Page and page size must be positive and `(page - 1) * pageSize` must fit a signed 32-bit offset. Empty, malformed, repeated, out-of-range and overflowing inputs return fixed validation errors without reflecting input values. Unknown query parameters are ignored.

Rows are ordered by season key ascending, then season ID, using database ordering semantics. Keys are opaque: this order does not imply chronological order, a particular year format or a football-only season convention. Dates are optional calendar dates, not timestamps.

Synthetic example request:

```text
GET /api/competitions/11111111-1111-1111-1111-111111111111/seasons?page=1&pageSize=20
```

Example 200 response:

```json
{
  "items": [
    {
      "id": "22222222-2222-2222-2222-222222222222",
      "competitionId": "11111111-1111-1111-1111-111111111111",
      "key": "example-season",
      "startDate": "2026-08-01",
      "endDate": null
    }
  ],
  "page": 1,
  "pageSize": 20,
  "hasNextPage": false
}
```

This is a filtered collection endpoint. A valid but nonexistent competition, an existing competition without seasons, and a page beyond the available rows all return 200 with `items: []` and `hasNextPage: false`, preserving the requested page and page size. Empty results make no assertion about parent existence. There is no separate existence query or 404 distinction for a valid identifier.

Invalid identifier or pagination returns 400 `application/problem+json` containing fixed messages keyed by parameter name. Unexpected failures use the existing sanitized 500 Problem Details handler, with no private configuration, provider or exception details in the response. Request cancellation propagates to the reader and EF execution; cancellation is not converted into an empty successful result.

For example, an invalid identifier returns:

```json
{
  "title": "Invalid request.",
  "status": 400,
  "errors": {
    "competitionId": ["Supply a nonempty competition identifier."]
  }
}
```

## Implementation and limitations

The Application contract owns the reader, validated request and season/page response DTOs. Pagination validation reuses the existing competition-page rules. The scoped Infrastructure reader filters by competition ID and uses one `AsNoTracking` scalar projection, deterministic ordering, offset and `pageSize + 1` limit. It trims the lookahead row to report `hasNextPage` without a COUNT or parent lookup. No entity graphs, per-row queries or sport-specific assumptions are introduced.

Ordering is deterministic for unchanged data; separate pages are not a consistent snapshot during concurrent changes. Large valid offsets may be expensive. Live PostgreSQL execution, schema compatibility, effective permissions, collation behavior and performance remain unverified and require separate authorization. This milestone introduces no schema changes, migrations, importer writes, provider requests, credentials changes, security changes or package dependencies.

`/health` remains database-independent application liveness. Actual application connectivity would not resolve the Phase 2.9I process-access finding or establish HBA, service, permission or deployment security.

## Offline verification and reviews

Lead verification passed **994 unit tests and 224 offline integration tests**, including 47 new season cases: 13 request-validation, eight reader/query and 26 synthetic API cases. Existing competitions, model, configuration, startup and fixture-guard regressions were included. Package restore was disabled, live PostgreSQL test opt-in was disabled in the test process and integration tests used `Category!=PostgreSQLLive`.

Tests cover bounded lookahead, disconnected Npgsql query translation, synthetic scalar projection including nullable dates, invalid identifiers/pagination before reader resolution, empty collection semantics, sanitized failures, cancellation and health independence. No database connection or SQL execution occurred. Whitespace checks passed and all 11 pinned Phase 2.9I artifacts remained unchanged.

Independent final reviews approved without blocking findings: Backend `SEASONS-BACKEND-20261023-01`, Database `SEASONS-DATABASE-20261023-01`, and QA `SEASONS-QA-20261023-01`. Real PostgreSQL execution and operational security readiness remain outside this milestone. Migration/deployment remain NO-GO.
