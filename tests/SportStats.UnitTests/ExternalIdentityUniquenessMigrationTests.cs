using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.Migrations;
using Xunit;

namespace SportStats.UnitTests;

public sealed class ExternalIdentityUniquenessMigrationTests
{
    private static readonly string[] IdentityColumns = ["ProviderKey", "IdentityScope", "ExternalId"];
    private static readonly (string Schema, string Table)[] Tables =
    [
        ("platform", "competition_external_ids"), ("platform", "season_external_ids"),
        ("football", "team_external_ids"), ("football", "match_external_ids")
    ];

    [Fact]
    public void ModelEnforcesExactScopedIdentityAndRetainsLegacyIndexes()
    {
        using var context = new SportStatsDesignTimeDbContextFactory().CreateDbContext([]);
        foreach (var (schema, table) in Tables)
        {
            var entity = Assert.Single(context.Model.GetEntityTypes(), e => e.GetSchema() == schema && e.GetTableName() == table);
            foreach (var column in IdentityColumns)
                Assert.False(entity.FindProperty(column)!.IsNullable);
            var indexes = entity.GetIndexes().Where(i => i.Properties.Select(p => p.Name).SequenceEqual(IdentityColumns)).ToArray();
            Assert.Contains(indexes, i => i.IsUnique && i.GetDatabaseName() == $"UX_{table}_identity" && i.GetFilter() is null);
            Assert.Contains(indexes, i => !i.IsUnique && i.GetDatabaseName()!.StartsWith("IX_", StringComparison.Ordinal));
        }
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void UpgradeLocksAndAuditsAllMappingsBeforeCreatingUniqueIndexesWithoutRewritingData()
    {
        var operations = new ExternalIdentityUniqueness().UpOperations;
        Assert.All(operations, o => Assert.True(o is SqlOperation or CreateIndexOperation));
        var firstIndex = operations.ToList().FindIndex(o => o is CreateIndexOperation);
        Assert.True(firstIndex > 0);
        Assert.All(operations.Take(firstIndex), o => Assert.IsType<SqlOperation>(o));
        Assert.All(operations.Skip(firstIndex), o => Assert.IsType<CreateIndexOperation>(o));
        var sqlOperations = operations.OfType<SqlOperation>().ToArray();
        Assert.All(sqlOperations, o => Assert.False(o.SuppressTransaction));
        var sql = string.Join("\n", sqlOperations.Select(o => o.Sql));
        Assert.Contains("SET LOCAL lock_timeout = '5s'", sql);
        Assert.Contains("SET LOCAL statement_timeout = '60s'", sql);
        Assert.Contains("IN SHARE ROW EXCLUSIVE MODE", sql);
        Assert.True(sql.IndexOf("LOCK TABLE", StringComparison.Ordinal) < sql.IndexOf("HAVING", StringComparison.Ordinal));
        Assert.Contains("RAISE EXCEPTION", sql);
        Assert.DoesNotMatch(new Regex(@"\b(DELETE|UPDATE|INSERT|MERGE|TRUNCATE)\b", RegexOptions.IgnoreCase), sql);
        Assert.DoesNotContain("IF NOT EXISTS", sql, StringComparison.OrdinalIgnoreCase);
        foreach (var (schema, table) in Tables)
        {
            Assert.Contains($"{schema}.{table}", sql);
            Assert.Contains($"FROM {schema}.{table} GROUP BY \"ProviderKey\", \"IdentityScope\", \"ExternalId\" HAVING count(*) > 1", sql);
            var index = Assert.Single(operations.OfType<CreateIndexOperation>(), i => i.Table == table);
            Assert.Equal(schema, index.Schema);
            Assert.Equal($"UX_{table}_identity", index.Name);
            Assert.Equal(IdentityColumns, index.Columns);
            Assert.True(index.IsUnique);
            Assert.Null(index.Filter);
        }
        Assert.Equal(4, operations.OfType<CreateIndexOperation>().Count());
    }

    [Fact]
    public void DowngradeOnlyDropsNewUniqueIndexes()
    {
        var operations = new ExternalIdentityUniqueness().DownOperations;
        Assert.Equal(4, operations.Count);
        Assert.All(operations, o => Assert.IsType<DropIndexOperation>(o));
        foreach (var (schema, table) in Tables)
        {
            var index = Assert.Single(operations.OfType<DropIndexOperation>(), i => i.Name == $"UX_{table}_identity");
            Assert.Equal(schema, index.Schema);
            Assert.Equal(table, index.Table);
        }
    }

    [Fact]
    public void GeneratedUpgradeScriptIsTransactionalAndRecordsHistoryAfterConstraints()
    {
        using var context = new SportStatsDesignTimeDbContextFactory().CreateDbContext([]);
        var migrations = context.GetService<IMigrationsAssembly>().Migrations;
        var initial = Assert.Single(migrations, m => m.Value.AsType() == typeof(InitialCreate)).Key;
        var target = Assert.Single(migrations, m => m.Value.AsType() == typeof(ExternalIdentityUniqueness)).Key;
        var script = context.GetService<IMigrator>().GenerateScript(initial, target);
        Assert.Contains("START TRANSACTION;", script);
        Assert.Contains("COMMIT;", script);
        Assert.DoesNotContain("CONCURRENTLY", script, StringComparison.OrdinalIgnoreCase);
        var history = script.IndexOf("INSERT INTO platform.\"__EFMigrationsHistory\"", StringComparison.Ordinal);
        Assert.True(history > 0);
        foreach (var (_, table) in Tables)
        {
            var index = script.IndexOf($"CREATE UNIQUE INDEX \"UX_{table}_identity\"", StringComparison.Ordinal);
            Assert.True(index > script.IndexOf("RAISE EXCEPTION", StringComparison.Ordinal));
            Assert.True(index < history);
        }
        Assert.True(script.LastIndexOf("COMMIT;", StringComparison.Ordinal) > history);
        Assert.Contains(target, script);
    }

    [Fact]
    public void OriginalMigrationStillAllowsAmbiguityForLegacyFixtures()
    {
        var operations = new InitialCreate().UpOperations;
        foreach (var (_, table) in Tables)
        {
            var index = Assert.Single(operations.OfType<CreateIndexOperation>(), i => i.Table == table && i.Columns.SequenceEqual(IdentityColumns));
            Assert.False(index.IsUnique);
            Assert.StartsWith("IX_", index.Name);
            var createTable = Assert.Single(operations.OfType<CreateTableOperation>(), t => t.Name == table);
            foreach (var column in IdentityColumns)
                Assert.False(Assert.Single(createTable.Columns, c => c.Name == column).IsNullable);
        }
    }
}
