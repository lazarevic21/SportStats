using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportStats.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExternalIdentityUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Keep audit and index creation in the same EF-owned transaction.
            migrationBuilder.Sql("""
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
                """);

            migrationBuilder.CreateIndex(
                name: "UX_team_external_ids_identity",
                schema: "football",
                table: "team_external_ids",
                columns: new[] { "ProviderKey", "IdentityScope", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_season_external_ids_identity",
                schema: "platform",
                table: "season_external_ids",
                columns: new[] { "ProviderKey", "IdentityScope", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_match_external_ids_identity",
                schema: "football",
                table: "match_external_ids",
                columns: new[] { "ProviderKey", "IdentityScope", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_competition_external_ids_identity",
                schema: "platform",
                table: "competition_external_ids",
                columns: new[] { "ProviderKey", "IdentityScope", "ExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_team_external_ids_identity",
                schema: "football",
                table: "team_external_ids");

            migrationBuilder.DropIndex(
                name: "UX_season_external_ids_identity",
                schema: "platform",
                table: "season_external_ids");

            migrationBuilder.DropIndex(
                name: "UX_match_external_ids_identity",
                schema: "football",
                table: "match_external_ids");

            migrationBuilder.DropIndex(
                name: "UX_competition_external_ids_identity",
                schema: "platform",
                table: "competition_external_ids");
        }
    }
}
