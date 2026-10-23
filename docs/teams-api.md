# Read-only teams API

`GET /api/teams` reads persisted **football teams**, using the existing football team model. It does not infer a current league, load provider identities, fetch crest images, call a provider or change data. Phase 2.9I remains **UNRESOLVED**; migration/deployment remain **NO-GO**.

## Contract

Optional `page` defaults to 1. Optional `pageSize` defaults to 20 and must be between 1 and 100. Validation reuses the existing competition pagination rules: page numbers are positive and `(page - 1) * pageSize` must fit a signed 32-bit offset. Each parameter accepts exactly one unsigned decimal integer value. Empty, signed, whitespace, fractional, repeated, malformed, out-of-range and overflowing values return 400 before resolving persistence. Parameter names are case-insensitive.

Only `page` and `pageSize` are supported. Unlike the older competition endpoints, this endpoint rejects unknown query parameters with a fixed `query` validation error. It never echoes an unsupported parameter's name or value. In particular, `competitionId` and `seasonId` are not silently ignored.

Rows are ordered by name ascending, then team ID as the unique tie-breaker, using database collation semantics. Duplicate names are allowed and do not merge team identities. The response contains `items`, the requested `page` and `pageSize`, and `hasNextPage`. No total count is returned. An empty dataset or a page beyond existing results returns 200 with an empty collection and `hasNextPage: false`.

Synthetic request:

```text
GET /api/teams?page=1&pageSize=20
```

Example 200 response:

```json
{
  "items": [
    {
      "id": "11111111-1111-1111-1111-111111111111",
      "name": "Example Football Club",
      "shortName": "Example FC",
      "crestUrl": null
    }
  ],
  "page": 1,
  "pageSize": 20,
  "hasNextPage": false
}
```

The DTO exposes only existing mapped fields: internal ID, name, optional short name and optional crest URL. Null and empty optional values are preserved; no provider code, sport ID or competition ID is invented. Crest URLs are returned as stored text, without verification or server-side retrieval. Consumers must handle these values as untrusted display data; the API does not certify an external image source.

Invalid requests return 400 `application/problem+json`, with title `Invalid request.`, numeric status and fixed parameter-keyed messages. For an unsupported filter:

```json
{
  "title": "Invalid request.",
  "status": 400,
  "errors": {
    "query": ["Only page and pageSize query parameters are supported."]
  }
}
```

Unexpected failures use the existing sanitized 500 Problem Details handler. With `Accept: application/problem+json`, the title is `An unexpected error occurred.` and detail is `The server could not complete the request.` Provider diagnostics, secrets and exception details are not returned. Cancellation propagates through the reader to EF; it is not converted into an empty successful page.

## Competition filtering decision

The model links teams to **seasons** through `FootballSeasonTeam`; each season belongs to a competition. It has no direct team/current-competition relationship. An all-history membership filter could be designed explicitly, but it would not establish current participation. This milestone defers competition filtering rather than choosing a season implicitly or mixing historical and current membership. A future explicitly scoped season-membership query can use the existing relationships without a schema change, subject to separate design/review.

## Implementation and verification boundary

Application owns the validated request, DTOs and `ITeamReader`. Infrastructure registers a scoped EF reader with one `AsNoTracking` query against `football.teams`. It orders, applies offset/`pageSize + 1`, and projects four scalar fields on the server. The extra row is trimmed to calculate `hasNextPage`; there is no COUNT, join, entity graph loading or per-team query.

Pagination is deterministic for unchanged data, not a snapshot across requests during concurrent updates. Large valid offsets and name sorting may be expensive; the existing model has no new name-order index. PostgreSQL execution, collation behavior, effective permissions, schema compatibility and query plans remain unverified. Row bounds do not guarantee bounded server work.

`/health` remains database-independent liveness. No database connection or SQL execution was performed. Actual team integration would require separately reviewed scope, permissions and explicit authorization.

No schema, migration, importer, security-policy, provider, credential, provisioning, backup/restore, commit or push changes are part of this milestone. Phase 2.9I remains UNRESOLVED; migration/deployment remain NO-GO. Existing access-boundary requirements remain in force.

## Offline verification and reviews

Lead verification passed **1,014 unit tests and 251 offline integration tests**. The new team tests comprise 20 unit cases and 27 API cases. Existing competition/season, model, configuration, fixture-guard, cancellation and health regressions were included. Package restore was disabled; live PostgreSQL test opt-in was disabled and integration tests excluded `PostgreSQLLive`. No database connection or SQL execution occurred.

The generated-query test initially assumed a particular provider-generated table alias. Its assertions now capture the actual alias while checking the required table, scalar projection and ordering. The final suites passed after that test-only correction.

Independent final reviews approved without blocking findings: Backend `TEAMS-BACKEND-20261023-01`, Database `TEAMS-DATABASE-20261023-01`, and QA `TEAMS-QA-20261023-01`. Actual PostgreSQL execution and operational readiness remain unverified. Migration/deployment remain NO-GO.
