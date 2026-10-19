function Get-VerifiedDisposableDockerEndpoint {
    # Reject overrides even when they appear local; context inspection must have one source.
    if ($env:DOCKER_HOST -or $env:DOCKER_CONTEXT) {
        throw 'Unset DOCKER_HOST and DOCKER_CONTEXT before disposable verification.'
    }
    $context = & docker context show
    if ($LASTEXITCODE -ne 0 -or @($context).Count -ne 1 -or $context -cnotin @('default', 'desktop-linux')) {
        throw 'Disposable verification requires the default or desktop-linux local Docker context.'
    }
    $inspection = & docker context inspect $context
    if ($LASTEXITCODE -ne 0) { throw 'Docker context inspection failed.' }
    try { $contexts = @($inspection | ConvertFrom-Json -ErrorAction Stop) }
    catch { throw 'Docker context inspection returned invalid JSON.' }
    if ($contexts.Count -ne 1 -or $contexts[0].Name -isnot [string] -or $contexts[0].Name -cne $context) { throw 'Unexpected Docker context inspection.' }
    $endpoint = $contexts[0].Endpoints.docker.Host
    # Windows local named pipes only; TCP, SSH and Unix endpoints are intentionally rejected.
    if ($endpoint -isnot [string] -or $endpoint -cnotin @('npipe:////./pipe/docker_engine', 'npipe:////./pipe/dockerDesktopLinuxEngine')) {
        throw 'Docker endpoint is not an approved local Windows named pipe.'
    }
    $osType = & docker --host $endpoint info --format '{{.OSType}}'
    if ($LASTEXITCODE -ne 0 -or @($osType).Count -ne 1 -or $osType -cne 'linux') {
        throw 'A reachable local Linux Docker daemon is required.'
    }
    return $endpoint
}

function Remove-OwnedDisposableContainer {
    param([string] $Endpoint, [string] $ContainerId, [string] $Marker, [string] $ContainerName)
    if ($Endpoint -cnotin @('npipe:////./pipe/docker_engine', 'npipe:////./pipe/dockerDesktopLinuxEngine') -or
        $ContainerId -cnotmatch '^[a-f0-9]{64}$' -or $Marker -cnotmatch '^[a-f0-9]{32}$') {
        throw "Disposable cleanup refused invalid ownership parameters for container $ContainerName; manual inspection is required."
    }
    # Avoid embedded quotes: Windows PowerShell's native argument passing strips them
    # from Go template string literals. Read the label from JSON instead.
    $labelJson = & docker --host $Endpoint inspect --format '{{json .Config.Labels}}' $ContainerId
    if ($LASTEXITCODE -ne 0 -or $labelJson -isnot [string] -or !$labelJson.TrimStart().StartsWith('{')) {
        throw "Disposable cleanup ownership check failed for container $ContainerId; manual inspection is required."
    }
    try { $actualMarker = ($labelJson | ConvertFrom-Json -ErrorAction Stop).'sportstats.disposable-marker' }
    catch { throw "Disposable cleanup ownership check failed for container $ContainerId; manual inspection is required." }
    if ($actualMarker -isnot [string] -or $actualMarker -cne $Marker) {
        throw "Disposable cleanup ownership check failed for container $ContainerId; manual inspection is required."
    }
    # --rm removes this container's anonymous PostgreSQL volume; never prune volumes.
    & docker --host $Endpoint rm --force $ContainerId *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Disposable cleanup FAILED for container $ContainerId; manual cleanup is required."
    }
}
