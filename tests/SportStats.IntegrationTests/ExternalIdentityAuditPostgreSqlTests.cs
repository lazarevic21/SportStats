using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Xunit;

namespace SportStats.IntegrationTests;

[Collection("Rollback-only PostgreSQL")]
[Trait("Category", "PostgreSQLLive")]
public sealed class ExternalIdentityAuditPostgreSqlTests
{
    [GuardedPostgreSqlFact]
    public async Task ExistingIdentityKeysAreUniqueInAllFourTables()
    {
        await using var fixture = await PostgreSqlTestDatabaseFixture.OpenAsync(
            Environment.GetEnvironmentVariable("SPORTSTATS_TEST_CONNECTION_STRING"));
        Assert.Equal(new long[] { 0, 0, 0, 0 }, await fixture.AuditExternalIdentityDuplicatesAsync());
    }

    [GuardedPostgreSqlFact]
    public async Task StagedSafeguardRejectsIdenticalAndConflictingKeysOnActualSchemaCopies()
    {
        await using var fixture = await PostgreSqlTestDatabaseFixture.OpenAsync(
            Environment.GetEnvironmentVariable("SPORTSTATS_TEST_CONNECTION_STRING"));
        await fixture.VerifyStagedIdentitySafeguardAsync(false);
    }

    [GuardedPostgreSqlFact]
    public async Task StagedSafeguardRefusesLegacyDuplicatesWithoutRepair()
    {
        await using var fixture = await PostgreSqlTestDatabaseFixture.OpenAsync(
            Environment.GetEnvironmentVariable("SPORTSTATS_TEST_CONNECTION_STRING"));
        await fixture.VerifyStagedIdentitySafeguardAsync(true);
    }
}

public sealed partial class PostgreSqlTestDatabaseFixture
{
    private static readonly (string Table, string Target)[] IdentityAuditTables =
    [
        ("platform.competition_external_ids", "CompetitionId"), ("platform.season_external_ids", "SeasonId"),
        ("football.team_external_ids", "TeamId"), ("football.match_external_ids", "MatchId")
    ];

    public async Task VerifyStagedIdentitySafeguardAsync(bool legacyDuplicates, CancellationToken token = default)
    {
        ValidateLiveOptIn(Environment.GetEnvironmentVariable("SPORTSTATS_RUN_POSTGRES_TESTS"));
        await VerifyOwnedSessionAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30)); token = deadline.Token;
        // Only TEMP copies are modified; permanent schema and data stay untouched.
        // EF still owns the transaction and its existing NeverCommitInterceptor stays installed.
        foreach (var subject in IdentityAuditTables)
        {
            await using var transaction = await context.Database.BeginTransactionAsync(token);
            var pgTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();
            async Task Execute(string sql)
            {
                await using var command = new NpgsqlCommand(sql, connection, pgTransaction);
                await command.ExecuteNonQueryAsync(token);
            }
            try
            {
                using var stream = typeof(ExternalIdentityAuditPostgreSqlTests).Assembly.GetManifestResourceStream("ExternalIdentityUniqueness.sql")!;
                using var reader = new StreamReader(stream);
                var script = string.Join('\n', (await reader.ReadToEndAsync(token)).Split('\n')
                    .Where(line => line.Trim() is not "BEGIN;" and not "COMMIT;"));
                foreach (var table in IdentityAuditTables)
                {
                    var temp = "pg_temp." + table.Table.Split('.')[1];
                    await Execute($"CREATE TEMP TABLE {table.Table.Split('.')[1]} (LIKE {table.Table} INCLUDING ALL) ON COMMIT DROP");
                    script = script.Replace(table.Table, temp, StringComparison.Ordinal);
                }
                var tempSubject = "pg_temp." + subject.Table.Split('.')[1];
                async Task Insert(string provider, string scope, string externalId, Guid target)
                {
                    await using var command = new NpgsqlCommand($"""
                        INSERT INTO {tempSubject} ("Id", "ProviderKey", "IdentityScope", "ExternalId", "{subject.Target}")
                        VALUES ($1, $2, $3, $4, $5)
                        """, connection, pgTransaction);
                    command.Parameters.AddWithValue(Guid.NewGuid()); command.Parameters.AddWithValue(provider);
                    command.Parameters.AddWithValue(scope); command.Parameters.AddWithValue(externalId); command.Parameters.AddWithValue(target);
                    await command.ExecuteNonQueryAsync(token);
                }
                var target = Guid.NewGuid();
                await Insert("fixture", "scope", "key", target);
                if (legacyDuplicates)
                {
                    await Insert("fixture", "scope", "key", Guid.NewGuid());
                    var failure = await Assert.ThrowsAsync<PostgresException>(() => Execute(script));
                    Assert.Equal(PostgresErrorCodes.RaiseException, failure.SqlState);
                }
                else
                {
                    await Execute(script);
                    foreach (var conflictingTarget in new[] { target, Guid.NewGuid() })
                    {
                        await Execute("SAVEPOINT identity_conflict");
                        var failure = await Assert.ThrowsAsync<PostgresException>(() => Insert("fixture", "scope", "key", conflictingTarget));
                        Assert.Equal(PostgresErrorCodes.UniqueViolation, failure.SqlState);
                        await Execute("ROLLBACK TO SAVEPOINT identity_conflict");
                    }
                    await Insert("other-provider", "scope", "key", target);
                    await Insert("fixture", "other-scope", "key", target);
                    await Insert("fixture", "scope", "alias", target);
                }
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await transaction.RollbackAsync(cleanup.Token); }
                catch { unusable = true; await connection.CloseAsync(); throw new InvalidOperationException("Temporary identity safeguard cleanup failed."); }
            }
        }
    }

    public async Task<long[]> AuditExternalIdentityDuplicatesAsync(CancellationToken token = default)
    {
        ValidateLiveOptIn(Environment.GetEnvironmentVariable("SPORTSTATS_RUN_POSTGRES_TESTS"));
        await VerifyOwnedSessionAsync(token);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token);
        try
        {
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection,
                (NpgsqlTransaction)transaction.GetDbTransaction()))
                await readOnly.ExecuteNonQueryAsync(token);
            var counts = new List<long>();
            foreach (var table in new[] { "platform.competition_external_ids", "platform.season_external_ids", "football.team_external_ids", "football.match_external_ids" })
            {
                // Table identifiers are compile-time constants; no row identities or credentials leave this audit.
                await using var command = new NpgsqlCommand($"""
                    SELECT count(*) FROM (
                        SELECT "ProviderKey", "IdentityScope", "ExternalId" FROM {table}
                        GROUP BY "ProviderKey", "IdentityScope", "ExternalId" HAVING count(*) > 1
                    ) AS duplicate_keys
                    """, connection, (NpgsqlTransaction)transaction.GetDbTransaction());
                counts.Add((long)(await command.ExecuteScalarAsync(token))!);
            }
            return counts.ToArray();
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await transaction.RollbackAsync(cleanup.Token); }
            catch { unusable = true; await connection.CloseAsync(); throw new InvalidOperationException("Read-only identity audit cleanup failed."); }
        }
    }
}
