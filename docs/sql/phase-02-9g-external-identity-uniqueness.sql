-- HISTORICAL 2.9G VERIFICATION ONLY. Do not deploy this script or run after the 2.9H migration.
-- Audit the intended target first. Never repair or delete duplicate mappings automatically.
-- Superseded for deployment by EF ExternalIdentityUniqueness; legacy ambiguity fixtures retain
-- the original schema. A deployed target needs independent post-migration certification.
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '60s';

DO $target$
BEGIN
    IF current_database() <> 'sportstats_test' THEN
        RAISE EXCEPTION 'External identity safeguard requires the approved test database target.';
    END IF;
END
$target$;

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

-- Exact stored key equality; aliases and different providers/scopes remain valid.
-- Existing nonunique indexes are retained to avoid destructive schema operations.
-- No IF NOT EXISTS: an unexpected existing object fails closed instead of claiming safety.
CREATE UNIQUE INDEX "UX_competition_external_ids_identity" ON platform.competition_external_ids ("ProviderKey", "IdentityScope", "ExternalId");
CREATE UNIQUE INDEX "UX_season_external_ids_identity" ON platform.season_external_ids ("ProviderKey", "IdentityScope", "ExternalId");
CREATE UNIQUE INDEX "UX_team_external_ids_identity" ON football.team_external_ids ("ProviderKey", "IdentityScope", "ExternalId");
CREATE UNIQUE INDEX "UX_match_external_ids_identity" ON football.match_external_ids ("ProviderKey", "IdentityScope", "ExternalId");
COMMIT;
