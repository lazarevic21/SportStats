# Football data HTTP client

`IFootballDataClient` is a read-only transport for football-data.org v4. Its
competition, teams and matches methods accept a competition code or numeric ID
(for example `PL`, `BL1` or `2021`). Requests always use
`https://api.football-data.org/v4/competitions/{competition}` and the appropriate
subresource. Responses still require the separate normalizer before use.

## Runtime configuration

Supply `FootballData:ApiToken` through development User Secrets or a securely
injected runtime secret, such as `FootballData__ApiToken`. Never put a real token
in tracked configuration, command examples, fixtures or logs. The API project
already has a development User Secrets ID.

Optional settings:

| Key | Default | Accepted range |
| --- | --- | --- |
| `FootballData:TimeoutSeconds` | 30 | 1–300 seconds |
| `FootballData:MaxResponseBytes` | 4194304 | 1–16777216 bytes |

Configuration is validated when the provider client is resolved. Startup and
`/health` remain independent of provider credentials and connectivity.

## Request and failure behavior

The typed client uses HttpClientFactory and sends `X-Auth-Token` per request.
Redirects and cookies are disabled. Factory HTTP logging is removed for this
client and all header values are marked for redaction. The client does not log
payloads or transport exceptions.

Each call makes one attempt. Caller cancellation propagates; request deadlines
cover both headers and body reads. Authentication, authorization, missing
resources, rate limits, provider errors, transport failures, timeouts and invalid
responses produce typed failures without raw provider error content.

`Retry-After` durations and absolute dates are preserved separately. The client
does not sleep or retry. Successful bodies must contain a nonempty JSON object
within the configured byte limit; endpoint-specific semantic validation remains
the normalizer's responsibility.

There is no polling, shared rate budget, persistence or synchronization in this
increment. Consumers must arrange account-wide rate coordination before using
this client from concurrent scheduled workloads. Neither retrieval order nor a
provider `lastUpdated` timestamp establishes guaranteed freshness ordering.
