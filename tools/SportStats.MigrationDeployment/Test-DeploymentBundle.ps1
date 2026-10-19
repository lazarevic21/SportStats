$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$prefix = Join-Path $root ('.local/deployment-test-' + [guid]::NewGuid().ToString('N'))
$inputPath = $prefix + '-input.sql'
$outputPath = $prefix + '-output.sql'
$tampered = $prefix + '-tampered.sql'
$generator = Join-Path $PSScriptRoot 'New-DeploymentBundle.ps1'
$count = 0
function Must-Fail([scriptblock]$Action) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    if (!$failed) { throw 'Expected rejection did not occur.' }
    $script:count++
}
try {
 Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ExpectedMigration.sql') -Destination $inputPath
 $hash = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash
 $args = @{ InputSql=$inputPath; OutputSql=$outputPath; ReviewedSha256=$hash; Port=5432; SystemIdentifier='123456789'; Approval='PREPARE sportstats_test 127.0.0.1:5432 sportstats_migrator 123456789' }
 Must-Fail { & $generator @args -Approval 'yes' }
 $bad = $args.Clone(); $bad.SystemIdentifier = "1'; DROP DATABASE x;--"
 Must-Fail { & $generator @bad }
 $bad = $args.Clone(); $bad.ReviewedSha256 = ('0' * 64)
 Must-Fail { & $generator @bad }
 $bad = $args.Clone(); $bad.Port = 5433
 Must-Fail { & $generator @bad }
 [IO.File]::WriteAllText($tampered, [IO.File]::ReadAllText($inputPath) + 'DROP TABLE platform.sports;')
 $bad = $args.Clone(); $bad.InputSql = $tampered; $bad.ReviewedSha256 = (Get-FileHash -LiteralPath $tampered).Hash
 Must-Fail { & $generator @bad }
 & $generator @args | Out-Null
 $bundle = [IO.File]::ReadAllText($outputPath)
 foreach ($fragment in @('\set ON_ERROR_STOP on', "current_database() <> 'sportstats_test'", "session_user <> 'sportstats_migrator'", 'pg_control_system()', "inet_server_port() IS DISTINCT FROM 5432", 'Deployment baseline history mismatch', 'Deployment final history mismatch', 'Deployment index catalog mismatch', 'SET LOCAL search_path = pg_catalog;', 'Deployment baseline index drift', 'Deployment baseline constraint drift', 'Deployment column drift', 'Deployment history schema drift', 'Deployment baseline product version mismatch', 'pg_catalog.pg_inherits', 'idx.indislive')) {
  if (!$bundle.Contains($fragment)) { throw 'Required execution guard missing.' }
  $count++
 }
 if ($bundle.IndexOf('Deployment target identity mismatch') -gt $bundle.IndexOf('LOCK TABLE') -or $bundle.IndexOf('Deployment final history mismatch') -gt $bundle.IndexOf('COMMIT;')) { throw 'Guard ordering failed.' }
 $count++
 foreach ($fragment in @('Deployment NOT NULL constraint drift', 'c.conenforced::text', 'c.conislocal::text', 'c.coninhcount::text', 'c.connoinherit::text', 'c.conparentid::text', 'cardinality(c.conkey)=1', "a.attnum>0 AND NOT a.attisdropped", "AND conname='PK___EFMigrationsHistory'", 'AND NOT condeferred')) {
  if (!$bundle.Contains($fragment)) { throw 'Required PostgreSQL 18 constraint guard missing.' }
  $count++
 }
 $verification = [IO.File]::ReadAllText((Join-Path $root 'docs/initial-migration-verify.sql'))
 foreach ($fragment in @('PostgreSQL 18 NOT NULL constraint drift', 'EXCEPT ALL', 'cardinality(k.conkey)=1', 'k.conenforced', 'k.conislocal', 'k.coninhcount', 'k.connoinherit', 'k.conparentid', 'AND a.attnotnull')) {
  if (!$verification.Contains($fragment)) { throw 'Required initial verification NOT NULL guard missing.' }
  $count++
 }
 Must-Fail { & $generator @args }
 Write-Output "PASS: $count offline deployment guard checks; no database calls."
} finally {
 foreach ($path in @($inputPath,$outputPath,$tampered)) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } }
}
