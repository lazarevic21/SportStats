using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.InitialImport;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Football;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.InitialImport;
using Xunit;

namespace SportStats.IntegrationTests;

[Collection("Rollback-only PostgreSQL")]
[Trait("Category", "PostgreSQLLive")]
public sealed class InitialImportOwnerPostgreSqlTests
{
    [GuardedPostgreSqlFact] public Task CommitRefusalRollsBackOwnedTransaction() => Verify(0);
    [GuardedPostgreSqlFact] public Task FinalStageFailureRollsBackOwnedTransaction() => Verify(1);
    [GuardedPostgreSqlFact] public Task FinalStageCancellationRollsBackOwnedTransaction() => Verify(2);
    [GuardedPostgreSqlFact] public Task ExistingTransactionIsRejectedWithoutTakingOwnership() => Verify(3);
    [GuardedPostgreSqlFact] public Task ReturnedConflictRollsBackOwnedTransactionAndResetsCreates() => Verify(4);
    private static async Task Verify(int scenario)
    {
        await using var fixture = await PostgreSqlTestDatabaseFixture.OpenAsync(Environment.GetEnvironmentVariable("SPORTSTATS_TEST_CONNECTION_STRING"));
        await fixture.VerifyInitialImportOwnerAsync(scenario);
    }
}

public sealed partial class PostgreSqlTestDatabaseFixture
{
    internal static IdentityResolutionBatch VerificationBatch(Guid sportId)
    {
        var competition = new NormalizedCompetition(new("football-data.org", "competition:PL", "2021"), "PL", "Premier League");
        var season = new NormalizedSeason(new("football-data.org", "competition:PL/season:2026", "2502"), 2026, "2026/27", new(2026, 8, 21), new(2027, 5, 30));
        var home = new NormalizedTeam(new("football-data.org", "competition:PL", "57"), "Home", null, null);
        var away = new NormalizedTeam(new("football-data.org", "competition:PL", "65"), "Away", null, null);
        var match = new NormalizedMatch(new("football-data.org", "competition:PL/season:2026", "560542"), season, home.Identity, away.Identity,
            new(2026, 8, 21, 19, 0, 0, DateTimeKind.Utc), FootballMatchStatus.Finished, "FINISHED", 1,
            new(ScoreFieldPresence.Value, 0), new(ScoreFieldPresence.Value, 2));
        return new InitialImportBundle(competition, season, [home, away], [match]).ToBatch(sportId);
    }

    internal async Task VerifyInitialImportOwnerAsync(int scenario)
    {
        ValidateLiveOptIn(Environment.GetEnvironmentVariable("SPORTSTATS_RUN_POSTGRES_TESTS"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = timeout.Token;
        await VerifyOwnedSessionAsync(token);
        var batch = VerificationBatch(await context.Sports.Where(x => x.Code == "football").Select(x => x.Id).SingleAsync(token));
        if (await context.Competitions.AnyAsync(x => x.SportId == batch.Competitions[0].SportId && x.Code == "PL", token))
            throw new InvalidOperationException("Unoccupied football/PL scope required.");
        var before = await InitialImportRowsAsync(token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ownerProbe = new ImportOwnerProbe(this, scenario, cancellation, batch.Competitions[0].SportId);
        var options = new DbContextOptionsBuilder<SportStatsDbContext>(CreateScenarioOptions(connection, scenarioInterceptor)).AddInterceptors(ownerProbe).Options;
        await using var owner = new SportStatsDbContext(options);
        try
        {
            var store = new EfInitialImportStore(owner);
            if (scenario == 3)
            {
                await using var outer = await owner.Database.BeginTransactionAsync(token);
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyForSafetyVerificationAsync(batch, token));
                Assert.Same(outer, owner.Database.CurrentTransaction);
                await outer.RollbackAsync(token);
            }
            else if (scenario == 4)
            {
                var result = await store.ApplyForSafetyVerificationAsync(batch, token);
                Assert.False(result.Success);
                Assert.False(result.Applied);
                Assert.All(result.Counts, count => Assert.Equal(0, count.Created));
                Assert.Contains(result.Conflicts, failure => failure.Code == IdentityResolutionFailureCode.NaturalKeyConflict);
            }
            else if (scenario == 2)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ApplyForSafetyVerificationAsync(batch, cancellation.Token));
            else if (scenario == 1)
                await Assert.ThrowsAsync<DbUpdateException>(() => store.ApplyForSafetyVerificationAsync(batch, token));
            else
            {
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyForSafetyVerificationAsync(batch, token));
                Assert.Contains("indeterminate", exception.Message);
                Assert.Contains("must never commit", exception.InnerException!.Message);
            }
            Assert.Null(owner.Database.CurrentTransaction);
            Assert.Empty(owner.ChangeTracker.Entries());
            if (scenario < 3) Assert.True(scenarioInterceptor.EarlierInsertObserved);
            Assert.Equal(before, await InitialImportRowsAsync(token));
            await VerifyOwnedSessionAsync(token);
        }
        finally { scenarioInterceptor.Deactivate(); }
    }

    private sealed class ImportOwnerProbe(PostgreSqlTestDatabaseFixture fixture, int scenario, CancellationTokenSource cancellation, Guid sportId) : DbTransactionInterceptor
    {
        public override async ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData,
            DbTransaction result, CancellationToken cancellationToken = default)
        {
            fixture.scenarioInterceptor.ActivateMatch(fixture.connection, result, () => fixture.VerifyOwnedSessionAsync(cancellationToken));
            fixture.scenarioInterceptor.ObserveMatchResolution(scenario == 1, scenario == 2 ? cancellation : null);
            if (scenario == 4)
            {
                // Seed a conflicting canonical owner inside the lifecycle-owned transaction;
                // it must disappear when the returned planner conflict rolls that transaction back.
                await using var command = connection.CreateCommand();
                command.Transaction = result;
                command.CommandText = "INSERT INTO platform.competitions (\"Id\", \"SportId\", \"Code\", \"Name\") VALUES (@id, @sport, 'PL', 'Unmapped owner')";
                var id = command.CreateParameter(); id.ParameterName = "id"; id.Value = Guid.NewGuid(); command.Parameters.Add(id);
                var sport = command.CreateParameter(); sport.ParameterName = "sport"; sport.Value = sportId; command.Parameters.Add(sport);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            return result;
        }
    }
}
