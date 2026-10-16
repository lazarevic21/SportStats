# Read-only match metadata evaluation

`MatchMetadataChangeEvaluator` compares immutable canonical scalar snapshots with provider observations.
It performs no database access and does not invoke the write-capable identity resolver. Existing identity
resolution and match persistence behavior are unchanged.

The caller supplies a `MatchMetadataIdentitySnapshot` from an authoritative source. This is a trusted
application boundary: never construct its mappings or canonical values from provider payloads.
`IMatchMetadataEvaluationService` connects observations to the separate
`EfMatchMetadataIdentityReader`, which loads existing PostgreSQL mappings and scalar canonical values
using `AsNoTracking`. Persisted match relationships supply the canonical season and ordered team IDs;
provider relationship assertions must resolve through their own exact existing mappings. All matching
mapping rows, including duplicates, are retained for the pure evaluator's strict checks. The service
copies the request collection before awaiting reads and evaluates the entire batch together, preserving
request order and contradictions across aliases. No ingestion workflow is introduced.

Reads are **best effort**, not a transactionally consistent snapshot. Batched mapping, match and
relationship queries can observe concurrent changes between statements under the database's normal
read behavior. There is no explicit transaction, repeatable-read guarantee or advisory lock. A result
can become stale immediately and must never be used as authority for a subsequent write. A future
write workflow would need its own identity, concurrency and freshness checks within its write boundary.
Cancellation is propagated to database reads and checked before evaluation. Database failures propagate
to the caller; they are not disguised as missing mappings or valid evaluation outcomes.

The reader issues at most six sequential set-based queries per batch: match mappings, season mappings,
team mappings, canonical match metadata, existing seasons and existing teams. Empty key sets skip their
queries. Mapping SQL filters provider, scope and external-ID dimensions using parameterized arrays;
an ordinal full-tuple check after materialization excludes cross-combinations while retaining duplicate
rows. Only lookup target IDs are deduplicated. Season/team existence lookups include both mapping
targets and persisted match relationships. Query count does not grow per observation. Heterogeneous
large batches can overfetch mapping combinations, so trusted callers should keep batches bounded.

Each observation requires exactly one exact external mapping for its match, season, home team and away
team. Missing and dangling mappings produce `MissingReference`; repeated rows produce `DuplicateMappings`,
unless they have different targets, which produces `AmbiguousMappings`. There is no fallback using names,
dates, scores or natural keys. Canonical match ID, season and ordered participants cannot change.

Metadata fields are evaluated independently. Missing preserves the canonical value; null clears nullable
fields; values propose replacements, including zero goals. Explicit null and unknown status are invalid.
The normalized `Status` is authoritative; `RawProviderStatus` is diagnostic provenance, not a second
provider-specific parsing API. Adapters must map unsupported provider tokens to `Unknown`, which the
evaluator rejects. A caller that fabricates a supported normalized status for an unsupported raw token
violates the adapter contract; the provider-neutral evaluator does not reparse raw provider strings.
Per-field changes retain typed before/after values and the observation presence.

`EvaluateBatch` retains input order and allows repeated compatible observations. Different aliases for
one canonical match are compared together. Incompatible explicit proposals produce conflicts, including
one proposal equal to the baseline. Missing fields impose no proposal. Rejected results expose no changes.

Every result has `FreshnessUnverified`, regardless of retrieval time, provider last-update time, status,
scores or input order. `ChangesDetected` reports differences only; `IsUpdateAuthorized` is always false.
There is no verified ordering policy, metadata update, scheduled job or persistence side effect.
