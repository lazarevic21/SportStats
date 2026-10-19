[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InputSql,
    [Parameter(Mandatory)][string]$OutputSql,
    [Parameter(Mandatory)][string]$ReviewedSha256,
    [Parameter(Mandatory)][ValidateRange(1,65535)][int]$Port,
    [Parameter(Mandatory)][string]$SystemIdentifier,
    [Parameter(Mandatory)][string]$Approval
)
$ErrorActionPreference = 'Stop'
# Offline preparation only. This tool never starts a database client or reads credentials.
if ($SystemIdentifier -cnotmatch '^[0-9]{1,20}$' -or $ReviewedSha256 -cnotmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid review evidence.' }
$requiredApproval = "PREPARE sportstats_test 127.0.0.1:$Port sportstats_migrator $SystemIdentifier"
if ($Approval -cne $requiredApproval) { throw 'Explicit bundle preparation approval does not match the selected target.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$local = [IO.Path]::GetFullPath((Join-Path $root '.local')) + [IO.Path]::DirectorySeparatorChar
$inputPath = [IO.Path]::GetFullPath($InputSql)
$outputPath = [IO.Path]::GetFullPath($OutputSql)
foreach ($path in @($inputPath, $outputPath)) {
    if (!$path.StartsWith($local, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetExtension($path) -cne '.sql') { throw 'SQL paths must be inside repository .local and end in .sql.' }
    # Refuse links/junctions anywhere below the repository to prevent path redirection.
    $cursor = [IO.Path]::GetDirectoryName($path)
    while ($cursor.Length -ge $local.TrimEnd([IO.Path]::DirectorySeparatorChar).Length) {
        if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked directories are not permitted.' }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}
if (Test-Path -LiteralPath $outputPath) { throw 'Output already exists; choose a new reviewed artifact path.' }
if ((Get-Item -LiteralPath $inputPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked inputs are not permitted.' }
$bytes = [IO.File]::ReadAllBytes($inputPath)
$hasher = [Security.Cryptography.SHA256]::Create()
try { $hash = [BitConverter]::ToString($hasher.ComputeHash($bytes)).Replace('-', '') } finally { $hasher.Dispose() }
if ($hash -cne $ReviewedSha256.ToUpperInvariant()) { throw 'Reviewed SHA256 does not match input.' }
$sql = [Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF).Replace("`r`n", "`n").Trim()
$expected = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ExpectedMigration.sql')).Replace("`r`n", "`n").Trim()
if ($sql -cne $expected) { throw 'SQL differs from the exact approved bounded migration contract.' }
$identity = @"
DO `$target`$
BEGIN
 IF current_database() <> 'sportstats_test' OR current_user <> 'sportstats_migrator'
 OR session_user <> 'sportstats_migrator' OR inet_server_addr() IS DISTINCT FROM '127.0.0.1'::inet
 OR inet_server_port() IS DISTINCT FROM $Port
 OR (SELECT system_identifier::text FROM pg_catalog.pg_control_system()) <> '$SystemIdentifier'
 OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname=current_user
   AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication)
 THEN RAISE EXCEPTION 'Deployment target identity mismatch'; END IF;
END
`$target`$;
"@
$baseline = @'
LOCK TABLE platform."__EFMigrationsHistory" IN SHARE ROW EXCLUSIVE MODE;
DO $history$
BEGIN
 IF (SELECT array_agg("MigrationId" ORDER BY "MigrationId") FROM platform."__EFMigrationsHistory")
 IS DISTINCT FROM ARRAY['20261012165516_InitialCreate']::varchar(150)[]
 THEN RAISE EXCEPTION 'Deployment baseline history mismatch'; END IF;
 IF EXISTS (SELECT 1 FROM platform."__EFMigrationsHistory" WHERE "ProductVersion" <> '10.0.12')
 THEN RAISE EXCEPTION 'Deployment baseline product version mismatch'; END IF;
END
$history$;
'@
$post = @'
DO $post$
DECLARE item record; definition text; valid boolean;
BEGIN
 FOR item IN SELECT * FROM (VALUES
 ('football','team_external_ids','UX_team_external_ids_identity'),
 ('platform','season_external_ids','UX_season_external_ids_identity'),
 ('football','match_external_ids','UX_match_external_ids_identity'),
 ('platform','competition_external_ids','UX_competition_external_ids_identity')) AS x(s,t,i)
 LOOP
  SELECT pg_catalog.pg_get_indexdef(c.oid), idx.indisvalid AND idx.indisready AND idx.indislive AND idx.indisunique AND idx.indimmediate
  INTO definition, valid FROM pg_catalog.pg_class c
  JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
  JOIN pg_catalog.pg_index idx ON idx.indexrelid=c.oid
  WHERE n.nspname=item.s AND c.relname=item.i;
  IF valid IS DISTINCT FROM true OR definition IS DISTINCT FROM
   format('CREATE UNIQUE INDEX %I ON %I.%I USING btree ("ProviderKey", "IdentityScope", "ExternalId")', item.i,item.s,item.t)
  THEN RAISE EXCEPTION 'Deployment index catalog mismatch'; END IF;
 END LOOP;
 IF (SELECT array_agg("MigrationId" ORDER BY "MigrationId") FROM platform."__EFMigrationsHistory")
 IS DISTINCT FROM ARRAY['20261012165516_InitialCreate','20261019102703_ExternalIdentityUniqueness']::varchar(150)[]
 THEN RAISE EXCEPTION 'Deployment final history mismatch'; END IF;
 IF EXISTS (SELECT 1 FROM platform."__EFMigrationsHistory" WHERE "ProductVersion" <> '10.0.12')
 THEN RAISE EXCEPTION 'Deployment final product version mismatch'; END IF;
END
$post$;
'@
$schemaGuard = @'
DO $schema_guard$
DECLARE subject regclass;
BEGIN
 FOREACH subject IN ARRAY ARRAY[
 'platform."__EFMigrationsHistory"'::regclass,
 'platform.competition_external_ids'::regclass, 'platform.season_external_ids'::regclass,
 'football.team_external_ids'::regclass, 'football.match_external_ids'::regclass]
 LOOP
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class WHERE oid=subject AND relkind='r'
    AND NOT relrowsecurity AND NOT relforcerowsecurity AND NOT relispartition
    AND relowner=(SELECT oid FROM pg_catalog.pg_roles WHERE rolname=current_user))
   OR EXISTS (SELECT 1 FROM pg_catalog.pg_trigger WHERE tgrelid=subject AND NOT tgisinternal)
   OR EXISTS (SELECT 1 FROM pg_catalog.pg_rewrite WHERE ev_class=subject)
   OR EXISTS (SELECT 1 FROM pg_catalog.pg_inherits WHERE inhrelid=subject OR inhparent=subject)
  THEN RAISE EXCEPTION 'Deployment table security catalog mismatch'; END IF;
 END LOOP;
END
$schema_guard$;
'@
# Keep target checks inside the original transaction and postchecks before its only COMMIT.
$body = $sql.Substring('START TRANSACTION;'.Length)
$baselineCatalog = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'BaselineCatalog.sql'))
$body = $body.Replace("SET LOCAL statement_timeout = '60s';", "SET LOCAL statement_timeout = '60s';`nSET LOCAL search_path = pg_catalog;`n$baseline")
$body = $body.Replace('football.team_external_ids, football.match_external_ids IN SHARE ROW EXCLUSIVE MODE;', "football.team_external_ids, football.match_external_ids IN SHARE ROW EXCLUSIVE MODE;`n$schemaGuard`n$baselineCatalog")
$body = $body.Replace('COMMIT;', "$post`nCOMMIT;")
$bundle = "\set ON_ERROR_STOP on`nSTART TRANSACTION;`nSET LOCAL lock_timeout = '5s';`nSET LOCAL statement_timeout = '60s';`nSET LOCAL search_path = pg_catalog;`n$identity`n$body`n"
$stream = [IO.File]::Open($outputPath, [IO.FileMode]::CreateNew)
try { $encoded = [Text.UTF8Encoding]::new($false).GetBytes($bundle); $stream.Write($encoded,0,$encoded.Length) } finally { $stream.Dispose() }
Write-Output 'Offline deployment bundle prepared. No database connection or execution occurred. Execution requires separate explicit approval.'
