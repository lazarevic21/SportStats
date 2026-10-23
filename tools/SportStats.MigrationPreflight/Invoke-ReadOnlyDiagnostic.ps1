[CmdletBinding()]
param([switch]$ApproveReadOnlyAudit)

# This switch records separate authorization; it does not obtain authorization.

if (-not $ApproveReadOnlyAudit) {
    [Console]::Out.WriteLine('DIAGNOSTIC|REFUSED|AUTHORIZATION_REQUIRED')
    exit 1
}

# Intentionally disabled until private target selection receives separate review.
# Refuse before location/environment changes, builds, credentials or process launches.
[Console]::Out.WriteLine('DIAGNOSTIC|REFUSED|RECONFIGURATION_REQUIRED')
exit 1

# Dormant implementation: retain the original safety/cleanup checks for future review.
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Push-Location -LiteralPath $repositoryRoot
$secret = $null
$passwordPointer = [IntPtr]::Zero
$builder = $null
try {
    [Environment]::SetEnvironmentVariable(
        'SPORTSTATS_MIGRATION_PREFLIGHT_CONNECTION_STRING', $null,
        [EnvironmentVariableTarget]::Process)

    # Build before acquiring credentials. No database connection is made by build.
    dotnet build tools/SportStats.MigrationPreflight --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

    $secret = Read-Host 'Password for sportstats_migrator' -AsSecureString
    if ($secret.Length -eq 0) { throw 'Empty password' }
    $builder = New-Object System.Data.Common.DbConnectionStringBuilder
    $builder['Host'] = '127.0.0.1'
    $builder['Port'] = 5432
    $builder['Database'] = 'sportstats_test'
    $builder['Username'] = 'sportstats_migrator'
    $builder['SSL Mode'] = 'Disable'
    $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
    $builder['Password'] = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
    [Environment]::SetEnvironmentVariable(
        'SPORTSTATS_MIGRATION_PREFLIGHT_CONNECTION_STRING', $builder.ConnectionString,
        [EnvironmentVariableTarget]::Process)
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    $passwordPointer = [IntPtr]::Zero
    $builder.Clear()
    $secret.Dispose()
    $secret = $null

    dotnet .\tools\SportStats.MigrationPreflight\bin\Debug\net10.0\SportStats.MigrationPreflight.dll --host 127.0.0.1 --port 5432 --database sportstats_test --role sportstats_migrator --expect baseline --system-identifier 'DISABLED_REQUIRES_SEPARATE_REVIEW' --approve-read-only-audit
    if ($LASTEXITCODE -ne 0) { throw 'Audit failed' }
}
catch {
    # Never render exception records, connection strings or credential objects.
    Write-Error 'Diagnostic stopped. No migration was attempted; raw errors are suppressed.' -ErrorAction Continue
    exit 1
}
finally {
    [Environment]::SetEnvironmentVariable(
        'SPORTSTATS_MIGRATION_PREFLIGHT_CONNECTION_STRING', $null,
        [EnvironmentVariableTarget]::Process)
    if ($passwordPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    }
    if ($null -ne $builder) { $builder.Clear() }
    if ($null -ne $secret) { $secret.Dispose() }
    Pop-Location
}
