$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DisposableDockerPreflight.ps1')
# Entirely offline: this function shadows Docker; no daemon or database is contacted.
function docker {
    $script:calls += ,@($args)
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'context' -and $args[1] -eq 'show') { return $script:context }
    if ($args[0] -eq 'context') {
        if ($script:inspectFail) { $global:LASTEXITCODE = 1; return }
        if ($script:invalidJson) { return '{' }
        return @{ Name = $script:inspectionName; Endpoints = @{ docker = @{ Host = $script:endpoint } } } | ConvertTo-Json -Depth 5 -Compress
    }
    if ($args[2] -eq 'info') {
        if ($script:daemonFail) { $global:LASTEXITCODE = 1 }
        return $script:osType
    }
    if ($args[2] -eq 'inspect') { if ($script:cleanupInspectFail) { $global:LASTEXITCODE = 1 }; return $script:label }
    if ($args[2] -eq 'rm') { if ($script:removeFail) { $global:LASTEXITCODE = 1 }; return }
    throw 'Unexpected Docker command in offline test.'
}
function Test-Case([string] $Name, [scriptblock] $Arrange, [bool] $Reject, [switch] $Cleanup) {
    $script:context = 'default'; $script:endpoint = 'npipe:////./pipe/docker_engine'
    $script:inspectionName = 'default'; $script:cleanupInspectFail = $false
    $script:inspectFail = $false; $script:invalidJson = $false; $script:daemonFail = $false
    $script:osType = 'linux'; $script:label = '{"sportstats.disposable-marker":"' + ('b' * 32) + '"}'; $script:removeFail = $false; $script:calls = @()
    $env:DOCKER_HOST = $null; $env:DOCKER_CONTEXT = $null
    & $Arrange
    $rejected = $false
    try {
        if ($Cleanup) { Remove-OwnedDisposableContainer -Endpoint $script:endpoint -ContainerId ('a' * 64) -Marker ('b' * 32) }
        else { $result = Get-VerifiedDisposableDockerEndpoint; if ($result -cne $script:endpoint) { throw 'Wrong endpoint.' } }
    } catch { $rejected = $true }
    if ($rejected -ne $Reject) { throw "FAILED: $Name" }
    if (($env:DOCKER_HOST -or $env:DOCKER_CONTEXT) -and $script:calls.Count -ne 0) { throw 'Override contacted Docker.' }
    foreach ($call in $script:calls) {
        if ($call[0] -ne 'context' -and ($call[0] -ne '--host' -or $call[1] -cne $script:endpoint)) { throw 'Endpoint not pinned.' }
        if ($Cleanup -and $Reject -and !$script:removeFail -and $call[2] -eq 'rm') { throw 'Removed unowned container.' }
        if ($Cleanup -and $call[2] -eq 'inspect' -and
            ($call.Count -ne 6 -or $call[3] -cne '--format' -or $call[4] -cne '{{json .Config.Labels}}' -or $call[5] -cne ('a' * 64))) {
            throw 'Cleanup inspection did not use the quote-free labels template and exact full ID.'
        }
        if ($Cleanup -and $call[2] -eq 'rm' -and
            ($call.Count -ne 5 -or $call[3] -cne '--force' -or $call[4] -cne ('a' * 64))) { throw 'Cleanup removal did not use the exact full ID.' }
    }
    $script:passed++
    Write-Output "PASS: $Name"
}
$oldHost = $env:DOCKER_HOST; $oldContext = $env:DOCKER_CONTEXT; $script:passed = 0
try {
    Test-Case 'local default' {} $false
    Test-Case 'local desktop' { $script:context = 'desktop-linux'; $script:inspectionName = 'desktop-linux'; $script:endpoint = 'npipe:////./pipe/dockerDesktopLinuxEngine' } $false
    Test-Case 'remote context' { $script:context = 'remote' } $true
    Test-Case 'remote endpoint' { $script:endpoint = 'tcp://example.invalid:2376' } $true
    Test-Case 'untrusted pipe' { $script:endpoint = 'npipe:////./pipe/other' } $true
    Test-Case 'host override' { $env:DOCKER_HOST = 'npipe:////./pipe/docker_engine' } $true
    Test-Case 'context override' { $env:DOCKER_CONTEXT = 'default' } $true
    Test-Case 'invalid inspection' { $script:invalidJson = $true } $true
    Test-Case 'failed inspection' { $script:inspectFail = $true } $true
    Test-Case 'null endpoint' { $script:endpoint = $null } $true
    Test-Case 'array endpoint' { $script:endpoint = @('npipe:////./pipe/docker_engine', 'tcp://example.invalid') } $true
    Test-Case 'array context name' { $script:inspectionName = @('default', 'other') } $true
    Test-Case 'daemon unavailable' { $script:daemonFail = $true } $true
    Test-Case 'Windows daemon' { $script:osType = 'windows' } $true
    Test-Case 'owned cleanup' {} $false -Cleanup
    Test-Case 'cleanup failure' { $script:removeFail = $true } $true -Cleanup
    Test-Case 'ownership mismatch' { $script:label = '{"sportstats.disposable-marker":"other"}' } $true -Cleanup
    Test-Case 'ownership case mismatch' { $script:label = '{"sportstats.disposable-marker":"' + ('B' * 32) + '"}' } $true -Cleanup
    Test-Case 'missing ownership label' { $script:label = '{}' } $true -Cleanup
    Test-Case 'null labels' { $script:label = 'null' } $true -Cleanup
    Test-Case 'invalid labels JSON' { $script:label = '{' } $true -Cleanup
    Test-Case 'array labels object' { $script:label = '[{"sportstats.disposable-marker":"' + ('b' * 32) + '"}]' } $true -Cleanup
    Test-Case 'non-string ownership label' { $script:label = '{"sportstats.disposable-marker":123}' } $true -Cleanup
    Test-Case 'array ownership label' { $script:label = '{"sportstats.disposable-marker":["' + ('b' * 32) + '"]}' } $true -Cleanup
    Test-Case 'duplicate ownership output' { $script:label = @($script:label, $script:label) } $true -Cleanup
    Test-Case 'cleanup inspection failed' { $script:cleanupInspectFail = $true } $true -Cleanup
    foreach ($badId in @('', '--all', 'abc', ('a' * 63), ('a' * 65), ('A' * 64), (('a' * 63) + 'g'))) {
        $script:calls = @(); $rejected = $false
        try { Remove-OwnedDisposableContainer -Endpoint 'npipe:////./pipe/docker_engine' -ContainerId $badId -Marker ('b' * 32) -ContainerName 'test-container' }
        catch { $rejected = $true }
        if (!$rejected -or $script:calls.Count -ne 0) { throw 'Malformed cleanup ID reached Docker.' }
        $script:passed++
        Write-Output 'PASS: malformed cleanup identifier rejected before Docker'
    }
    foreach ($badMarker in @('', '--all', ('b' * 31), ('b' * 33), ('B' * 32), (('b' * 31) + 'g'))) {
        $script:calls = @(); $rejected = $false
        try { Remove-OwnedDisposableContainer -Endpoint 'npipe:////./pipe/docker_engine' -ContainerId ('a' * 64) -Marker $badMarker }
        catch { $rejected = $true }
        if (!$rejected -or $script:calls.Count -ne 0) { throw 'Malformed cleanup marker reached Docker.' }
        $script:passed++
        Write-Output 'PASS: malformed cleanup marker rejected before Docker'
    }
    # Cross a real native executable boundary: PowerShell function mocks cannot detect
    # Windows PowerShell 5.1 stripping embedded quotes from native command arguments.
    $argvProbe = Join-Path ([IO.Path]::GetTempPath()) (([guid]::NewGuid().ToString('N')) + '.ps1')
    try {
        [IO.File]::WriteAllText($argvProbe, '$args | ForEach-Object { Write-Output $_ }')
        $fixedTemplate = '{{json .Config.Labels}}'
        $nativeArgs = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $argvProbe --format $fixedTemplate ('a' * 64))
        if ($LASTEXITCODE -ne 0 -or $nativeArgs.Count -ne 3 -or $nativeArgs[0] -cne '--format' -or
            $nativeArgs[1] -cne $fixedTemplate -or $nativeArgs[2] -cne ('a' * 64)) { throw 'Quote-free cleanup template failed native argument roundtrip.' }
        $script:passed++
        Write-Output 'PASS: quote-free cleanup template survives Windows native argument passing'
        if ($PSVersionTable.PSEdition -eq 'Desktop') {
            $oldTemplate = '{{index .Config.Labels "sportstats.disposable-marker"}}'
            $oldNativeArgs = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $argvProbe --format $oldTemplate ('a' * 64))
            if ($LASTEXITCODE -ne 0 -or $oldNativeArgs.Count -ne 3 -or
                $oldNativeArgs[1] -cne '{{index .Config.Labels sportstats.disposable-marker}}') { throw 'Historical Windows PowerShell quoting defect was not reproduced.' }
            $script:passed++
            Write-Output 'PASS: exact original template reproduces stripped label quotes on Windows PowerShell'
        }
    } finally {
        if (Test-Path -LiteralPath $argvProbe) { Remove-Item -LiteralPath $argvProbe }
    }
    Write-Output "$script:passed offline preflight tests passed."
} finally { $env:DOCKER_HOST = $oldHost; $env:DOCKER_CONTEXT = $oldContext }
