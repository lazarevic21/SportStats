DO $baseline_catalog$
DECLARE item record; subject regclass; actual text[]; expected text[]; identity_name text;
BEGIN
 FOR item IN SELECT * FROM (VALUES
 ('platform','competition_external_ids','CompetitionId','competitions'),
 ('platform','season_external_ids','SeasonId','seasons'),
 ('football','team_external_ids','TeamId','teams'),
 ('football','match_external_ids','MatchId','matches')) AS x(s,t,k,p)
 LOOP
  subject := pg_catalog.to_regclass(format('%I.%I',item.s,item.t));
  SELECT array_agg(value ORDER BY value) INTO actual FROM (
   SELECT a.attname || '|' || pg_catalog.format_type(a.atttypid,a.atttypmod) || '|' || a.attnotnull::text || '|' || a.attidentity::text || '|' || a.attgenerated::text || '|' || COALESCE(pg_catalog.pg_get_expr(d.adbin,d.adrelid),'') || '|' || CASE WHEN a.attcollation='pg_catalog.default'::regcollation THEN 'default' ELSE a.attcollation::text END AS value
   FROM pg_catalog.pg_attribute a LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
   WHERE a.attrelid=subject AND a.attnum>0 AND NOT a.attisdropped) q;
  SELECT array_agg(value ORDER BY value) INTO expected FROM unnest(ARRAY['Id|uuid|true||||0',item.k||'|uuid|true||||0','ProviderKey|text|true||||default','IdentityScope|text|true||||default','ExternalId|text|true||||default']) value;
  IF actual IS DISTINCT FROM expected THEN RAISE EXCEPTION 'Deployment column drift'; END IF;
  identity_name := 'IX_'||item.t||'_ProviderKey_IdentityScope_ExternalId';
  IF item.t='competition_external_ids' THEN identity_name := 'IX_competition_external_ids_ProviderKey_IdentityScope_External~'; END IF;
  SELECT array_agg(value ORDER BY value) INTO expected FROM unnest(ARRAY[
   format('PK_%s|true|true|true|true|true|btree|CREATE UNIQUE INDEX %I ON %I.%I USING btree ("Id")',item.t,'PK_'||item.t,item.s,item.t),
   format('IX_%s_%s|false|false|true|true|true|btree|CREATE INDEX %I ON %I.%I USING btree (%I)',item.t,item.k,'IX_'||item.t||'_'||item.k,item.s,item.t,item.k),
   format('%s|false|false|true|true|true|btree|CREATE INDEX %I ON %I.%I USING btree ("ProviderKey", "IdentityScope", "ExternalId")',identity_name,identity_name,item.s,item.t)]) value;
  SELECT array_agg(value ORDER BY value) INTO actual FROM (
   SELECT c.relname || '|' || i.indisunique::text || '|' || i.indisprimary::text || '|' || i.indisvalid::text || '|' || i.indisready::text || '|' || i.indislive::text || '|' || am.amname || '|' || pg_catalog.pg_get_indexdef(i.indexrelid,0,false) AS value
   FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class c ON c.oid=i.indexrelid JOIN pg_catalog.pg_am am ON am.oid=c.relam WHERE i.indrelid=subject) q;
  IF actual IS DISTINCT FROM expected THEN RAISE EXCEPTION 'Deployment baseline index drift'; END IF;
  SELECT array_agg(value ORDER BY value) INTO expected FROM unnest(ARRAY[
   format('PK_%s|true|false|false|PRIMARY KEY ("Id")|true',item.t),
   format('FK_%s_%s_%s|true|false|false|FOREIGN KEY (%I) REFERENCES %I.%I("Id") ON DELETE RESTRICT|true',item.t,item.p,item.k,item.k,item.s,item.p)]) value;
  SELECT array_agg(value ORDER BY value) INTO actual FROM (
   SELECT conname || '|' || convalidated::text || '|' || condeferrable::text || '|' || condeferred::text || '|' || pg_catalog.pg_get_constraintdef(oid,false) || '|' || conenforced::text AS value
   FROM pg_catalog.pg_constraint WHERE conrelid=subject AND contype <> 'n') q;
  IF actual IS DISTINCT FROM expected THEN RAISE EXCEPTION 'Deployment baseline constraint drift'; END IF;
 END LOOP;
 subject := 'platform."__EFMigrationsHistory"'::regclass;
 IF (SELECT count(*) FROM pg_catalog.pg_attribute WHERE attrelid=subject AND attnum>0 AND NOT attisdropped) <> 2
 OR (SELECT count(*) FROM pg_catalog.pg_attribute a WHERE attrelid=subject AND attnum>0 AND NOT attisdropped AND attnotnull
   AND attidentity='' AND attgenerated='' AND NOT atthasdef AND attcollation='pg_catalog.default'::regcollation AND
   ((attname='MigrationId' AND pg_catalog.format_type(atttypid,atttypmod)='character varying(150)') OR
    (attname='ProductVersion' AND pg_catalog.format_type(atttypid,atttypmod)='character varying(32)'))) <> 2
 OR (SELECT count(*) FROM pg_catalog.pg_constraint WHERE conrelid=subject AND contype <> 'n') <> 1
 OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint WHERE conrelid=subject AND contype='p' AND conname='PK___EFMigrationsHistory' AND convalidated AND conenforced AND NOT condeferrable AND NOT condeferred AND pg_catalog.pg_get_constraintdef(oid,false)='PRIMARY KEY ("MigrationId")')
 THEN RAISE EXCEPTION 'Deployment history schema drift'; END IF;
 IF (SELECT count(*) FROM pg_catalog.pg_index WHERE indrelid=subject) <> 1
 OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class c ON c.oid=i.indexrelid
  WHERE i.indrelid=subject AND i.indisvalid AND i.indisready AND i.indislive AND i.indisunique AND i.indisprimary AND i.indimmediate
  AND c.relname='PK___EFMigrationsHistory'
  AND pg_catalog.pg_get_indexdef(c.oid)='CREATE UNIQUE INDEX "PK___EFMigrationsHistory" ON platform."__EFMigrationsHistory" USING btree ("MigrationId")')
 THEN RAISE EXCEPTION 'Deployment history index drift'; END IF;
 -- PostgreSQL 18 materializes NOT NULL constraints. Validate every row separately:
 -- names are server generated, but column identity and enforcement metadata are exact.
 -- Earlier column comparisons already establish the complete expected nullability.
 FOR subject IN SELECT pg_catalog.to_regclass(name) FROM unnest(ARRAY[
  'platform.competition_external_ids','platform.season_external_ids',
  'football.team_external_ids','football.match_external_ids',
  'platform."__EFMigrationsHistory"']) name
 LOOP
  SELECT array_agg(value ORDER BY value) INTO expected FROM (
   SELECT a.attname || '|true|true|false|false|true|0|false|0|true' AS value
   FROM pg_catalog.pg_attribute a
   WHERE a.attrelid=subject AND a.attnum>0 AND NOT a.attisdropped AND a.attnotnull) q;
  SELECT array_agg(value ORDER BY value) INTO actual FROM (
   SELECT COALESCE((SELECT a.attname FROM pg_catalog.pg_attribute a
     WHERE a.attrelid=c.conrelid AND cardinality(c.conkey)=1 AND a.attnum=c.conkey[1]
      AND a.attnum>0 AND NOT a.attisdropped),'<invalid>') || '|' ||
    c.convalidated::text || '|' || c.conenforced::text || '|' || c.condeferrable::text || '|' ||
    c.condeferred::text || '|' || c.conislocal::text || '|' || c.coninhcount::text || '|' ||
    c.connoinherit::text || '|' || c.conparentid::text || '|' ||
    COALESCE((SELECT a.attnotnull::text FROM pg_catalog.pg_attribute a
     WHERE a.attrelid=c.conrelid AND cardinality(c.conkey)=1 AND a.attnum=c.conkey[1]
      AND a.attnum>0 AND NOT a.attisdropped),'<invalid>') AS value
   FROM pg_catalog.pg_constraint c WHERE c.conrelid=subject AND c.contype='n') q;
  IF actual IS DISTINCT FROM expected THEN RAISE EXCEPTION 'Deployment NOT NULL constraint drift'; END IF;
 END LOOP;
END
$baseline_catalog$;
