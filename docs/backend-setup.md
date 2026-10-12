# Milestone 2.1 — Backend project setup

This milestone supplies the backend foundation only. The solution is a single deployable modular monolith with sport-neutral layers. Football remains the first planned sport; no sport-specific models, database access, provider clients, synchronization, caching, or authentication are implemented here.

## Structure

```text
SportStats.slnx
global.json
Directory.Build.props
src/
  SportStats.Domain/
  SportStats.Application/
  SportStats.Infrastructure/
  SportStats.Api/
tests/
  SportStats.UnitTests/
  SportStats.IntegrationTests/
```

Domain contains no framework or provider concepts. Application references Domain. Infrastructure references Application. API is the composition root and registers Application and Infrastructure services. Future sport modules should respect these boundaries; this setup introduces no speculative shared sports entities.

## Restore, build, and test

Run from the repository root in PowerShell. The SDK verified for this milestone is 10.0.401, with ASP.NET Core runtime 10.0.12 on Windows x64.

```powershell
$dotnetExe = 'C:\Program Files\dotnet\dotnet.exe'
& $dotnetExe restore SportStats.slnx
& $dotnetExe build SportStats.slnx --no-restore
& $dotnetExe test SportStats.slnx --no-build
```

If `dotnet` is on PATH, it can replace the full executable path. Existing terminal sessions may need reopening after SDK installation. Package restore requires NuGet access. For a restricted environment, set `$env:DOTNET_CLI_HOME` to a writable location such as `$PWD\.local\dotnet-cli`; `.local` is ignored by Git.

## Run locally

```powershell
& $dotnetExe run --project src/SportStats.Api --launch-profile http
```

The local HTTP profile listens at `http://localhost:5080`. Request `http://localhost:5080/health`; a successful check returns HTTP 200 with `{"status":"Healthy"}`. It checks application liveness only; it does not establish database or provider readiness.

## Configuration and security

Configuration follows ASP.NET Core defaults: appsettings files, environment-specific appsettings, development User Secrets, environment variables, and command-line overrides. The `Api` section provides validated, nonsecret service configuration. Environment variables use double underscores, for example `Api__ServiceName`. Invalid required configuration fails startup.

Tracked configuration contains no credentials. Future credentials must use development User Secrets or securely injected environment variables; production should use managed secret storage. User Secrets are not an encrypted production vault. Do not copy the Phase 1 DPAPI credential into the repository. No provider credential is needed for this milestone.

API failures use sanitized Problem Details without exception details. Health responses expose no dependency details. HTTPS redirection and HSTS apply outside Development; production hosting must configure HTTPS termination appropriately. `AllowedHosts` defaults to loopback host names; deployment must override it with the approved public host names. Authentication, CORS, and provider logging policies must be added with their approved milestones before exposing protected functionality.

## Verification and approval boundary

Initial tests cover configuration boundaries, startup rejection, options resolution through DI, API health behavior, absent routes, and sanitized exception responses in Development and Production. Layer registration methods are empty extension points until future services exist. Test hosts use synthetic nonsecret settings and make no external requests.

Verification on Windows: solution restore succeeded, solution build succeeded with zero warnings/errors, and all 13 tests passed (6 unit, 7 integration). NuGet restore required approved network access. The restricted test run could not write to the Windows Event Log; the approved run outside the sandbox passed all cases. This environment limitation does not indicate an application test failure.

Work stays on the existing `phase/02-backend` branch. This milestone does not authorize commits, pushes, merges, branch switches, or the next milestone. Continue only after user approval.
