-- Run in the same psql --single-transaction session as preflight and InitialCreate.sql.
-- Catalog-only assertions; no role/grant changes or auxiliary database objects.
SET LOCAL search_path = pg_catalog;
DO $verify$
BEGIN
    IF (SELECT count(*) FROM platform."__EFMigrationsHistory") <> 1
       OR NOT EXISTS (SELECT 1 FROM platform."__EFMigrationsHistory"
                      WHERE "MigrationId" = '20261012165516_InitialCreate' AND "ProductVersion" = '10.0.12')
    THEN RAISE EXCEPTION 'Migration history mismatch'; END IF;

    IF EXISTS (
        WITH expected(schema_name,table_name,column_name,type_name,is_nullable) AS (VALUES
('platform','__EFMigrationsHistory','MigrationId','character varying(150)',false),
('platform','__EFMigrationsHistory','ProductVersion','character varying(32)',false),
('platform','sports','Id','uuid',false),
('platform','sports','Code','text',false),
('platform','sports','Name','text',false),
('football','teams','Id','uuid',false),
('football','teams','Name','text',false),
('football','teams','ShortName','text',true),
('football','teams','CrestUrl','text',true),
('platform','competitions','Id','uuid',false),
('platform','competitions','SportId','uuid',false),
('platform','competitions','Code','text',false),
('platform','competitions','Name','text',false),
('football','team_external_ids','Id','uuid',false),
('football','team_external_ids','ProviderKey','text',false),
('football','team_external_ids','IdentityScope','text',false),
('football','team_external_ids','ExternalId','text',false),
('football','team_external_ids','TeamId','uuid',false),
('platform','competition_external_ids','Id','uuid',false),
('platform','competition_external_ids','ProviderKey','text',false),
('platform','competition_external_ids','IdentityScope','text',false),
('platform','competition_external_ids','ExternalId','text',false),
('platform','competition_external_ids','CompetitionId','uuid',false),
('platform','seasons','Id','uuid',false),
('platform','seasons','CompetitionId','uuid',false),
('platform','seasons','Key','text',false),
('platform','seasons','StartDate','date',true),
('platform','seasons','EndDate','date',true),
('platform','season_external_ids','Id','uuid',false),
('platform','season_external_ids','ProviderKey','text',false),
('platform','season_external_ids','IdentityScope','text',false),
('platform','season_external_ids','ExternalId','text',false),
('platform','season_external_ids','SeasonId','uuid',false),
('football','season_teams','SeasonId','uuid',false),
('football','season_teams','TeamId','uuid',false),
('football','standing_snapshots','Id','uuid',false),
('football','standing_snapshots','SeasonId','uuid',false),
('football','standing_snapshots','ProviderKey','text',false),
('football','standing_snapshots','RetrievedAtUtc','timestamp with time zone',false),
('football','standing_snapshots','TableType','text',false),
('football','standing_snapshots','Scope','text',false),
('football','matches','Id','uuid',false),
('football','matches','SeasonId','uuid',false),
('football','matches','HomeTeamId','uuid',false),
('football','matches','AwayTeamId','uuid',false),
('football','matches','KickoffUtc','timestamp with time zone',true),
('football','matches','Status','text',false),
('football','matches','Matchday','integer',true),
('football','matches','HomeGoals','integer',true),
('football','matches','AwayGoals','integer',true),
('football','standing_rows','SnapshotId','uuid',false),
('football','standing_rows','TeamId','uuid',false),
('football','standing_rows','SeasonId','uuid',false),
('football','standing_rows','Position','integer',false),
('football','match_external_ids','Id','uuid',false),
('football','match_external_ids','ProviderKey','text',false),
('football','match_external_ids','IdentityScope','text',false),
('football','match_external_ids','ExternalId','text',false),
('football','match_external_ids','MatchId','uuid',false)
        ), actual AS (
            SELECT n.nspname::text, c.relname::text, a.attname::text,
                   format_type(a.atttypid,a.atttypmod), NOT a.attnotnull
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
            WHERE n.nspname IN ('platform','football') AND c.relkind='r'
        )
        (SELECT * FROM expected EXCEPT SELECT * FROM actual)
        UNION ALL (SELECT * FROM actual EXCEPT SELECT * FROM expected)
    ) THEN RAISE EXCEPTION 'Table columns, UUID keys, date/timestamp mappings or nullability differ'; END IF;

    -- PostgreSQL 18 NOT NULL rows must exactly match the nullability established
    -- above. EXCEPT ALL preserves duplicate rows; generated constraint names vary.
    IF EXISTS (
        WITH expected AS (
            SELECT c.oid AS relation_id,a.attnum AS column_number,
                   true AS validated,true AS enforced,false AS deferrable,false AS deferred,
                   true AS is_local,0::smallint AS inherited,false AS no_inherit,0::oid AS parent_id
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
            WHERE n.nspname IN ('platform','football') AND c.relkind='r' AND a.attnotnull
        ), actual AS (
            SELECT c.oid,a.attnum,k.convalidated,k.conenforced,k.condeferrable,k.condeferred,
                   k.conislocal,k.coninhcount,k.connoinherit,k.conparentid
            FROM pg_constraint k JOIN pg_class c ON c.oid=k.conrelid
            JOIN pg_namespace n ON n.oid=c.relnamespace
            LEFT JOIN pg_attribute a ON a.attrelid=c.oid AND cardinality(k.conkey)=1
                AND a.attnum=k.conkey[1] AND a.attnum>0 AND NOT a.attisdropped AND a.attnotnull
            WHERE n.nspname IN ('platform','football') AND k.contype='n'
        )
        (SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
        UNION ALL (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected)
    ) THEN RAISE EXCEPTION 'PostgreSQL 18 NOT NULL constraint drift'; END IF;

    IF EXISTS (
        SELECT 1 FROM pg_constraint k JOIN pg_class c ON c.oid=k.conrelid
        JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname IN ('platform','football')
          AND (NOT k.convalidated OR NOT k.conenforced OR k.condeferrable OR k.condeferred
               OR NOT k.conislocal OR k.coninhcount<>0 OR k.conparentid<>0)
    ) THEN RAISE EXCEPTION 'Constraint enforcement or inheritance drift'; END IF;

    IF EXISTS (
        WITH expected(schema_name,table_name,constraint_name,kind,definition) AS (VALUES
('platform','__EFMigrationsHistory','PK___EFMigrationsHistory','p','PRIMARYKEYMigrationId'),
('platform','sports','PK_sports','p','PRIMARYKEYId'),
('football','teams','PK_teams','p','PRIMARYKEYId'),
('platform','competitions','PK_competitions','p','PRIMARYKEYId'),
('platform','competitions','FK_competitions_sports_SportId','f','FOREIGNKEYSportIdREFERENCESplatform.sportsIdONDELETERESTRICT'),
('football','team_external_ids','PK_team_external_ids','p','PRIMARYKEYId'),
('football','team_external_ids','FK_team_external_ids_teams_TeamId','f','FOREIGNKEYTeamIdREFERENCESfootball.teamsIdONDELETERESTRICT'),
('platform','competition_external_ids','PK_competition_external_ids','p','PRIMARYKEYId'),
('platform','competition_external_ids','FK_competition_external_ids_competitions_CompetitionId','f','FOREIGNKEYCompetitionIdREFERENCESplatform.competitionsIdONDELETERESTRICT'),
('platform','seasons','PK_seasons','p','PRIMARYKEYId'),
('platform','seasons','FK_seasons_competitions_CompetitionId','f','FOREIGNKEYCompetitionIdREFERENCESplatform.competitionsIdONDELETERESTRICT'),
('platform','season_external_ids','PK_season_external_ids','p','PRIMARYKEYId'),
('platform','season_external_ids','FK_season_external_ids_seasons_SeasonId','f','FOREIGNKEYSeasonIdREFERENCESplatform.seasonsIdONDELETERESTRICT'),
('football','season_teams','PK_season_teams','p','PRIMARYKEYSeasonId,TeamId'),
('football','season_teams','FK_season_teams_seasons_SeasonId','f','FOREIGNKEYSeasonIdREFERENCESplatform.seasonsIdONDELETERESTRICT'),
('football','season_teams','FK_season_teams_teams_TeamId','f','FOREIGNKEYTeamIdREFERENCESfootball.teamsIdONDELETERESTRICT'),
('football','standing_snapshots','PK_standing_snapshots','p','PRIMARYKEYId'),
('football','standing_snapshots','AK_standing_snapshots_Id_SeasonId','u','UNIQUEId,SeasonId'),
('football','standing_snapshots','FK_standing_snapshots_seasons_SeasonId','f','FOREIGNKEYSeasonIdREFERENCESplatform.seasonsIdONDELETERESTRICT'),
('football','matches','PK_matches','p','PRIMARYKEYId'),
('football','matches','CK_matches_away_goals','c','CHECKAwayGoalsISNULLORAwayGoals>=0'),
('football','matches','CK_matches_distinct_teams','c','CHECKHomeTeamId<>AwayTeamId'),
('football','matches','CK_matches_home_goals','c','CHECKHomeGoalsISNULLORHomeGoals>=0'),
('football','matches','FK_matches_season_teams_SeasonId_AwayTeamId','f','FOREIGNKEYSeasonId,AwayTeamIdREFERENCESfootball.season_teamsSeasonId,TeamIdONDELETERESTRICT'),
('football','matches','FK_matches_season_teams_SeasonId_HomeTeamId','f','FOREIGNKEYSeasonId,HomeTeamIdREFERENCESfootball.season_teamsSeasonId,TeamIdONDELETERESTRICT'),
('football','matches','FK_matches_seasons_SeasonId','f','FOREIGNKEYSeasonIdREFERENCESplatform.seasonsIdONDELETERESTRICT'),
('football','matches','FK_matches_teams_AwayTeamId','f','FOREIGNKEYAwayTeamIdREFERENCESfootball.teamsIdONDELETERESTRICT'),
('football','matches','FK_matches_teams_HomeTeamId','f','FOREIGNKEYHomeTeamIdREFERENCESfootball.teamsIdONDELETERESTRICT'),
('football','standing_rows','PK_standing_rows','p','PRIMARYKEYSnapshotId,TeamId'),
('football','standing_rows','FK_standing_rows_season_teams_SeasonId_TeamId','f','FOREIGNKEYSeasonId,TeamIdREFERENCESfootball.season_teamsSeasonId,TeamIdONDELETERESTRICT'),
('football','standing_rows','FK_standing_rows_standing_snapshots_SnapshotId_SeasonId','f','FOREIGNKEYSnapshotId,SeasonIdREFERENCESfootball.standing_snapshotsId,SeasonIdONDELETECASCADE'),
('football','standing_rows','FK_standing_rows_teams_TeamId','f','FOREIGNKEYTeamIdREFERENCESfootball.teamsIdONDELETERESTRICT'),
('football','match_external_ids','PK_match_external_ids','p','PRIMARYKEYId'),
('football','match_external_ids','FK_match_external_ids_matches_MatchId','f','FOREIGNKEYMatchIdREFERENCESfootball.matchesIdONDELETERESTRICT')
        ), actual AS (
            SELECT n.nspname::text,c.relname::text,k.conname::text,k.contype::text,
                   regexp_replace(pg_get_constraintdef(k.oid),'[[:space:]"()]','','g')
            FROM pg_constraint k JOIN pg_class c ON c.oid=k.conrelid
            JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname IN ('platform','football') AND k.contype <> 'n'
        )
        (SELECT * FROM expected EXCEPT SELECT * FROM actual)
        UNION ALL (SELECT * FROM actual EXCEPT SELECT * FROM expected)
    ) THEN RAISE EXCEPTION 'Primary/composite keys, foreign keys, delete behavior or checks differ'; END IF;

    IF EXISTS (
        WITH expected(schema_name,table_name,index_name,is_unique) AS (VALUES
('platform','__EFMigrationsHistory','PK___EFMigrationsHistory',true),
('platform','sports','PK_sports',true),
('football','teams','PK_teams',true),
('platform','competitions','PK_competitions',true),
('football','team_external_ids','PK_team_external_ids',true),
('platform','competition_external_ids','PK_competition_external_ids',true),
('platform','seasons','PK_seasons',true),
('platform','season_external_ids','PK_season_external_ids',true),
('football','season_teams','PK_season_teams',true),
('football','standing_snapshots','PK_standing_snapshots',true),
('football','standing_snapshots','AK_standing_snapshots_Id_SeasonId',true),
('football','matches','PK_matches',true),
('football','standing_rows','PK_standing_rows',true),
('football','match_external_ids','PK_match_external_ids',true),
('platform','competition_external_ids','IX_competition_external_ids_CompetitionId',false),
('platform','competition_external_ids','IX_competition_external_ids_ProviderKey_IdentityScope_External~',false),
('platform','competitions','IX_competitions_SportId_Code',true),
('football','match_external_ids','IX_match_external_ids_MatchId',false),
('football','match_external_ids','IX_match_external_ids_ProviderKey_IdentityScope_ExternalId',false),
('football','matches','IX_matches_AwayTeamId',false),
('football','matches','IX_matches_HomeTeamId',false),
('football','matches','IX_matches_SeasonId_AwayTeamId',false),
('football','matches','IX_matches_SeasonId_HomeTeamId',false),
('football','matches','IX_matches_SeasonId_KickoffUtc',false),
('football','matches','IX_matches_SeasonId_Status_KickoffUtc',false),
('platform','season_external_ids','IX_season_external_ids_ProviderKey_IdentityScope_ExternalId',false),
('platform','season_external_ids','IX_season_external_ids_SeasonId',false),
('football','season_teams','IX_season_teams_TeamId_SeasonId',false),
('platform','seasons','IX_seasons_CompetitionId_Key',true),
('platform','sports','IX_sports_Code',true),
('football','standing_rows','IX_standing_rows_SeasonId_TeamId',false),
('football','standing_rows','IX_standing_rows_SnapshotId_SeasonId',false),
('football','standing_rows','IX_standing_rows_TeamId',false),
('football','standing_snapshots','IX_standing_snapshots_SeasonId_ProviderKey_TableType_Scope_Ret~',false),
('football','team_external_ids','IX_team_external_ids_ProviderKey_IdentityScope_ExternalId',false),
('football','team_external_ids','IX_team_external_ids_TeamId',false)
        ), actual AS (
            SELECT n.nspname::text,c.relname::text,ic.relname::text,i.indisunique
            FROM pg_index i JOIN pg_class c ON c.oid=i.indrelid
            JOIN pg_class ic ON ic.oid=i.indexrelid JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname IN ('platform','football')
        )
        (SELECT * FROM expected EXCEPT SELECT * FROM actual)
        UNION ALL (SELECT * FROM actual EXCEPT SELECT * FROM expected)
    ) THEN RAISE EXCEPTION 'Expected 36 indexes (22 explicit and 14 constraint indexes) differ'; END IF;

    IF EXISTS (
        WITH expected(schema_name,index_name,column_names) AS (VALUES
('platform','IX_competition_external_ids_CompetitionId','CompetitionId'),
('platform','IX_competition_external_ids_ProviderKey_IdentityScope_External~','ProviderKey,IdentityScope,ExternalId'),
('platform','IX_competitions_SportId_Code','SportId,Code'),
('football','IX_match_external_ids_MatchId','MatchId'),
('football','IX_match_external_ids_ProviderKey_IdentityScope_ExternalId','ProviderKey,IdentityScope,ExternalId'),
('football','IX_matches_AwayTeamId','AwayTeamId'),
('football','IX_matches_HomeTeamId','HomeTeamId'),
('football','IX_matches_SeasonId_AwayTeamId','SeasonId,AwayTeamId'),
('football','IX_matches_SeasonId_HomeTeamId','SeasonId,HomeTeamId'),
('football','IX_matches_SeasonId_KickoffUtc','SeasonId,KickoffUtc'),
('football','IX_matches_SeasonId_Status_KickoffUtc','SeasonId,Status,KickoffUtc'),
('platform','IX_season_external_ids_ProviderKey_IdentityScope_ExternalId','ProviderKey,IdentityScope,ExternalId'),
('platform','IX_season_external_ids_SeasonId','SeasonId'),
('football','IX_season_teams_TeamId_SeasonId','TeamId,SeasonId'),
('platform','IX_seasons_CompetitionId_Key','CompetitionId,Key'),
('platform','IX_sports_Code','Code'),
('football','IX_standing_rows_SeasonId_TeamId','SeasonId,TeamId'),
('football','IX_standing_rows_SnapshotId_SeasonId','SnapshotId,SeasonId'),
('football','IX_standing_rows_TeamId','TeamId'),
('football','IX_standing_snapshots_SeasonId_ProviderKey_TableType_Scope_Ret~','SeasonId,ProviderKey,TableType,Scope,RetrievedAtUtc'),
('football','IX_team_external_ids_ProviderKey_IdentityScope_ExternalId','ProviderKey,IdentityScope,ExternalId'),
('football','IX_team_external_ids_TeamId','TeamId')
        ), actual AS (
            SELECT n.nspname::text,ic.relname::text,
                   (SELECT string_agg(a.attname,',' ORDER BY x.ordinality)
                    FROM unnest(i.indkey) WITH ORDINALITY x(attnum,ordinality)
                    JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=x.attnum)
            FROM pg_index i JOIN pg_class ic ON ic.oid=i.indexrelid
            JOIN pg_namespace n ON n.oid=ic.relnamespace
            WHERE n.nspname IN ('platform','football') AND ic.relname LIKE 'IX_%'
        )
        (SELECT * FROM expected EXCEPT SELECT * FROM actual)
        UNION ALL (SELECT * FROM actual EXCEPT SELECT * FROM expected)
    ) OR EXISTS (
        SELECT 1 FROM pg_index i JOIN pg_class c ON c.oid=i.indrelid
        JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_class ic ON ic.oid=i.indexrelid
        JOIN pg_am am ON am.oid=ic.relam
        WHERE n.nspname IN ('platform','football')
          AND (NOT i.indisvalid OR NOT i.indisready OR i.indpred IS NOT NULL
               OR i.indexprs IS NOT NULL OR am.amname <> 'btree')
    ) THEN RAISE EXCEPTION 'Index columns, validity or method differ'; END IF;
    IF (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname IN ('platform','football') AND c.relkind='r') <> 13
       OR EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                  WHERE n.nspname IN ('platform','football') AND (c.relkind NOT IN ('r','i') OR pg_get_userbyid(c.relowner) <> 'sportstats_migrator'))
       OR EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                  WHERE n.nspname IN ('platform','football'))
       OR EXISTS (SELECT 1 FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid
                  JOIN pg_namespace n ON n.oid=c.relnamespace
                  WHERE n.nspname IN ('platform','football') AND NOT t.tgisinternal)
    THEN RAISE EXCEPTION 'Unexpected tables, relations, owners, routines or user triggers'; END IF;
    RAISE NOTICE 'Verification passed: 12 application tables, history=1, reviewed constraints and 36 indexes';
END
$verify$;

SELECT n.nspname AS schema_name,c.relname AS table_name,
       pg_get_userbyid(c.relowner) AS owner
FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname IN ('platform','football') AND c.relkind='r'
ORDER BY 1,2;
SELECT n.nspname AS schema_name,c.relname AS table_name,k.conname,
       k.contype,pg_get_constraintdef(k.oid) AS definition
FROM pg_constraint k JOIN pg_class c ON c.oid=k.conrelid
JOIN pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname IN ('platform','football')
ORDER BY 1,2,3;
SELECT schemaname,tablename,indexname,indexdef FROM pg_indexes
WHERE schemaname IN ('platform','football') ORDER BY 1,2,3;
SELECT "MigrationId","ProductVersion" FROM platform."__EFMigrationsHistory";
