using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SportStats.Application.Football.InitialImport;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Football;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.InitialImport;

namespace SportStats.IntegrationTests;

public enum InitialImportScenario { Preview, CreationReplay, Conflict, FailureRollback, CancellationRollback, DuplicateMappings, AmbiguousMappings, Constraints, MissingMapping, Concurrent }
public sealed record InitialImportObservation(bool BehaviorCorrect, bool AllRowsRolledBack, bool TrackingEmpty);

public sealed partial class PostgreSqlTestDatabaseFixture
{
    public async Task<InitialImportObservation> RunInitialImportScenarioAsync(InitialImportScenario scenario, CancellationToken token = default)
    {
        ValidateLiveOptIn(Environment.GetEnvironmentVariable("SPORTSTATS_RUN_POSTGRES_TESTS"));
        if (!Enum.IsDefined(scenario) || unusable) throw new InvalidOperationException("The guarded scenario is unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(40)); token = timeout.Token;
        await VerifyOwnedSessionAsync(token);
        if (context.Database.CurrentTransaction is not null || context.ChangeTracker.Entries().Any()) throw new InvalidOperationException("A clean fixture session is required.");
        var sportId = await context.Sports.Where(x => x.Code == "football").Select(x => x.Id).SingleAsync(token);
        if (await context.Competitions.AnyAsync(x => x.SportId == sportId && x.Code == "PL", token)) throw new InvalidOperationException("Unoccupied football/PL scope required.");
        var competition = new NormalizedCompetition(new("football-data.org", "competition:PL", "2021"), "PL", "Premier League");
        var season = new NormalizedSeason(new("football-data.org", "competition:PL/season:2026", "2502"), 2026, "2026/27", new(2026, 8, 21), new(2027, 5, 30));
        var home = new NormalizedTeam(new("football-data.org", "competition:PL", "57"), "Home", null, null);
        var away = new NormalizedTeam(new("football-data.org", "competition:PL", "65"), "Away", null, null);
        var match = new NormalizedMatch(new("football-data.org", "competition:PL/season:2026", "560542"), season, home.Identity, away.Identity,
            new(2026, 8, 21, 19, 0, 0, DateTimeKind.Utc), FootballMatchStatus.Finished, "FINISHED", 1,
            new(ScoreFieldPresence.Value, 0), new(ScoreFieldPresence.Value, 2));
        var batch = new InitialImportBundle(competition, season, [home, away], [match]).ToBatch(sportId);
        var beforeRows = await InitialImportRowsAsync(token);
        var correct = false; var trackingEmpty = false;
        var ownerReleased = false;
        var standalonePreviewCorrect = true;
        if (scenario == InitialImportScenario.Preview)
        {
            var standalone = await new EfInitialImportStore(context).PreviewAsync(batch, token);
            var afterStandalone = await InitialImportRowsAsync(token);
            standalonePreviewCorrect = standalone.Success && !standalone.Applied && context.Database.CurrentTransaction is null &&
                !context.ChangeTracker.Entries().Any() && beforeRows.SequenceEqual(afterStandalone);
        }
        var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        try
        {
            scenarioInterceptor.ActivateMatch(connection, transaction.GetDbTransaction(), () => VerifyOwnedSessionAsync(token));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            scenarioInterceptor.ObserveMatchResolution(false);
            var store = new EfInitialImportStore(context, TimeSpan.FromSeconds(5));
            var preview = await store.PreviewAsync(batch, token);
            correct = standalonePreviewCorrect && preview.Success && !preview.Applied && preview.Counts.Single(x => x.Entity == "Matches").Created == 1 &&
                !await context.Competitions.AnyAsync(x => x.SportId == sportId && x.Code == "PL", token);
            if (scenario == InitialImportScenario.MissingMapping)
            {
                context.Add(new SportStats.Domain.Platform.Competition(Guid.NewGuid(), await context.Sports.SingleAsync(x => x.Id == sportId, token), "PL", "Unmapped owner"));
                await context.SaveChangesAsync(token); context.ChangeTracker.Clear();
                var conflicted = await store.ApplyWithinTransactionAsync(batch, token);
                correct &= !conflicted.Success && conflicted.Conflicts.Any(x => x.Code == SportStats.Application.Football.IdentityResolution.IdentityResolutionFailureCode.NaturalKeyConflict);
            }
            else if (scenario == InitialImportScenario.Preview)
            {
                // Reject all SaveChanges and DML/sequence/advisory commands, including transient writes.
                var probe = new InitialPreviewProbe();
                var options = new DbContextOptionsBuilder<SportStatsDbContext>(CreateScenarioOptions(connection, scenarioInterceptor)).AddInterceptors(probe, new InitialPreviewSaveProbe()).Options;
                await using var readContext = new SportStatsDbContext(options);
                await readContext.Database.UseTransactionAsync(transaction.GetDbTransaction(), token);
                var observed = await new EfInitialImportStore(readContext).PreviewAsync(batch, token);
                correct &= observed.Success && probe.Reads > 0 && !readContext.ChangeTracker.Entries().Any();
            }
            else if (scenario is InitialImportScenario.FailureRollback or InitialImportScenario.CancellationRollback)
            {
                var failureProbe = new InitialFinalStageProbe(scenario == InitialImportScenario.CancellationRollback ? cancellation : null);
                var failureOptions = new DbContextOptionsBuilder<SportStatsDbContext>(CreateScenarioOptions(connection, scenarioInterceptor)).AddInterceptors(failureProbe).Options;
                await using var writeContext = new SportStatsDbContext(failureOptions);
                await writeContext.Database.UseTransactionAsync(transaction.GetDbTransaction(), token);
                try { await new EfInitialImportStore(writeContext).ApplyWithinTransactionAsync(batch, cancellation.Token); correct = false; }
                catch (DbUpdateException exception) when (scenario == InitialImportScenario.FailureRollback && exception.InnerException is InitialFinalStageFailure) { }
                catch (OperationCanceledException) when (scenario == InitialImportScenario.CancellationRollback && cancellation.IsCancellationRequested) { }
                correct &= scenarioInterceptor.EarlierInsertObserved && await context.Competitions.AnyAsync(x => x.Code == "PL", token);
            }
            else
            {
                var created = await store.ApplyWithinTransactionAsync(batch, token);
                var matchId = await context.FootballMatchExternalIdentities.Where(x => x.ProviderKey == match.Identity.ProviderKey && x.IdentityScope == match.Identity.IdentityScope && x.ExternalId == match.Identity.ExternalId).Select(x => x.MatchId).SingleAsync(token);
                var original = await context.FootballMatches.AsNoTracking().SingleAsync(x => x.Id == matchId, token);
                var ids = await context.FootballMatchExternalIdentities.AsNoTracking().Where(x => x.MatchId == original.Id).Select(x => x.Id).ToArrayAsync(token);
                if (scenario == InitialImportScenario.Concurrent)
                {
                    await using var contender = await OpenAsync(Environment.GetEnvironmentVariable("SPORTSTATS_TEST_CONNECTION_STRING"), token);
                    var pending = contender.RunInitialImportScenarioAsync(InitialImportScenario.CreationReplay, token);
                    var observed = false;
                    try
                    {
                        while (!observed)
                        {
                            token.ThrowIfCancellationRequested();
                            if (pending.IsCompleted) { await pending; throw new InvalidOperationException("Contender completed before owner rollback."); }
                            await using var command = new NpgsqlCommand("SELECT $1 = ANY(pg_catalog.pg_blocking_pids($2)) AND EXISTS (SELECT 1 FROM pg_catalog.pg_locks WHERE pid = $2 AND locktype = 'advisory' AND NOT granted)", connection, (NpgsqlTransaction)transaction.GetDbTransaction());
                            command.Parameters.AddWithValue(connection.ProcessID); command.Parameters.AddWithValue(contender.connection.ProcessID);
                            observed = (bool)(await command.ExecuteScalarAsync(token))!;
                            if (!observed) await Task.Delay(25, token);
                        }
                    }
                    finally { using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await transaction.RollbackAsync(releaseTimeout.Token); ownerReleased = true; }
                    var contenderResult = await pending.WaitAsync(token);
                    correct &= observed && contenderResult.BehaviorCorrect && contenderResult.AllRowsRolledBack && contenderResult.TrackingEmpty;
                }
                if (scenario is InitialImportScenario.DuplicateMappings or InitialImportScenario.AmbiguousMappings)
                {
                    var target = original.Id;
                    if (scenario == InitialImportScenario.AmbiguousMappings)
                    {
                        var canonicalSeason = await context.Seasons.SingleAsync(x => x.Id == original.SeasonId, token);
                        var memberships = await context.FootballSeasonTeams.Include(x => x.Team).Where(x => x.SeasonId == original.SeasonId).ToArrayAsync(token);
                        var other = new FootballMatch(Guid.NewGuid(), canonicalSeason, memberships.Single(x => x.TeamId == original.HomeTeamId), memberships.Single(x => x.TeamId == original.AwayTeamId));
                        context.Add(other); target = other.Id;
                    }
                    context.Add(new FootballMatchExternalIdentity(Guid.NewGuid(), match.Identity.ProviderKey, match.Identity.IdentityScope, match.Identity.ExternalId, target));
                    await context.SaveChangesAsync(token); context.ChangeTracker.Clear();
                    var rejected = await store.ApplyWithinTransactionAsync(batch, token);
                    var expected = scenario == InitialImportScenario.DuplicateMappings ? SportStats.Application.Football.IdentityResolution.IdentityResolutionFailureCode.DuplicateMappings : SportStats.Application.Football.IdentityResolution.IdentityResolutionFailureCode.AmbiguousMappings;
                    correct &= !rejected.Success && rejected.Conflicts.Any(x => x.Code == expected);
                    trackingEmpty = !context.ChangeTracker.Entries().Any();
                }
                if (scenario == InitialImportScenario.Constraints)
                {
                    async Task<bool> Reject(FormattableString sql, string state)
                    {
                        await transaction.CreateSavepointAsync("initial_constraint", token);
                        try { await context.Database.ExecuteSqlInterpolatedAsync(sql, token); return false; }
                        catch (PostgresException exception) when (exception.SqlState == state) { return true; }
                        finally { await transaction.RollbackToSavepointAsync("initial_constraint", token); await transaction.ReleaseSavepointAsync("initial_constraint", token); }
                    }
                    correct &= await Reject($"INSERT INTO platform.competitions (\"Id\", \"SportId\", \"Code\", \"Name\") VALUES ({Guid.NewGuid()}, {sportId}, {"PL"}, {"Duplicate"})", PostgresErrorCodes.UniqueViolation);
                    correct &= await Reject($"INSERT INTO football.match_external_ids (\"Id\", \"ProviderKey\", \"IdentityScope\", \"ExternalId\", \"MatchId\") VALUES ({Guid.NewGuid()}, {match.Identity.ProviderKey}, {match.Identity.IdentityScope}, {"dangling"}, {Guid.NewGuid()})", PostgresErrorCodes.ForeignKeyViolation);
                }
                if (scenario is not InitialImportScenario.DuplicateMappings and not InitialImportScenario.AmbiguousMappings and not InitialImportScenario.Concurrent)
                {
                var replayBatch = scenario == InitialImportScenario.Conflict
                    ? batch with { Matches = [batch.Matches[0] with { Match = match with { KickoffUtc = match.KickoffUtc.AddHours(1), Matchday = 2, HomeGoals = new(ScoreFieldPresence.Value, 7) } }] } : batch;
                var replay = await store.ApplyWithinTransactionAsync(replayBatch, token);
                var after = await context.FootballMatches.AsNoTracking().SingleAsync(x => x.Id == original.Id, token);
                var afterIds = await context.FootballMatchExternalIdentities.Where(x => x.MatchId == original.Id).Select(x => x.Id).ToArrayAsync(token);
                correct &= created.Success && !created.Applied && created.Counts.Single(x => x.Entity == "Matches").Created == 1 &&
                    after.KickoffUtc == original.KickoffUtc && after.Matchday == original.Matchday && after.Status == original.Status && after.HomeGoals == 0 && after.AwayGoals == 2 &&
                    ids.SequenceEqual(afterIds) &&
                    await context.FootballSeasonTeams.CountAsync(token) >= 2;
                correct &= scenario == InitialImportScenario.Conflict ? !replay.Success && replay.Conflicts.Count > 0 : replay.Success && replay.Counts.All(x => x.Created == 0);
                }
            }
            trackingEmpty = !context.ChangeTracker.Entries().Any();
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { if (!ownerReleased) await transaction.RollbackAsync(cleanup.Token); }
            catch { unusable = true; await connection.DisposeAsync(); throw new InvalidOperationException("Full import rollback could not be confirmed; fixture closed."); }
            finally { scenarioInterceptor.Deactivate(); context.ChangeTracker.Clear(); await transaction.DisposeAsync(); }
        }
        await VerifyOwnedSessionAsync(token);
        var removed = !await context.CompetitionExternalIdentities.AnyAsync(x => x.ProviderKey == competition.Identity.ProviderKey && x.IdentityScope == competition.Identity.IdentityScope && x.ExternalId == competition.Identity.ExternalId, token) &&
            !await context.Competitions.AnyAsync(x => x.SportId == sportId && x.Code == "PL", token) &&
            !await context.FootballMatchExternalIdentities.AnyAsync(x => x.ProviderKey == match.Identity.ProviderKey && x.IdentityScope == match.Identity.IdentityScope && x.ExternalId == match.Identity.ExternalId, token);
        var afterRows = await InitialImportRowsAsync(token);
        return new(correct, removed && beforeRows.SequenceEqual(afterRows), trackingEmpty);
    }

    private async Task<Guid[]> InitialImportRowsAsync(CancellationToken token)
    {
        var rows = new List<Guid>();
        rows.AddRange(await context.Competitions.Select(x => x.Id).ToArrayAsync(token));
        rows.AddRange(await context.Seasons.Select(x => x.Id).ToArrayAsync(token));
        rows.AddRange(await context.FootballTeams.Select(x => x.Id).ToArrayAsync(token));
        rows.AddRange(await context.FootballMatches.Select(x => x.Id).ToArrayAsync(token));
        rows.AddRange(await context.CompetitionExternalIdentities.Select(x => x.Id).ToArrayAsync(token));
        rows.AddRange(await context.SeasonExternalIdentities.Select(x => x.Id).ToArrayAsync(token));
        rows.AddRange(await context.FootballTeamExternalIdentities.Select(x => x.Id).ToArrayAsync(token));
        rows.AddRange(await context.FootballMatchExternalIdentities.Select(x => x.Id).ToArrayAsync(token));
        foreach (var membership in await context.FootballSeasonTeams.AsNoTracking().ToArrayAsync(token)) { rows.Add(membership.SeasonId); rows.Add(membership.TeamId); }
        return rows.Order().ToArray();
    }

    private sealed class InitialPreviewProbe : DbCommandInterceptor
    {
        public int Reads { get; private set; }
        private void Check(DbCommand command)
        {
            var sql = command.CommandText.TrimStart();
            if (!sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || sql.Contains("pg_advisory", StringComparison.OrdinalIgnoreCase) || sql.Contains("nextval", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Dry run attempted a write or write lock.");
            Reads++;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { Check(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) { Check(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default) { Check(command); return ValueTask.FromResult(result); }
    }
    private sealed class InitialPreviewSaveProbe : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Dry run attempted SaveChanges.");
    }
    private sealed class InitialFinalStageFailure : Exception { }
    private sealed class InitialFinalStageProbe(CancellationTokenSource? cancellation) : DbCommandInterceptor
    {
        private void Check(DbCommand command)
        {
            if (!command.CommandText.Replace("\"", "", StringComparison.Ordinal).TrimStart().StartsWith("INSERT INTO football.match_external_ids ", StringComparison.OrdinalIgnoreCase)) return;
            if (cancellation is not null) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            throw new InitialFinalStageFailure();
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { Check(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) { Check(command); return ValueTask.FromResult(result); }
    }
}
