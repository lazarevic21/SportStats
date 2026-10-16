# Guarded PostgreSQL match metadata evaluation tests

Phase 2.8F adds live coverage for the existing read-only evaluation service. It does not change the
production reader, evaluator, identity resolver, persistence executor or database schema.

Tests use the existing nonparallel rollback-only PostgreSQL collection. Both
`SPORTSTATS_RUN_POSTGRES_TESTS=1` and a separately supplied `SPORTSTATS_TEST_CONNECTION_STRING` are
required. The existing fixture checks the configured and actual database (`sportstats_test`), session
and current role (`sportstats_app`), and restricted role privileges. Never supply a production
connection. No connection values or credentials belong in tracked files.

Fixture setup inserts only into the existing approved match and parent tables inside its owned
transaction. Transactions are never committed. Cleanup uses a separate bounded cancellation token,
and tests verify fixture rows are absent after rollback. Existing insert permissions and table
allowlists remain unchanged. Foreign keys stay enabled: invalid dangling rows cannot be persisted
just to test the reader. Constraint probes verify rejection; pure/offline tests retain coverage of
defensive handling of already-dangling snapshots.

Coverage includes exact match identities and persisted ordered relationships; unchanged, changed,
missing, explicit-null and zero metadata; trusted and conflicting aliases; duplicate, ambiguous and
missing mappings; conflicting season/participants; batch order; heterogeneous mapping combinations;
no tracking or writes; cancellation and database-error propagation. Existing match persistence live
tests remain in the full live category to check regression behavior.

With the two settings securely supplied by the caller, run from the repository root:

```powershell
dotnet test tests/SportStats.IntegrationTests/SportStats.IntegrationTests.csproj --filter 'FullyQualifiedName~MatchMetadataEvaluationPostgreSqlTests'
dotnet test tests/SportStats.IntegrationTests/SportStats.IntegrationTests.csproj --filter 'Category=PostgreSQLLive'
```

Without the exact opt-in, live facts are skipped. With opt-in but absent or invalid connection settings,
fixture opening fails closed. Offline verification uses `--filter 'Category!=PostgreSQLLive'`.

## Query strategy and consistency

The reader uses at most six sequential set-based queries, skipping queries with no lookup keys. It
loads mapping rows without deduplication, applies an exact ordinal provider/scope/external-ID check,
and then loads distinct canonical targets and persisted relationships with no-tracking projections.
Duplicate/ambiguous mapping evidence is therefore preserved.

Independent SQL filters on the three identity dimensions can retrieve unrequested tuple combinations
in heterogeneous batches. Exact filtering excludes those rows before target loading. The live
heterogeneous scenario measures this behavior against seeded combinations and checks that only exact
identities influence results. The six-query design is acceptable for the current bounded internal
evaluation scope; it is not a measured throughput guarantee or an unbounded public batch API.

The fixture owns a ReadCommitted transaction for rollback safety. This does not make the reader's
multiple statements a repeatable-read snapshot. Production evaluations remain best effort, can become
stale immediately, and always report unverified freshness without update authorization. No new locks
or production transactions are introduced.

During implementation, neither required live environment setting was present. Real PostgreSQL SQL
translation, constraint behavior and overfetch measurements require a configured live run; skipped
tests are not evidence of those behaviors.
