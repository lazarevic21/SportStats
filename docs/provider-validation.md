# Developer provider contract validation

This console tool is separate from the API and its startup. It reads provider
responses in memory, emits sanitized aggregate summaries, and never registers a
database context or persists raw responses. Normal build and test commands do
not make provider requests.

## Explicit live execution

Provision `FootballData__ApiToken` securely in the current process environment.
Do not paste the token into command arguments or tracked files. The tool does
not print or save it.

```powershell
dotnet run --project tools/SportStats.ProviderValidation -- --live
```

Omitting `--live` makes no requests. `PL` is the default competition. Override
the identifier with `--competition BL1`, for example. The existing observation
normalizer supports PL only; another competition is reported as unsupported for
observation normalization, rather than being assigned PL identities.

The tool derives the query year from the competition response's current season.
An explicit `--year 2026` supplies a normalization assertion; it does not request
a historical season from the provider.

```powershell
dotnet run --project tools/SportStats.ProviderValidation -- --live --competition PL --compare
```

Comparison requires a second manual confirmation in the same process. Responses
are held only in memory. EOF or declining confirmation makes no second request
round. There is no polling or automatic retry.

## Request budget and output

Each observation makes at most three sequential GET requests: competition,
teams, and matches. Requests are spaced at least seven seconds apart; use
`--spacing-seconds` to increase spacing up to 300 seconds. A failure or 429 stops
the round. Retry-After is reported without automatically retrying. This pacing
does not coordinate other applications or processes sharing the account: run
only when the account's verified budget permits the requests.

Output contains aggregate field presence, status, score and identity diagnostics,
plus safely available HTTP metadata. Unknown raw status strings and raw provider
errors are not printed. No API token, authentication headers or payload captures
are written. Cancel with Ctrl+C.

One response establishes observed behavior only. A manually compared pair can
show changes, equal timestamps or regressions for that pair; it cannot prove
that `lastUpdated` is monotonic, complete or authoritative. Local retrieval time
is separate from provider timestamps and is not a provider revision.

## Offline verification

The unit suite uses fake responses and controlled delays. No credentials or
provider access are required. Live execution is a separate developer action and
must be reported separately from offline test results.
