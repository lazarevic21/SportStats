# Read-only competitions API

`GET /api/competitions` reads persisted competitions across all sports. It does not fetch provider data, import records or change the database. This milestone is implemented and tested offline. Phase 2.9I remains **UNRESOLVED**; database migration and deployment remain **NO-GO**.

## Request and responses

Optional `page` defaults to 1. Optional `pageSize` defaults to 20 and must be between 1 and 100. Both accept one unsigned decimal integer value; empty, repeated, malformed, out-of-range and unsupported-offset inputs return 400. Page numbers must be positive and `(page - 1) * pageSize` must fit a signed 32-bit offset. Unknown query parameters are ignored.

Results are ordered by sport code, competition code and competition ID as the final unique tie-breaker, using database ordering semantics. Sport identity accompanies each competition; the contract assumes neither one sport nor one league. Offset pagination is deterministic for unchanged data, not a snapshot guarantee across requests during concurrent changes. Large valid offsets may be expensive.

Example request: `/api/competitions?page=1&pageSize=20`. Synthetic example 200 response:

```json
{
  "items": [
    {
      "id": "11111111-1111-1111-1111-111111111111",
      "code": "DEMO",
      "name": "Example Competition",
      "sportId": "22222222-2222-2222-2222-222222222222",
      "sportCode": "example-sport",
      "sportName": "Example Sport"
    }
  ],
  "page": 1,
  "pageSize": 20,
  "hasNextPage": false
}
```

An empty database or page beyond the available results returns 200 with an empty `items` array, the requested page/page size and `hasNextPage: false`. No total count is returned. `hasNextPage` reflects one extra row observed by that query.

Invalid pagination returns 400 `application/problem+json`, with fixed validation messages and parameter names, without reflecting supplied values. For example:

```json
{
  "title": "Invalid pagination.",
  "status": 400,
  "errors": {
    "pageSize": ["Page size must be between 1 and 100."]
  }
}
```

Unexpected failures use the existing sanitized 500 Problem Details handler. With an `Accept: application/problem+json` request, the title is `An unexpected error occurred.` and detail is `The server could not complete the request.` Private provider/configuration/exception details are not included in that response. Request cancellation propagates through the reader into EF execution; cancellation is not an empty successful result.

## Implementation and verification boundary

The Application layer owns immutable validated pagination, competition/page DTOs and `ICompetitionReader`. Infrastructure implements one `AsNoTracking` query with server-side scalar projection and a sport join. It requests at most `pageSize + 1` rows, trims the lookahead row and avoids a separate COUNT query. Scoped DI resolves persistence only after API pagination validation. No entity graph is returned to callers.

`/health` remains application liveness independent of database configuration or connectivity. Valid competition requests require usable database configuration and access; there is no production synthetic fallback. Missing or invalid configuration is handled by the existing sanitized failure path. Application connectivity does not establish process identity, HBA security, permissions or deployment readiness.

Tests use synthetic API readers and disconnected EF model/query inspection. No PostgreSQL connection or SQL execution is part of offline verification. Database-backed tests remain excluded. Real PostgreSQL execution, target schema compatibility, effective permissions and runtime query performance require a separately authorized future validation; this milestone grants no such authorization. Phase 2.9I remains UNRESOLVED; migration/deployment remain NO-GO.

No schema changes, migrations, importer writes, provider requests, caching, authentication or new package dependencies are introduced.

## Offline verification and reviews

Lead verification passed 973 unit tests and 198 offline integration tests, including existing model, configuration, startup and fixture-guard regressions. Tests ran with package restore disabled, the test-process live PostgreSQL opt-in disabled and integration filter `Category!=PostgreSQLLive`. The new tests cover request boundaries, lookahead trimming, disconnected Npgsql query translation, scoped registration, API serialization/validation, sanitized failures and cancellation. No PostgreSQL connection or SQL execution occurred. Whitespace checks passed and all 11 Phase 2.9I pinned artifacts remained unchanged.

Independent final reviews approved the implementation without blocking findings: Backend `COMPETITIONS-BACKEND-20261023-01`, Database `COMPETITIONS-DATABASE-20261023-01`, and QA `COMPETITIONS-QA-20261023-01`. An endpoint overload-selection build error and a test analyzer error were corrected before the passing integrated runs. Live PostgreSQL execution and performance remain unverified; migration/deployment remain NO-GO.
