# Disposable durable importer verification

This executable is intentionally outside the solution and ordinary test discovery.
It exercises the same internal lifecycle used by public apply after its release gate.
Public `--apply` remains disabled. Rollback-only fixtures and their commit refusal are unchanged.

**Do not run this workflow until the user explicitly approves disposable database commits.**
The script's mandatory approval argument records that approval; it does not obtain it.
After approval, from the repository root:

```powershell
./tools/SportStats.InitialImport.Verification/Verify-DisposableImport.ps1 -Approval 'I APPROVE DISPOSABLE IMPORT COMMITS'
```

Requirements: local Docker Desktop with Linux containers on Windows, PowerShell,
.NET 10 SDK, and access to
`postgres:17.6-bookworm` and NuGet. The script creates a new random container with no
host bind mounts or persistent named volumes and binds an ephemeral port on loopback.
The preflight rejects any `DOCKER_HOST` or `DOCKER_CONTEXT` override. The active
context must be exactly `default` or `desktop-linux`, and its inspected endpoint
must be `npipe:////./pipe/docker_engine` or
`npipe:////./pipe/dockerDesktopLinuxEngine`. TCP (including loopback), SSH, Unix
sockets, other context names and malformed inspection output are rejected.
It checks that the allowed local daemon reports Linux, then pins that endpoint
with `--host` for every container lifecycle command. Trusted local Docker Desktop
and its named pipes are prerequisites; this does not provide outbound network isolation.
It never accepts an existing database endpoint or uses the normal test connection setting.
A random PostgreSQL server marker, exact database/user checks, absence of existing
application tables, and an independent approval token must all pass before migration
or writes. Generated credentials are passed through environment variables and are
never printed; provider diagnostics are suppressed. Database commands have bounded
timeouts, and verification has a two-minute cancellation deadline.

The runner uses `platform.__EFMigrationsHistory`, matching application and design-time
configuration. It applies existing EF migrations to the empty container, seeds football,
applies the staged audited uniqueness SQL, and checks all four valid unique indexes
and their exact three columns. It creates a restricted application role with SELECT
and INSERT only. It then verifies:

1. A lifecycle-owned transaction commits a complete import and clears its context.
2. A fresh context/physical connection replays identical input with zero creates.
3. Another fresh context rejects changed metadata, rolls back, and clears tracking.
4. Full-column snapshots of all nine import tables, including mappings and season
   membership, are identical after both replays, preserving IDs and metadata.

The script removes only the full container ID returned by its creation command,
after checking the invocation's marker label, in `finally`; `--rm` also removes
anonymous volumes. Cleanup failures terminate with a visible error and require
manual inspection. Abrupt termination or an ambiguous creation failure can leave
disposable resources behind; cleanup never falls back to deleting unrelated containers
or pruning volumes. Docker images, caches and build artifacts remain.
No production or development database is touched.

Build-only verification (does not start Docker or write to a database):

```powershell
dotnet build tools/SportStats.InitialImport.Verification/SportStats.InitialImport.Verification.csproj
```

Offline Docker preflight/cleanup checks (Docker is mocked; no container or database
is created):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/SportStats.InitialImport.Verification/Test-DisposableDockerPreflight.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/SportStats.InitialImport.Verification/Test-DisposableImportWrapper.ps1
```

See [failure diagnosis](FAILURE-DIAGNOSIS.md) for the reported failure's execution
path, diagnostic changes, validation and read-only resource inspection commands.
Readiness now checks the container TCP listener explicitly. Runner failures report
fixed stage names and error categories, and wrapper/cleanup outcomes are separate.

Correction checks: 22 offline preflight/cleanup cases, 851 unit tests and 179 offline
integration tests passed with zero failures/skips. Solution and standalone runner
builds passed with zero warnings/errors. Security and Database reviews found no
remaining blockers in these corrections.

Status: built successfully; refused unapproved invocation as expected. **Durable
execution has not been approved or run.** A passing future run certifies the fresh
migrated disposable schema, staged unique indexes, successful commit and replay;
it does not certify an existing target's data audit/index deployment, production
permissions, network-loss commit uncertainty, or enable any release gate.
