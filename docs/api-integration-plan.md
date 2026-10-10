# Phase 2 API integration plan

Status: recommendation for review only. Phase 2 has not started; this document does not authorize implementation or branch changes.

## Evidence and assumptions

The user manually executed five authenticated Premier League (`PL`) requests from Windows PowerShell with a securely stored DPAPI key. All returned HTTP 200 using the free key. Reported results establish access to competition, teams (20), matches (380), standings (`TOTAL`), and scorers (10). The competition was Premier League, with season dates 2026-08-21 to 2027-05-30. See [API validation](api-validation.md) for the endpoint evidence.

These results were reported by the user and were not reproduced in the Codex sandbox. No additional authenticated requests were made for this plan. The user reports that the key was never exposed; credential storage and exposure history were not independently audited.

Detailed nested response fields, historical data access, player statistics, and update frequency remain unverified. The scorers endpoint's success and count do not establish available player statistics. Access is demonstrated for these calls, not guaranteed for other seasons, parameters, endpoints, or future subscription conditions.

The README supplies the planned C# / ASP.NET Core, PostgreSQL / EF Core, and modular monolith stack. Provider documentation conventions are planning assumptions unless supported by the reported HTTP results. Exact quotas, authentication header requirements, pagination/filter behavior, timestamps, identifiers, error bodies, and retry behavior require confirmation before implementation relies on them. This task did not independently review provider documentation.

## Recommended backend boundaries

Use one deployable ASP.NET Core modular monolith with clear responsibilities:

| Boundary | Responsibility |
| --- | --- |
| Domain | Football concepts and invariants independent of provider JSON and HTTP. |
| Application | Query use cases, synchronization orchestration, freshness policy, and interfaces for provider access, persistence, caching, and time. |
| Infrastructure | Typed `HttpClient` provider adapter, DTO deserialization and mapping, EF Core persistence, cache storage, outbound request scheduling, and credential configuration. |
| API | Validated client inputs, stable SportStats response contracts, authorization where needed, and consistent error responses. |

Keep the provider adapter behind an application interface. Serve frontend requests from synchronized local data or a cache; never let each browser request generate a provider request. A background synchronization service should schedule approved resources through the same outbound queue and rate budget as any manual refresh operation. Start with Premier League only. User authentication and favorites should follow the repository's phase scope rather than expand this provider integration task.

## C# models and persistence

Keep three distinct model groups:

- Provider DTOs represent the observed provider contract, with explicit `System.Text.Json` property mappings. Use endpoint envelopes for competition, teams, matches, standings, and scorers, then add nested DTOs only when their fields have been inspected.
- Domain models represent SportStats concepts such as competition, season, team, match, and standing. Establish provider identifiers, relationships, and uniqueness constraints only after validating their actual semantics.
- Public API response models expose stable, deliberately selected fields and freshness metadata without leaking provider envelopes or internal storage details.

Treat undocumented or unverified nested values as optional at the provider boundary. Use nullable reference types and nullable value types where omission or null is possible; never turn missing scores or statistics into zero. Handle unknown status strings without assuming a closed provider enum. Prefer `DateOnly` for validated calendar dates and `DateTimeOffset` for validated instants. Confirm date formats, timezone behavior, numeric types, nullability, and collection semantics using sanitized fixtures before finalizing types.

Validate minimum required identifiers and relationships during mapping. Missing required data should fail or quarantine that import with a safe diagnostic, preserving the last valid snapshot. Distinguish omitted, empty, and incomplete collections so a partial response cannot erase existing data. Use transactional, idempotent upserts after confirming stable provider IDs. Do not persist speculative player statistics or historical relationships as verified facts.

## Caching and freshness

Cache by resource, competition, season, and normalized supported query parameters. Keep provider retrieval time, last successful synchronization time, and any validated provider timestamp separate. Include an explicit freshness status in SportStats responses.

Choose resource-specific TTLs and synchronization schedules only after confirming update behavior and the current account quota. Competition metadata and team lists can provisionally refresh less often than fixtures and standings; this is an engineering proposal, not measured provider behavior. No numerical TTL or real-time guarantee is approved by Phase 1.

Coalesce concurrent refreshes for the same cache key. When refresh fails, retain the last valid snapshot and serve it as stale only within an agreed maximum stale window. With no acceptable snapshot, return a controlled unavailable response and a clear empty/error state. Report stale age rather than silently presenting cached information as current. A refresh failure must not replace valid data with an empty result.

For one backend process, an in-memory cache may be sufficient initially, backed by PostgreSQL snapshots. Before multiple instances run, move shared cache coordination and outbound request budgeting to a shared mechanism; per-process limits alone cannot enforce an account-wide quota.

## Outbound rate limiting and resilience

Route every provider call through one account-wide budget, including background sync, retries, and administrative refreshes. Configure the limit from verified current provider/account terms; do not infer an exact quota from five successful requests. Apply a separate inbound limit to public SportStats endpoints to control client load.

Bound concurrency and queue length, propagate cancellation, and use timeouts. Treat HTTP 429 as a signal to pause outbound work. Honor a valid `Retry-After` value when provided, supporting both duration and HTTP-date forms; if absent or invalid, use configurable conservative backoff with jitter. Whether the provider actually returns this header remains unverified. Never retry immediately or bypass the shared budget.

Allow only bounded retries for suitable transient failures on idempotent reads, with every attempt consuming the outbound budget. Do not repeatedly retry authentication or authorization failures. Use circuit breaking for sustained provider outages, retain valid cached data, and surface availability/freshness through the API. Sanitize provider errors before returning them to clients.

## Secure API key management

Keep the API key on the backend. Never include it in frontend configuration, public responses, URLs, tracked files, fixtures, screenshots, or logs. Restrict the adapter to an approved HTTPS provider base address and add the credential only to provider-bound requests; confirm the provider's required header before configuring it. Do not forward credentials through redirects to another host.

The existing DPAPI-protected key is evidence of the manual validation workflow, not a requirement to embed or copy that encrypted file into the application. For local backend development, use .NET user secrets or a process-scoped environment variable populated through an authorized local workflow. User secrets are outside source control but are not an encrypted production vault. Never ask the user to paste the key into chat.

For deployment, use a managed secret store with access granted only to the backend runtime identity, or securely injected runtime configuration appropriate to the host. Fail startup safely when required configuration is absent, without printing the secret. Configure HTTP logging and telemetry to redact credential headers and sensitive configuration; avoid logging raw request headers or full provider bodies. Rotate and revoke keys through an approved operational process, and test redaction with synthetic values.

## Gates before Phase 2 implementation

1. Review and approve Phase 1 documentation and the intended Phase 2 scope.
2. Inspect sanitized representative nested payloads and error responses through a separately authorized workflow. Confirm minimum fields, IDs, date formats, nullability, statuses, and supported filters; do not claim this inspection already occurred.
3. Confirm current free-plan quotas, required authentication configuration, and permitted caching/storage terms from authoritative provider/account information. Set the shared rate budget and retry policy accordingly.
4. Decide whether historical data or player statistics are needed. Keep them outside guaranteed V1 scope until verified; confirm update frequency before selecting refresh schedules.
5. Agree on public API contracts, freshness and maximum stale windows, and persistence rules before implementing DTOs and migrations.

Proposed Phase 2 checks include DTO mapping tests with sanitized fixtures, unknown/missing/null field handling, idempotent import and transaction tests, cache coalescing and stale expiry tests, shared budget and 429 handling tests, and secret-redaction checks using synthetic keys. These checks are planned, not executed. No backend code, application configuration, dependency installation, or authenticated API calls are part of this documentation task.
