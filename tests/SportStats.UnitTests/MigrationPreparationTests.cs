using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.Migrations;
using Xunit;

namespace SportStats.UnitTests;

public sealed class MigrationPreparationTests
{
    [Fact]
    public void DesignTimeModelNeedsNoConnectionAndMatchesGeneratedSnapshot()
    {
        using var context = new SportStatsDesignTimeDbContextFactory().CreateDbContext([]);
        Assert.Null(context.Database.GetConnectionString());
        Assert.False(context.Database.HasPendingModelChanges());
        var options = Assert.Single(context.GetService<IDbContextOptions>().Extensions.OfType<RelationalOptionsExtension>());
        Assert.Equal("platform", options.MigrationsHistoryTableSchema);
        Assert.Equal("__EFMigrationsHistory", options.MigrationsHistoryTableName);
        var core = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()!;
        Assert.False(core.IsSensitiveDataLoggingEnabled);
        Assert.False(core.DetailedErrorsEnabled);
    }

    [Fact]
    public void InitialMigrationContainsOnlyApprovedSchemaObjectsAndDeleteRules()
    {
        var operations = new InitialCreate().UpOperations;
        Assert.All(operations, operation => Assert.True(operation is EnsureSchemaOperation or CreateTableOperation or CreateIndexOperation));
        Assert.Equal(new[] { "football", "platform" }, operations.OfType<EnsureSchemaOperation>().Select(o => o.Name).Order());
        var tables = operations.OfType<CreateTableOperation>().ToArray();
        Assert.Equal(new[]
        {
            "football.match_external_ids", "football.matches", "football.season_teams", "football.standing_rows",
            "football.standing_snapshots", "football.team_external_ids", "football.teams",
            "platform.competition_external_ids", "platform.competitions", "platform.season_external_ids", "platform.seasons", "platform.sports"
        }.Order(), tables.Select(t => $"{t.Schema}.{t.Name}").Order());
        foreach (var table in tables)
        {
            Assert.NotNull(table.PrimaryKey);
            foreach (var columnName in table.PrimaryKey.Columns)
                Assert.Equal("uuid", Assert.Single(table.Columns, c => c.Name == columnName).ColumnType);
            foreach (var foreignKey in table.ForeignKeys)
                Assert.Equal(table.Name == "standing_rows" && foreignKey.PrincipalTable == "standing_snapshots"
                    ? ReferentialAction.Cascade : ReferentialAction.Restrict, foreignKey.OnDelete);
        }
        var matches = Assert.Single(tables, t => t.Name == "matches");
        Assert.True(Assert.Single(matches.Columns, c => c.Name == "HomeGoals").IsNullable);
        Assert.True(Assert.Single(matches.Columns, c => c.Name == "AwayGoals").IsNullable);
        Assert.Equal(3, matches.CheckConstraints.Count);
        var rows = Assert.Single(tables, t => t.Name == "standing_rows");
        Assert.Equal(new[] { "Position", "SeasonId", "SnapshotId", "TeamId" }, rows.Columns.Select(c => c.Name).Order());
    }
}
