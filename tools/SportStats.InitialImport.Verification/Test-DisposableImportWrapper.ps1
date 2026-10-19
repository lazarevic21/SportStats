$ErrorActionPreference = 'Stop'
# All external commands are shadowed. This script never contacts Docker or a database.
$global:SportStatsWrapperTest_wrapper = Join-Path $PSScriptRoot 'Verify-DisposableImport.ps1'
$global:SportStatsWrapperTest_endpoint = 'npipe:////./pipe/docker_engine'
$global:SportStatsWrapperTest_containerId = 'a' * 64
function docker {
    $global:SportStatsWrapperTest_dockerCalls += ,@($args)
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'context' -and $args[1] -eq 'show') { return 'default' }
    if ($args[0] -eq 'context' -and $args[1] -eq 'inspect') {
        return '{"Name":"default","Endpoints":{"docker":{"Host":"npipe:////./pipe/docker_engine"}}}'
    }
    if ($args[0] -ne '--host' -or $args[1] -cne $global:SportStatsWrapperTest_endpoint) { throw 'Unpinned Docker command.' }
    switch ($args[2]) {
        'info' { return 'linux' }
        'run' {
            $global:SportStatsWrapperTest_generatedPassword = $env:POSTGRES_PASSWORD
            $labelIndex = [Array]::IndexOf($args, '--label')
            $global:SportStatsWrapperTest_marker = $args[$labelIndex + 1].Substring('sportstats.disposable-marker='.Length)
            return $global:SportStatsWrapperTest_containerId
        }
        'exec' {
            if ($args[3] -cne $global:SportStatsWrapperTest_containerId -or $args[4] -cne 'pg_isready') { throw 'Unexpected readiness target.' }
            $hostIndex = [Array]::IndexOf($args, '-h')
            if ($hostIndex -lt 0) { $hostIndex = [Array]::IndexOf($args, '--host', 3) }
            $portIndex = [Array]::IndexOf($args, '-p')
            if ($portIndex -lt 0) { $portIndex = [Array]::IndexOf($args, '--port') }
            if ($hostIndex -lt 0 -or $args[$hostIndex + 1] -cne '127.0.0.1' -or
                $portIndex -lt 0 -or $args[$portIndex + 1] -cne '5432') { throw 'Readiness must check the final TCP listener.' }
            return
        }
        'port' { return '127.0.0.1:15432' }
        'inspect' {
            if ($args.Count -ne 6 -or $args[4] -cne '{{json .Config.Labels}}' -or $args[5] -cne $global:SportStatsWrapperTest_containerId) { throw 'Unsafe ownership inspection.' }
            return '{"sportstats.disposable-marker":"' + $global:SportStatsWrapperTest_marker + '"}'
        }
        'rm' {
            if ($args.Count -ne 5 -or $args[3] -cne '--force' -or $args[4] -cne $global:SportStatsWrapperTest_containerId) { throw 'Unsafe cleanup target.' }
            $global:SportStatsWrapperTest_removed = $true
            if ($global:SportStatsWrapperTest_scenario -eq 'cleanup-failure') { $global:LASTEXITCODE = 1 }
            return
        }
    }
    throw 'Unexpected Docker command in offline wrapper test.'
}
function dotnet {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'build') {
        if ($global:SportStatsWrapperTest_scenario -eq 'build-failure') { $global:LASTEXITCODE = 1 }
        return
    }
    if ($args[0] -ne 'run' -or $args -cnotcontains '--approved-disposable-commit-replay') { throw 'Unexpected dotnet command.' }
    $global:SportStatsWrapperTest_runnerCalled = $true
    if ($env:SPORTSTATS_DISPOSABLE_MARKER -cne $global:SportStatsWrapperTest_marker -or
        $env:SPORTSTATS_DISPOSABLE_APPROVAL -cne 'I APPROVE DISPOSABLE IMPORT COMMITS' -or
        $env:SPORTSTATS_DISPOSABLE_ADMIN_CONNECTION -notlike '*Host=127.0.0.1;Port=15432;Database=sportstats_test;*') { throw 'Runner isolation configuration missing.' }
    $global:SportStatsWrapperTest_generatedConnection = $env:SPORTSTATS_DISPOSABLE_ADMIN_CONNECTION
    if ($global:SportStatsWrapperTest_scenario -eq 'runner-failure') { $global:LASTEXITCODE = 1 }
}
$names = @('DOCKER_HOST', 'DOCKER_CONTEXT', 'POSTGRES_PASSWORD', 'SPORTSTATS_DISPOSABLE_ADMIN_CONNECTION', 'SPORTSTATS_DISPOSABLE_MARKER', 'SPORTSTATS_DISPOSABLE_APPROVAL')
$original = @{}
foreach ($name in $names) { $original[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $env:DOCKER_HOST = $null; $env:DOCKER_CONTEXT = $null
    foreach ($global:SportStatsWrapperTest_scenario in @('success', 'runner-failure', 'build-failure', 'cleanup-failure')) {
        $global:SportStatsWrapperTest_dockerCalls = @(); $global:SportStatsWrapperTest_removed = $false; $global:SportStatsWrapperTest_runnerCalled = $false
        $global:SportStatsWrapperTest_generatedPassword = $null; $global:SportStatsWrapperTest_generatedConnection = $null
        foreach ($name in $names[2..5]) { [Environment]::SetEnvironmentVariable($name, "offline-sentinel-$name") }
        $captured = New-Object System.Collections.Generic.List[string]
        $failure = $null
        try {
            & $global:SportStatsWrapperTest_wrapper -Approval 'I APPROVE DISPOSABLE IMPORT COMMITS' *>&1 | ForEach-Object { $captured.Add($_.ToString()) }
        } catch { $failure = $_.Exception.Message; $captured.Add($failure) }
        if (($null -ne $failure) -ne ($global:SportStatsWrapperTest_scenario -ne 'success')) { throw "Unexpected outcome: $global:SportStatsWrapperTest_scenario ($failure)" }
        $expectsContainer = $global:SportStatsWrapperTest_scenario -ne 'build-failure'
        if ($global:SportStatsWrapperTest_removed -ne $expectsContainer -or $global:SportStatsWrapperTest_runnerCalled -ne $expectsContainer) { throw "Wrong lifecycle: $global:SportStatsWrapperTest_scenario" }
        foreach ($name in $names[2..5]) {
            if ([Environment]::GetEnvironmentVariable($name) -cne "offline-sentinel-$name") { throw "Environment not restored: $name" }
        }
        $output = $captured -join "`n"
        foreach ($secret in @($global:SportStatsWrapperTest_generatedPassword, $global:SportStatsWrapperTest_generatedConnection, 'offline-sentinel-POSTGRES_PASSWORD', 'offline-sentinel-SPORTSTATS_DISPOSABLE_ADMIN_CONNECTION')) {
            if ($secret -and $output.Contains($secret)) { throw "Sensitive configuration leaked: $global:SportStatsWrapperTest_scenario" }
        }
        if ($failure -and $output -notmatch '(?i)stage') { throw "Missing failure stage: $global:SportStatsWrapperTest_scenario" }
        if ($global:SportStatsWrapperTest_scenario -eq 'runner-failure' -and $output -notmatch 'standalone-runner') { throw 'Runner failure lost its stage.' }
        if ($global:SportStatsWrapperTest_scenario -eq 'build-failure' -and $output -notmatch 'verification-build') { throw 'Build failure lost its stage.' }
        if ($global:SportStatsWrapperTest_scenario -eq 'cleanup-failure' -and $output -notmatch 'cleanup failed') { throw 'Cleanup failure was hidden.' }
        Write-Output "PASS: offline wrapper $global:SportStatsWrapperTest_scenario"
    }
    Write-Output '4 offline disposable wrapper tests passed.'
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $original[$name]) }
    Get-Variable -Scope Global -Name 'SportStatsWrapperTest_*' | Remove-Variable -Scope Global
}
