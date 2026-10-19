[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('I APPROVE DISPOSABLE IMPORT COMMITS')]
    [string] $Approval
)
$ErrorActionPreference = 'Stop'
# This script owns a fresh container and anonymous PostgreSQL data volume; --rm removes both.
. (Join-Path $PSScriptRoot 'DisposableDockerPreflight.ps1')
$verificationRoot = Split-Path -Parent $PSScriptRoot
$verificationProject = Join-Path $PSScriptRoot 'SportStats.InitialImport.Verification.csproj'
$containerName = 'sportstats-import-verification-' + [Guid]::NewGuid().ToString('N')
$marker = [Guid]::NewGuid().ToString('N')
$password = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$oldPassword = $env:POSTGRES_PASSWORD
$oldAdmin = $env:SPORTSTATS_DISPOSABLE_ADMIN_CONNECTION
$oldMarker = $env:SPORTSTATS_DISPOSABLE_MARKER
$oldApproval = $env:SPORTSTATS_DISPOSABLE_APPROVAL
$created = $false
$dockerEndpoint = $null
$containerId = $null
$stage = 'docker-preflight'
try {
    $dockerEndpoint = Get-VerifiedDisposableDockerEndpoint
    # Build before allocating a server; dotnet run below cannot execute another project.
    $stage = 'verification-build'
    & dotnet build $verificationProject --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Verification build failed.' }
    $env:POSTGRES_PASSWORD = $password
    # Exact PostgreSQL patch image, loopback-only ephemeral host port, no host bind mounts or persistent named volumes (--rm removes anonymous volumes).
    $stage = 'container-creation'
    $containerOutput = & docker --host $dockerEndpoint run --detach --rm --name $containerName --label "sportstats.disposable-marker=$marker" --publish '127.0.0.1::5432' --env POSTGRES_PASSWORD --env POSTGRES_DB=sportstats_test postgres:17.6-bookworm postgres -c "sportstats.disposable_marker=$marker"
    if ($LASTEXITCODE -ne 0) { throw 'Disposable PostgreSQL creation failed.' }
    $created = $true
    $returnedId = "$containerOutput".Trim()
    if ($returnedId -notmatch '^[a-f0-9]{64}$') { throw 'Unexpected disposable container identifier.' }
    $containerId = $returnedId
    $stage = 'database-readiness'
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        & docker --host $dockerEndpoint exec $containerId pg_isready --host 127.0.0.1 --port 5432 -U postgres -d sportstats_test *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (!$ready) { throw 'Disposable PostgreSQL readiness failed.' }
    $stage = 'port-discovery'
    $binding = (& docker --host $dockerEndpoint port $containerId 5432/tcp).Trim()
    if ($LASTEXITCODE -ne 0 -or $binding -notmatch '^127\.0\.0\.1:(\d+)$') { throw 'Unexpected disposable port binding.' }
    $port = [int] $Matches[1]
    if ($port -eq 5432) { throw 'Disposable endpoint must use an ephemeral port.' }
    $env:SPORTSTATS_DISPOSABLE_ADMIN_CONNECTION = "Host=127.0.0.1;Port=$port;Database=sportstats_test;Username=postgres;Password=$password;Pooling=false;Enlist=false;Include Error Detail=false"
    $env:SPORTSTATS_DISPOSABLE_MARKER = $marker
    $env:SPORTSTATS_DISPOSABLE_APPROVAL = $Approval
    $stage = 'standalone-runner'
    & dotnet run --project $verificationProject --no-build -- --approved-disposable-commit-replay
    if ($LASTEXITCODE -ne 0) { throw 'Disposable durable verification failed.' }
}
catch {
    # Do not echo raw exceptions or environment values.
    throw "Disposable verification failed at wrapper stage '$stage'. No release gate was enabled."
}
finally {
    $env:POSTGRES_PASSWORD = $oldPassword
    $env:SPORTSTATS_DISPOSABLE_ADMIN_CONNECTION = $oldAdmin
    $env:SPORTSTATS_DISPOSABLE_MARKER = $oldMarker
    $env:SPORTSTATS_DISPOSABLE_APPROVAL = $oldApproval
    if ($created) {
        Write-Host "Disposable cleanup: checking ownership for container $containerId (name $containerName)."
        try {
            Remove-OwnedDisposableContainer -Endpoint $dockerEndpoint -ContainerId $containerId -Marker $marker -ContainerName $containerName
            Write-Host 'Disposable cleanup completed; container and its anonymous volumes removed.'
        } catch {
            throw "Disposable cleanup failed; manual inspection required for container $containerId (name $containerName). Earlier wrapper stage: '$stage'."
        }
    }
}
