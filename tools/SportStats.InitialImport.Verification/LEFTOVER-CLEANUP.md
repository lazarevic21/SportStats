# Inspect and remove the Phase 2.9G leftover

Run from the repository root in Windows PowerShell. This procedure reads Docker
metadata only; it never connects to PostgreSQL. Do not dump the entire inspect
response, which includes credentials in the container environment.

First inspect and validate this exact container:

```powershell
$ErrorActionPreference = 'Stop'
. ./tools/SportStats.InitialImport.Verification/DisposableDockerPreflight.ps1
$endpoint = Get-VerifiedDisposableDockerEndpoint
$id = '6f7bd17b58ed0679714ffa67896a83c20103f6092e998e686a2162b42b71121f'
$raw = & docker --host $endpoint container inspect $id
if ($LASTEXITCODE -ne 0) { throw 'Inspection failed; stop here.' }
$items = @($raw | ConvertFrom-Json -ErrorAction Stop)
if ($items.Count -ne 1 -or $items[0].Id -cne $id) {
    throw 'Full container ID mismatch; stop here.'
}
$container = $items[0]
$marker = $container.Config.Labels.'sportstats.disposable-marker'
if ($marker -isnot [string] -or $marker -cnotmatch '^[a-f0-9]{32}$' -or
    $container.Name -cnotmatch '^/sportstats-import-verification-[a-f0-9]{32}$' -or
    $container.Config.Image -cne 'postgres:17.6-bookworm' -or
    $container.HostConfig.AutoRemove -ne $true -or
    (@($container.Config.Cmd) -join '|') -cne "postgres|-c|sportstats.disposable_marker=$marker") {
    throw 'Disposable ownership metadata mismatch; stop here.'
}
[pscustomobject]@{
    Endpoint = $endpoint
    Id = $container.Id
    Name = $container.Name
    Image = $container.Config.Image
    State = $container.State.Status
    Marker = $marker
    ServerCommand = (@($container.Config.Cmd) -join ' ')
    AutoRemove = $container.HostConfig.AutoRemove
} | Format-List
```

Review that output. Only after the inspection block succeeds, run this separate
block in the same PowerShell session to remove that container. The helper checks
the full ID format and re-inspects the exact marker immediately before removal.

```powershell
Remove-OwnedDisposableContainer -Endpoint $endpoint -ContainerId $id `
    -Marker $marker -ContainerName $container.Name
```

The original `--rm` setting removes its associated anonymous volume when the
container is removed. Do not issue any separate volume deletion or prune command.
If inspection reports that the container no longer exists, stop; do not substitute
a name, abbreviated ID, or another container. The server command check above
compares metadata; it does not execute a database query.
