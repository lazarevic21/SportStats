using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportStats.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "platform");

            migrationBuilder.EnsureSchema(
                name: "football");

            migrationBuilder.CreateTable(
                name: "sports",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "teams",
                schema: "football",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ShortName = table.Column<string>(type: "text", nullable: true),
                    CrestUrl = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "competitions",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_competitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_competitions_sports_SportId",
                        column: x => x.SportId,
                        principalSchema: "platform",
                        principalTable: "sports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "team_external_ids",
                schema: "football",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    IdentityScope = table.Column<string>(type: "text", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_external_ids", x => x.Id);
                    table.ForeignKey(
                        name: "FK_team_external_ids_teams_TeamId",
                        column: x => x.TeamId,
                        principalSchema: "football",
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "competition_external_ids",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    IdentityScope = table.Column<string>(type: "text", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    CompetitionId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_competition_external_ids", x => x.Id);
                    table.ForeignKey(
                        name: "FK_competition_external_ids_competitions_CompetitionId",
                        column: x => x.CompetitionId,
                        principalSchema: "platform",
                        principalTable: "competitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "seasons",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompetitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_seasons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_seasons_competitions_CompetitionId",
                        column: x => x.CompetitionId,
                        principalSchema: "platform",
                        principalTable: "competitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "season_external_ids",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    IdentityScope = table.Column<string>(type: "text", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    SeasonId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_season_external_ids", x => x.Id);
                    table.ForeignKey(
                        name: "FK_season_external_ids_seasons_SeasonId",
                        column: x => x.SeasonId,
                        principalSchema: "platform",
                        principalTable: "seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "season_teams",
                schema: "football",
                columns: table => new
                {
                    SeasonId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_season_teams", x => new { x.SeasonId, x.TeamId });
                    table.ForeignKey(
                        name: "FK_season_teams_seasons_SeasonId",
                        column: x => x.SeasonId,
                        principalSchema: "platform",
                        principalTable: "seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_season_teams_teams_TeamId",
                        column: x => x.TeamId,
                        principalSchema: "football",
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "standing_snapshots",
                schema: "football",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    RetrievedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TableType = table.Column<string>(type: "text", nullable: false),
                    Scope = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_standing_snapshots", x => x.Id);
                    table.UniqueConstraint("AK_standing_snapshots_Id_SeasonId", x => new { x.Id, x.SeasonId });
                    table.ForeignKey(
                        name: "FK_standing_snapshots_seasons_SeasonId",
                        column: x => x.SeasonId,
                        principalSchema: "platform",
                        principalTable: "seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "matches",
                schema: "football",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonId = table.Column<Guid>(type: "uuid", nullable: false),
                    HomeTeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    AwayTeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    KickoffUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Matchday = table.Column<int>(type: "integer", nullable: true),
                    HomeGoals = table.Column<int>(type: "integer", nullable: true),
                    AwayGoals = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_matches", x => x.Id);
                    table.CheckConstraint("CK_matches_away_goals", "\"AwayGoals\" IS NULL OR \"AwayGoals\" >= 0");
                    table.CheckConstraint("CK_matches_distinct_teams", "\"HomeTeamId\" <> \"AwayTeamId\"");
                    table.CheckConstraint("CK_matches_home_goals", "\"HomeGoals\" IS NULL OR \"HomeGoals\" >= 0");
                    table.ForeignKey(
                        name: "FK_matches_season_teams_SeasonId_AwayTeamId",
                        columns: x => new { x.SeasonId, x.AwayTeamId },
                        principalSchema: "football",
                        principalTable: "season_teams",
                        principalColumns: new[] { "SeasonId", "TeamId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_matches_season_teams_SeasonId_HomeTeamId",
                        columns: x => new { x.SeasonId, x.HomeTeamId },
                        principalSchema: "football",
                        principalTable: "season_teams",
                        principalColumns: new[] { "SeasonId", "TeamId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_matches_seasons_SeasonId",
                        column: x => x.SeasonId,
                        principalSchema: "platform",
                        principalTable: "seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_matches_teams_AwayTeamId",
                        column: x => x.AwayTeamId,
                        principalSchema: "football",
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_matches_teams_HomeTeamId",
                        column: x => x.HomeTeamId,
                        principalSchema: "football",
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "standing_rows",
                schema: "football",
                columns: table => new
                {
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_standing_rows", x => new { x.SnapshotId, x.TeamId });
                    table.ForeignKey(
                        name: "FK_standing_rows_season_teams_SeasonId_TeamId",
                        columns: x => new { x.SeasonId, x.TeamId },
                        principalSchema: "football",
                        principalTable: "season_teams",
                        principalColumns: new[] { "SeasonId", "TeamId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_standing_rows_standing_snapshots_SnapshotId_SeasonId",
                        columns: x => new { x.SnapshotId, x.SeasonId },
                        principalSchema: "football",
                        principalTable: "standing_snapshots",
                        principalColumns: new[] { "Id", "SeasonId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_standing_rows_teams_TeamId",
                        column: x => x.TeamId,
                        principalSchema: "football",
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "match_external_ids",
                schema: "football",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    IdentityScope = table.Column<string>(type: "text", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    MatchId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_match_external_ids", x => x.Id);
                    table.ForeignKey(
                        name: "FK_match_external_ids_matches_MatchId",
                        column: x => x.MatchId,
                        principalSchema: "football",
                        principalTable: "matches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_competition_external_ids_CompetitionId",
                schema: "platform",
                table: "competition_external_ids",
                column: "CompetitionId");

            migrationBuilder.CreateIndex(
                name: "IX_competition_external_ids_ProviderKey_IdentityScope_External~",
                schema: "platform",
                table: "competition_external_ids",
                columns: new[] { "ProviderKey", "IdentityScope", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_competitions_SportId_Code",
                schema: "platform",
                table: "competitions",
                columns: new[] { "SportId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_match_external_ids_MatchId",
                schema: "football",
                table: "match_external_ids",
                column: "MatchId");

            migrationBuilder.CreateIndex(
                name: "IX_match_external_ids_ProviderKey_IdentityScope_ExternalId",
                schema: "football",
                table: "match_external_ids",
                columns: new[] { "ProviderKey", "IdentityScope", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_matches_AwayTeamId",
                schema: "football",
                table: "matches",
                column: "AwayTeamId");

            migrationBuilder.CreateIndex(
                name: "IX_matches_HomeTeamId",
                schema: "football",
                table: "matches",
                column: "HomeTeamId");

            migrationBuilder.CreateIndex(
                name: "IX_matches_SeasonId_AwayTeamId",
                schema: "football",
                table: "matches",
                columns: new[] { "SeasonId", "AwayTeamId" });

            migrationBuilder.CreateIndex(
                name: "IX_matches_SeasonId_HomeTeamId",
                schema: "football",
                table: "matches",
                columns: new[] { "SeasonId", "HomeTeamId" });

            migrationBuilder.CreateIndex(
                name: "IX_matches_SeasonId_KickoffUtc",
                schema: "football",
                table: "matches",
                columns: new[] { "SeasonId", "KickoffUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_matches_SeasonId_Status_KickoffUtc",
                schema: "football",
                table: "matches",
                columns: new[] { "SeasonId", "Status", "KickoffUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_season_external_ids_ProviderKey_IdentityScope_ExternalId",
                schema: "platform",
                table: "season_external_ids",
                columns: new[] { "ProviderKey", "IdentityScope", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_season_external_ids_SeasonId",
                schema: "platform",
                table: "season_external_ids",
                column: "SeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_season_teams_TeamId_SeasonId",
                schema: "football",
                table: "season_teams",
                columns: new[] { "TeamId", "SeasonId" });

            migrationBuilder.CreateIndex(
                name: "IX_seasons_CompetitionId_Key",
                schema: "platform",
                table: "seasons",
                columns: new[] { "CompetitionId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sports_Code",
                schema: "platform",
                table: "sports",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_standing_rows_SeasonId_TeamId",
                schema: "football",
                table: "standing_rows",
                columns: new[] { "SeasonId", "TeamId" });

            migrationBuilder.CreateIndex(
                name: "IX_standing_rows_SnapshotId_SeasonId",
                schema: "football",
                table: "standing_rows",
                columns: new[] { "SnapshotId", "SeasonId" });

            migrationBuilder.CreateIndex(
                name: "IX_standing_rows_TeamId",
                schema: "football",
                table: "standing_rows",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_standing_snapshots_SeasonId_ProviderKey_TableType_Scope_Ret~",
                schema: "football",
                table: "standing_snapshots",
                columns: new[] { "SeasonId", "ProviderKey", "TableType", "Scope", "RetrievedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_team_external_ids_ProviderKey_IdentityScope_ExternalId",
                schema: "football",
                table: "team_external_ids",
                columns: new[] { "ProviderKey", "IdentityScope", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_team_external_ids_TeamId",
                schema: "football",
                table: "team_external_ids",
                column: "TeamId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "competition_external_ids",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "match_external_ids",
                schema: "football");

            migrationBuilder.DropTable(
                name: "season_external_ids",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "standing_rows",
                schema: "football");

            migrationBuilder.DropTable(
                name: "team_external_ids",
                schema: "football");

            migrationBuilder.DropTable(
                name: "matches",
                schema: "football");

            migrationBuilder.DropTable(
                name: "standing_snapshots",
                schema: "football");

            migrationBuilder.DropTable(
                name: "season_teams",
                schema: "football");

            migrationBuilder.DropTable(
                name: "seasons",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "teams",
                schema: "football");

            migrationBuilder.DropTable(
                name: "competitions",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "sports",
                schema: "platform");
        }
    }
}
