START TRANSACTION;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '60s';
-- Prevent the audit-to-index race, including non-cooperating writers.
LOCK TABLE platform.competition_external_ids, platform.season_external_ids,
    football.team_external_ids, football.match_external_ids IN SHARE ROW EXCLUSIVE MODE;

DO $audit$
BEGIN
    IF EXISTS (SELECT 1 FROM platform.competition_external_ids GROUP BY "ProviderKey", "IdentityScope", "ExternalId" HAVING count(*) > 1)
        OR EXISTS (SELECT 1 FROM platform.season_external_ids GROUP BY "ProviderKey", "IdentityScope", "ExternalId" HAVING count(*) > 1)
        OR EXISTS (SELECT 1 FROM football.team_external_ids GROUP BY "ProviderKey", "IdentityScope", "ExternalId" HAVING count(*) > 1)
        OR EXISTS (SELECT 1 FROM football.match_external_ids GROUP BY "ProviderKey", "IdentityScope", "ExternalId" HAVING count(*) > 1)
    THEN
        RAISE EXCEPTION 'External identity uniqueness audit failed; no mappings were changed.';
    END IF;
END
$audit$;

CREATE UNIQUE INDEX "UX_team_external_ids_identity" ON football.team_external_ids ("ProviderKey", "IdentityScope", "ExternalId");

CREATE UNIQUE INDEX "UX_season_external_ids_identity" ON platform.season_external_ids ("ProviderKey", "IdentityScope", "ExternalId");

CREATE UNIQUE INDEX "UX_match_external_ids_identity" ON football.match_external_ids ("ProviderKey", "IdentityScope", "ExternalId");

CREATE UNIQUE INDEX "UX_competition_external_ids_identity" ON platform.competition_external_ids ("ProviderKey", "IdentityScope", "ExternalId");

INSERT INTO platform."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261019102703_ExternalIdentityUniqueness', '10.0.12');

COMMIT;

