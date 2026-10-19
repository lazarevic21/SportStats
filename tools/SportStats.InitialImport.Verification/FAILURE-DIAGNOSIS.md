# Phase 2.9G failure diagnosis

The original runner message is emitted only by Program.cs's catch. The wrapper
then observes exit code 1 and throws `Disposable durable verification failed.`
at its original line 49, before entering its cleanup `finally` block.
Therefore Docker preflight, build, container creation, readiness polling and port
discovery succeeded. The failure was inside the standalone runner. Its original
catch discarded the exception and stage, so the exact database substage and
historical root cause cannot be recovered from those two messages. Database
writes may already have occurred in that disposable container.

Runner order: approval/configuration, admin connection, database isolation guard,
migration, sport seed, uniqueness SQL read/apply, index verification, restricted
role creation, initial import, committed snapshot, identical replay and snapshot,
conflict replay and preservation snapshot. Fixed stage identifiers now identify
these operations. Error categories are fixed strings; exception messages, stacks,
SQL, connection settings and provider data are never printed.

A confirmed readiness mismatch was corrected: `pg_isready` previously checked
the container's default Unix socket, although the runner uses TCP. It now checks
`127.0.0.1:5432` explicitly. Socket readiness could pass against a bootstrap server
before the final TCP server becomes available. This is a possible explanation,
not proof of the historical failure. No migration/import defect was established.

The preceding cleanup quoting correction reads only labels through the quote-free
`{{json .Config.Labels}}` template. It runs after the standalone runner and cannot
produce its generic failure message. Ownership checks, exact full-ID deletion,
local named-pipe restrictions, random marker, loopback ephemeral port and database
identity guards remain intact. Public importer apply remains disabled.

## Read-only leftover inspection

A local read-only check on 2026-10-19 found no containers matching the disposable
name prefix. This does not certify the absence of orphaned volumes from older
interrupted runs. No resources were deleted. For a subsequent inspection:

```powershell
. ./tools/SportStats.InitialImport.Verification/DisposableDockerPreflight.ps1
$endpoint = Get-VerifiedDisposableDockerEndpoint
docker --host $endpoint ps --all --no-trunc --filter 'name=sportstats-import-verification-' --format '{{.ID}}|{{.Names}}|{{.Image}}|{{.Status}}'
```

For each returned full ID, inspect only selected metadata (substitute the exact
64-character ID; never dump full inspect output or environment variables):

```powershell
$id = '<exact-full-container-ID>'
if ($id -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid full ID.' }
docker --host $endpoint inspect --format '{{.Id}}|{{.Name}}|{{.Config.Image}}|{{.State.Status}}|{{.HostConfig.AutoRemove}}' $id
docker --host $endpoint inspect --format '{{json .Config.Labels}}' $id
docker --host $endpoint inspect --format '{{json .Mounts}}' $id
```

Check the expected name, image, ownership marker and anonymous volume association.
These commands do not authorize deletion. Do not prune volumes or infer volume
ownership solely from a name. The original cleanup procedure is historical and
targets the earlier container; do not reuse its ID for another run.

## Validation and next step

Solution and standalone builds: zero warnings/errors. Unit tests: 851 passed.
Offline integration tests: 179 passed (live PostgreSQL category excluded and live
opt-in unset). Offline preflight/cleanup: 41 passed. Wrapper regressions mock all
Docker and dotnet execution; they do not allocate resources or run the executable.
Durable verification was not rerun, and no database writes were performed.

A fresh durable run requires new explicit approval. If approved later, retain the
new stage/category and cleanup result. A passing run would verify the corrected
path; a failure would identify the operation requiring further investigation.
