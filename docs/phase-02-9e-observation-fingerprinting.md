# Phase 2.9E — Observation fingerprints and unverified evidence

This phase implements the normalized-observation content fingerprint specified in
[Phase 2.9D](phase-02-9d-reviewed-evidence-contracts.md). It does not implement trusted
capture, verified ordering, operator review, persistence authorization or a writer.
The existing policy input remains unchanged, `Approved` remains unreachable and
`IsUpdateAuthorized` remains false.

## Format and scope

Schema `SS-MATCH-OBS/1`, algorithm `SHA-256`, 64 lowercase hexadecimal digest characters.
Canonical bytes start with ASCII `SS-MATCH-OBS/1` and a zero byte. The 25 fields are in
the exact positional order documented in 2.9D: match identity tuple, season identity tuple,
query year/key/start/end, ordered home/away identity tuples, source provider/endpoint,
kickoff, matchday, canonical status, both goals, provider lastUpdated and raw status.
Each string has a four-byte unsigned big-endian UTF-8 byte length. Strict UTF-8 preserves
ordinal identity/text without normalization; invalid surrogates are rejected. Integers use
invariant decimal, dates invariant Gregorian ASCII and UTC timestamps exactly seven
fractional digits. Missing/Null/Value have distinct byte tags. Null and empty raw status
differ. Zero goals are Values. Stable status tokens do not depend on enum numeric values.

The serializer requires UTC typed timestamps; it never interprets Local/Unspecified as
UTC. Equivalent accepted provider timestamp spellings already normalize through the
registered adapter to the same typed value. Raw status is diagnostic and is not reparsed
as a second policy status parser. Observed Null/Unknown/undefined normalized status is
rejected; unsupported provider statuses normalized to Unknown cannot acquire a fingerprint.
Missing status remains encodable, without establishing score/status semantics.

RetrievedAtUtc must be a valid UTC input but is excluded from canonical bytes. Receipt IDs,
canonical IDs, capture time, adapter version, network headers and authority/configuration
state are not part of an observation fingerprint. Provider identity/season/ordered-team
assertions are included; canonical match binding requires the separate future trusted
identity boundary. No canonical identity is inferred or added to this contract.

Hashes establish content equality under this schema, not provider origin, freshness,
revision order, correctness or permission. A manually supplied hash is an inert claim.
Multiple captures can have equal hashes; hash equality is not an accepted-revision or
idempotency key. Different lastUpdated/raw status may change the fingerprint without a
revision conflict; future equivocation checks require the documented field-coverage
projection rather than comparing whole observation hashes.

## Clarifications and compatibility with 2.9D

Before implementation, the following unspecified details were identified and resolved:

- **Resource bound:** 2.9D requires bounded encoding but leaves text limits open. Canonical
  output is limited to 16 MiB, the existing HTTP client's maximum configurable response
  ceiling (its default is 4 MiB). This is a local direct-helper safety bound, not a provider
  guarantee, database limit or a promise that every 16 MiB response yields an encodable
  observation. Length is checked before allocating canonical output; nothing is truncated.
  No arbitrary provider-specific per-field limit or registry is introduced.
- **Shared validation:** the evaluator's existing private structural validator is made
  internal for reuse. Its behavior is unchanged. Fingerprint-specific presence/Unicode,
  normalized season/year and distinct-participant checks supplement it; no canonical lookup,
  identity resolution or fabricated evaluator snapshot is used.
- **Registration:** 2.9D's future registry applicability check cannot be implemented by a
  pure helper without that infrastructure. This phase validates structural source/provider
  consistency only; adapter registration and source authenticity remain unverified.
- **Canonical versus provider identity:** MatchProviderObservation has no canonical ID.
  The fingerprint includes every provider identity assertion specified by 2.9D; it does
  not invent a canonical binding or implement the future IdentityBinding contract.

These are validation/scope clarifications. For accepted inputs, the canonical wire format,
field ordering, schema/domain header and hash algorithm follow 2.9D without deviation.
Coverage, candidate, grant and command serialization domains remain future specifications.

## Verification and independent reviews

The new application namespace is `SportStats.Application.Football.MatchEvidence`:

- `MatchObservationFingerprintSerializer.Serialize(observation)` returns fresh caller-owned
  canonical bytes; `Compute(observation)` returns the immutable fingerprint. Null input throws
  ArgumentNullException; invalid input throws payload-free ArgumentException with field/reason.
  Validation/counting precedes canonical-output allocation, including trailing presence tags.
- `ObservationFingerprint` exposes fixed Schema/Algorithm/LowerHex and content equality. There
  is no public arbitrary-schema/envelope constructor or parser. The internal constructor validates
  generated lowercase hash shape; constructor visibility does not make a fingerprint trusted.
  Its ordinary `GetHashCode` supports in-memory collections only and is never the wire fingerprint.
- `UnverifiedObservationEvidence(observation)` pairs the immutable observation with its computed
  fingerprint. It accepts no caller-supplied digest, verified flag or authority label. Original
  provider metadata stays inside the reused observation contract, explicitly unverified; no
  speculative revision/ETag/receipt/operator contracts are added.

Full canonical-byte and SHA-256 golden vectors are published in
[MatchObservationFingerprintTests.cs](../tests/SportStats.UnitTests/MatchObservationFingerprintTests.cs).
Three independent fixed vectors cover Missing, nullable Null and explicit Values (including
zero goals and tick precision); expected bytes and hashes were derived outside the production
encoder. The 50 new test cases cover these vectors, every supported status token, cultures,
Unicode/byte lengths, provider/season/participant assertions, metadata differences, timestamp
precision, valid UTC equivalence, repeated calls, invalid values, size boundaries, immutable
outputs, safe exceptions and isolation from policy evidence inputs. Existing policy regression
cases still cover all canonical status pairs and unreachable approval.

| Verification | Result |
| --- | --- |
| `dotnet build SportStats.slnx --no-restore` | Passed, 0 warnings, 0 errors |
| Complete unit suite, `dotnet test tests/SportStats.UnitTests/SportStats.UnitTests.csproj --no-build --no-restore` | 794 passed, 0 failed, 0 skipped (744 baseline + 50 new cases) |
| Offline integration, `dotnet test tests/SportStats.IntegrationTests/SportStats.IntegrationTests.csproj --no-build --no-restore --filter 'Category!=PostgreSQLLive'` | 179 passed, 0 failed, 0 skipped |
| `git diff --check`, explicit new-file whitespace, Markdown local-link/fence/table checks | Passed |

Independent review findings:

| Reviewer | Finding / disposition |
| --- | --- |
| Backend | Implemented exact format and minimal inert wrapper. Shared evaluator validation changes visibility only; no DI/runtime policy integration or dependency additions. |
| QA | Independently authored fixed-vector/adversarial tests. Found trailing presence tags could exceed an otherwise bounded text count; backend fixed all-byte counting and regression tests cover exact/overflow tails. Final read-only review found no blocking issue. |
| Security | Reviewed all implementation files and policy boundary; no blocking issue. Malformed UTF-8/Unicode errors do not retain attacker text or inner exceptions. Root added explicit exception-redaction coverage for identity/source/raw status. Fingerprints remain unverified, with no clock/config/network/database/authorization dependency. |
| Database | Final review of all three files confirmed no schema compatibility blocker. Content hashes cannot replace receipt IDs, accepted revisions, operations, canonical identity bindings or concurrency versions. Future storage must permit multiple receipts with the same fingerprint. |

Only these six files changed in this phase:

- [MatchObservationFingerprintSerializer.cs](../src/SportStats.Application/Football/MatchEvidence/MatchObservationFingerprintSerializer.cs) — new.
- [ObservationFingerprint.cs](../src/SportStats.Application/Football/MatchEvidence/ObservationFingerprint.cs) — new.
- [UnverifiedObservationEvidence.cs](../src/SportStats.Application/Football/MatchEvidence/UnverifiedObservationEvidence.cs) — new.
- [MatchMetadataChangeEvaluator.cs](../src/SportStats.Application/Football/MatchMetadata/MatchMetadataChangeEvaluator.cs) — private-to-internal validation visibility only.
- [MatchObservationFingerprintTests.cs](../tests/SportStats.UnitTests/MatchObservationFingerprintTests.cs) — new.
- [This phase document](phase-02-9e-observation-fingerprinting.md) — new.

The pre-existing untracked 2.9C and 2.9D documents were read and left unchanged. No PostgreSQL
writes, migrations, provider requests, scheduled jobs, operator endpoints, frontend changes,
commits or pushes occurred. Policy approval behavior and false persistence authorization remain
unchanged; no trusted evidence or factory was implemented.

## Remaining blockers and next phase

Provider ordering/revision/score semantics and authority remain unverified. Trusted capture,
source/adapter registration, canonical baseline adoption/versioning, protected receipt/conflict/
acceptance storage, operator authentication/authorization, audit/grant/idempotency infrastructure
and transaction/phantom safeguards remain unavailable. None is solved by hashing.

Recommended next phase: a separately scoped **2.9F trusted capture and evidence-storage design**,
resolving source registration, retention/licensing, identity binding and schema/concurrency
requirements before implementation. Product correction permissions, baseline adoption/handback,
reviewer assurance and approval lifetimes still need explicit decisions. Keep automatic provider
ordering blocked unless authoritative guarantees are established; no writer or operator pathway
is implicitly authorized by this phase.
