using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Application.Football.MatchMetadata;
using SportStats.Domain.Football;
using SportStats.Domain.Platform;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.MatchMetadata;

namespace SportStats.IntegrationTests;

public enum MetadataEvaluationScenario
{
    Unchanged, Changes, Missing, Null, ZeroGoals, TrustedAliases, ConflictingAliases,
    DuplicateMappings, AmbiguousMappings, MissingMatchMapping, MissingSeasonMapping, MissingTeamMapping,
    WrongSeason, WrongHome, WrongAway, OrderedBatch, HeterogeneousBatch, Cancellation, QueryCancellation, DatabaseFailure,
    DanglingMatchConstraint, MissingRelationshipConstraint, UnsupportedStatus
}
public sealed record MetadataEvaluationScenarioObservation(IReadOnlyList<MatchMetadataEvaluation> Evaluations,
    bool BehaviorCorrect, bool TrackingEmpty, bool NoWrites, bool CallerTransactionPreserved,
    bool AllFixtureRowsRolledBack, int ReadQueryCount, int MatchMappingCandidateRows = 0, int ExactMatchMappingRows = 0)
{
    public bool NoSaveChanges => NoWrites;
}

public sealed partial class PostgreSqlTestDatabaseFixture
{
    public async Task<MetadataEvaluationScenarioObservation> RunMetadataEvaluationScenarioAsync(
        MetadataEvaluationScenario scenario, CancellationToken token = default)
    {
        ValidateLiveOptIn(Environment.GetEnvironmentVariable("SPORTSTATS_RUN_POSTGRES_TESTS"));
        if (!Enum.IsDefined(scenario) || unusable) throw new InvalidOperationException("The guarded scenario is unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30)); token = timeout.Token;
        await VerifyOwnedSessionAsync(token);
        if (context.Database.CurrentTransaction is not null || context.ChangeTracker.Entries().Any())
            throw new InvalidOperationException("A clean fixture session is required.");
        var competitionId = Guid.NewGuid(); var seasonId = Guid.NewGuid(); var otherSeasonId = Guid.NewGuid();
        var homeId = Guid.NewGuid(); var awayId = Guid.NewGuid(); var matchId = Guid.NewGuid(); var otherMatchId = Guid.NewGuid();
        var identity = new ProviderIdentity("fixture-metadata", Guid.NewGuid().ToString("N"), " opaque match ");
        var seasonIdentity = identity with { ExternalId = "season" };
        var homeIdentity = identity with { ExternalId = "home" }; var awayIdentity = identity with { ExternalId = "away" };
        var alias = identity with { ProviderKey = "trusted-alias", IdentityScope = identity.IdentityScope + "-alias", ExternalId = "alias-match" };
        var kickoff = new DateTime(2026, 9, 1, 18, 0, 0, DateTimeKind.Utc);
        MetadataEvaluationScenarioObservation? result = null;
        var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        try
        {
            scenarioInterceptor.ActivateMatch(connection, transaction.GetDbTransaction(), () => VerifyOwnedSessionAsync(token));
            var sport = await context.Sports.AsTracking().SingleAsync(s => s.Code == "football", token);
            if (await context.Competitions.AnyAsync(c => c.SportId == sport.Id && c.Code == "PL", token))
                throw new InvalidOperationException("Unoccupied football/PL scope required.");
            var competition = new Competition(competitionId, sport, "PL", "Caller-owned metadata parent");
            var season = new Season(seasonId, competition, "2026", new(2026, 8, 1), new(2027, 5, 31));
            var otherSeason = new Season(otherSeasonId, competition, "2027", new(2027, 8, 1), new(2028, 5, 31));
            var home = new FootballTeam(homeId, "Home"); var away = new FootballTeam(awayId, "Away");
            var homeMembership = new FootballSeasonTeam(season, home); var awayMembership = new FootballSeasonTeam(season, away);
            var match = new FootballMatch(matchId, season, homeMembership, awayMembership, kickoff, FootballMatchStatus.Finished, 3, new FootballScore(0, 2));
            context.AddRange(otherSeason, homeMembership, awayMembership, match);
            context.AddRange(new SeasonExternalIdentity(Guid.NewGuid(), identity.ProviderKey, identity.IdentityScope, "season", seasonId),
                new SeasonExternalIdentity(Guid.NewGuid(), identity.ProviderKey, identity.IdentityScope, "other-season", otherSeasonId),
                new FootballTeamExternalIdentity(Guid.NewGuid(), identity.ProviderKey, identity.IdentityScope, "home", homeId),
                new FootballTeamExternalIdentity(Guid.NewGuid(), identity.ProviderKey, identity.IdentityScope, "away", awayId));
            void Mapping(ProviderIdentity key, Guid target) => context.Add(new FootballMatchExternalIdentity(Guid.NewGuid(), key.ProviderKey, key.IdentityScope, key.ExternalId, target));
            Mapping(identity, matchId); Mapping(alias, matchId);
            if (scenario == MetadataEvaluationScenario.DuplicateMappings) Mapping(identity, matchId);
            if (scenario == MetadataEvaluationScenario.AmbiguousMappings)
            {
                context.Add(new FootballMatch(otherMatchId, season, homeMembership, awayMembership)); Mapping(identity, otherMatchId);
            }
            // This crossed tuple matches every SQL dimension but neither requested complete identity.
            if (scenario == MetadataEvaluationScenario.HeterogeneousBatch)
                Mapping(identity with { ExternalId = alias.ExternalId }, matchId);
            await context.SaveChangesAsync(token); context.ChangeTracker.Clear();
            scenarioInterceptor.ObserveMatchResolution(false);
            var input = new MatchProviderObservation(identity, new(identity.ProviderKey, "fixture"),
                new(seasonIdentity, 2026, "2026", new(2026, 8, 1), new(2027, 5, 31)), homeIdentity, awayIdentity,
                MatchObservationField<DateTime>.FromValue(kickoff), MatchObservationField<int>.FromValue(3),
                MatchObservationField<FootballMatchStatus>.FromValue(FootballMatchStatus.Finished), "FINISHED",
                MatchObservationField<int>.FromValue(0), MatchObservationField<int>.FromValue(2), default, kickoff.AddDays(1));
            input = scenario switch
            {
                MetadataEvaluationScenario.Changes => input with { KickoffUtc = MatchObservationField<DateTime>.FromValue(kickoff.AddHours(1)), Matchday = MatchObservationField<int>.FromValue(4), Status = MatchObservationField<FootballMatchStatus>.FromValue(FootballMatchStatus.Scheduled), HomeGoals = MatchObservationField<int>.FromValue(1), AwayGoals = MatchObservationField<int>.FromValue(3) },
                MetadataEvaluationScenario.Missing => input with { KickoffUtc = default, Matchday = default, Status = default, HomeGoals = default, AwayGoals = default },
                MetadataEvaluationScenario.Null => input with { KickoffUtc = MatchObservationField<DateTime>.Null, Matchday = MatchObservationField<int>.Null, HomeGoals = MatchObservationField<int>.Null, AwayGoals = MatchObservationField<int>.Null },
                MetadataEvaluationScenario.MissingMatchMapping => input with { Identity = identity with { ExternalId = "missing" } },
                MetadataEvaluationScenario.MissingSeasonMapping => input with { Season = input.Season with { Identity = seasonIdentity with { ExternalId = "missing" } } },
                MetadataEvaluationScenario.MissingTeamMapping => input with { HomeTeam = homeIdentity with { ExternalId = "missing" } },
                MetadataEvaluationScenario.WrongSeason => input with { Season = input.Season with { Identity = seasonIdentity with { ExternalId = "other-season" } } },
                MetadataEvaluationScenario.WrongHome => input with { HomeTeam = awayIdentity },
                MetadataEvaluationScenario.WrongAway => input with { AwayTeam = homeIdentity },
                MetadataEvaluationScenario.UnsupportedStatus => input with { Status = MatchObservationField<FootballMatchStatus>.FromValue(FootballMatchStatus.Unknown), RawProviderStatus = "FUTURE_STATUS" },
                _ => input
            };
            MatchProviderObservation[] inputs = scenario switch
            {
                MetadataEvaluationScenario.TrustedAliases or MetadataEvaluationScenario.HeterogeneousBatch => [input with { Identity = alias, Source = new(alias.ProviderKey, "fixture") }, input, input],
                MetadataEvaluationScenario.ConflictingAliases => [input, input with { Identity = alias, Source = new(alias.ProviderKey, "fixture"), HomeGoals = MatchObservationField<int>.FromValue(9) }],
                MetadataEvaluationScenario.OrderedBatch => [input with { Identity = identity with { ExternalId = "missing" } }, input with { Identity = alias, Source = new(alias.ProviderKey, "fixture") }, input],
                _ => [input]
            };
            using var queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var probe = new MetadataReadProbe(scenario == MetadataEvaluationScenario.DatabaseFailure,
                scenario == MetadataEvaluationScenario.QueryCancellation ? queryCancellation : null);
            var saveProbe = new MetadataSaveProbe();
            var options = new DbContextOptionsBuilder<SportStatsDbContext>(CreateScenarioOptions(connection, scenarioInterceptor))
                .AddInterceptors(probe, saveProbe).Options;
            await using var readContext = new SportStatsDbContext(options);
            await readContext.Database.UseTransactionAsync(transaction.GetDbTransaction(), token);
            var service = new MatchMetadataEvaluationService(new EfMatchMetadataIdentityReader(readContext), new MatchMetadataChangeEvaluator());
            IReadOnlyList<MatchMetadataEvaluation> evaluations = [];
            var correct = false;
            var candidateRows = 0; var exactRows = 0;
            if (scenario is MetadataEvaluationScenario.DanglingMatchConstraint or MetadataEvaluationScenario.MissingRelationshipConstraint)
            {
                await transaction.CreateSavepointAsync("metadata_constraint", token);
                try
                {
                    if (scenario == MetadataEvaluationScenario.DanglingMatchConstraint)
                        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO football.match_external_ids (\"Id\", \"ProviderKey\", \"IdentityScope\", \"ExternalId\", \"MatchId\") VALUES ({Guid.NewGuid()}, {identity.ProviderKey}, {identity.IdentityScope}, {"dangling"}, {Guid.NewGuid()})", token);
                    else
                        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO football.matches (\"Id\", \"SeasonId\", \"HomeTeamId\", \"AwayTeamId\", \"Status\") VALUES ({Guid.NewGuid()}, {Guid.NewGuid()}, {homeId}, {awayId}, {"Unknown"})", token);
                }
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation) { correct = true; }
                finally { await transaction.RollbackToSavepointAsync("metadata_constraint", token); await transaction.ReleaseSavepointAsync("metadata_constraint", token); }
            }
            else if (scenario == MetadataEvaluationScenario.Cancellation)
            {
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                try { await service.EvaluateBatchAsync(inputs, cancelled.Token); }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { correct = probe.ReadCount == 0; }
            }
            else if (scenario == MetadataEvaluationScenario.QueryCancellation)
            {
                await transaction.CreateSavepointAsync("metadata_cancel", token);
                try { await service.EvaluateBatchAsync(inputs, queryCancellation.Token); }
                catch (OperationCanceledException) when (queryCancellation.IsCancellationRequested) { correct = probe.ReadCount == 1; }
                finally { await transaction.RollbackToSavepointAsync("metadata_cancel", token); await transaction.ReleaseSavepointAsync("metadata_cancel", token); }
            }
            else if (scenario == MetadataEvaluationScenario.DatabaseFailure)
            {
                await transaction.CreateSavepointAsync("metadata_error", token);
                try { await service.EvaluateBatchAsync(inputs, token); }
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.DivisionByZero) { correct = true; }
                finally { await transaction.RollbackToSavepointAsync("metadata_error", token); await transaction.ReleaseSavepointAsync("metadata_error", token); }
            }
            else
            {
                evaluations = await service.EvaluateBatchAsync(inputs, token);
                correct = evaluations.Count == inputs.Length && evaluations.All(e => !e.IsUpdateAuthorized && e.Freshness == MatchMetadataFreshness.FreshnessUnverified);
                if (scenario == MetadataEvaluationScenario.Changes)
                {
                    correct &= evaluations.Single().Changes.Count == 5 &&
                        evaluations.Single().Changes.Contains(new MatchMetadataChange<DateTime>(MatchMetadataField.KickoffUtc, MatchObservationFieldPresence.Value, kickoff, kickoff.AddHours(1))) &&
                        evaluations.Single().Changes.Contains(new MatchMetadataChange<int>(MatchMetadataField.Matchday, MatchObservationFieldPresence.Value, 3, 4)) &&
                        evaluations.Single().Changes.Contains(new MatchMetadataChange<FootballMatchStatus>(MatchMetadataField.Status, MatchObservationFieldPresence.Value, FootballMatchStatus.Finished, FootballMatchStatus.Scheduled)) &&
                        evaluations.Single().Changes.Contains(new MatchMetadataChange<int>(MatchMetadataField.HomeGoals, MatchObservationFieldPresence.Value, 0, 1)) &&
                        evaluations.Single().Changes.Contains(new MatchMetadataChange<int>(MatchMetadataField.AwayGoals, MatchObservationFieldPresence.Value, 2, 3));
                }
                if (scenario == MetadataEvaluationScenario.Null)
                {
                    correct &= evaluations.Single().Changes.Count == 4 &&
                        evaluations.Single().Changes.Contains(new MatchMetadataChange<DateTime>(MatchMetadataField.KickoffUtc, MatchObservationFieldPresence.Null, kickoff, null)) &&
                        evaluations.Single().Changes.Contains(new MatchMetadataChange<int>(MatchMetadataField.Matchday, MatchObservationFieldPresence.Null, 3, null)) &&
                        evaluations.Single().Changes.Contains(new MatchMetadataChange<int>(MatchMetadataField.HomeGoals, MatchObservationFieldPresence.Null, 0, null)) &&
                        evaluations.Single().Changes.Contains(new MatchMetadataChange<int>(MatchMetadataField.AwayGoals, MatchObservationFieldPresence.Null, 2, null));
                }
                if (scenario is MetadataEvaluationScenario.Missing or MetadataEvaluationScenario.ZeroGoals)
                    correct &= evaluations.Single().Disposition == MatchMetadataEvaluationDisposition.Unchanged && evaluations.Single().Changes.Count == 0;
                if (scenario == MetadataEvaluationScenario.HeterogeneousBatch)
                {
                    probe.Active = false;
                    var snapshot = await new EfMatchMetadataIdentityReader(readContext).LoadAsync(inputs, token);
                    exactRows = snapshot.Mappings.Count(m => m.Kind == MatchMetadataIdentityKind.Match);
                    var providers = inputs.Select(x => x.Identity.ProviderKey).Distinct().ToArray();
                    var scopes = inputs.Select(x => x.Identity.IdentityScope).Distinct().ToArray();
                    var externalIds = inputs.Select(x => x.Identity.ExternalId).Distinct().ToArray();
                    candidateRows = await readContext.FootballMatchExternalIdentities.AsNoTracking()
                        .CountAsync(x => providers.Contains(x.ProviderKey) && scopes.Contains(x.IdentityScope) && externalIds.Contains(x.ExternalId), token);
                    correct &= exactRows == 2 && candidateRows == 3;
                }
            }
            probe.Active = false;
            var after = await context.FootballMatches.AsNoTracking().SingleAsync(m => m.Id == matchId, token);
            correct &= after.KickoffUtc == kickoff && after.Matchday == 3 && after.Status == FootballMatchStatus.Finished && after.HomeGoals == 0 && after.AwayGoals == 2 &&
                after.SeasonId == seasonId && after.HomeTeamId == homeId && after.AwayTeamId == awayId;
            result = new(evaluations, correct, !readContext.ChangeTracker.Entries().Any() && !context.ChangeTracker.Entries().Any(),
                probe.WriteAttempts == 0 && saveProbe.Attempts == 0 && scenarioInterceptor.MatchWriteCount == 0,
                ReferenceEquals(context.Database.CurrentTransaction, transaction), false, probe.ReadCount, candidateRows, exactRows);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await transaction.RollbackAsync(cleanup.Token); cleanup.Token.ThrowIfCancellationRequested(); }
            catch { unusable = true; await connection.DisposeAsync(); throw new InvalidOperationException("Full metadata rollback could not be confirmed; fixture closed."); }
            finally { scenarioInterceptor.Deactivate(); context.ChangeTracker.Clear(); await transaction.DisposeAsync(); }
        }
        await VerifyOwnedSessionAsync(token);
        var removed = !await context.Competitions.AnyAsync(c => c.Id == competitionId, token) &&
            !await context.Seasons.AnyAsync(s => s.Id == seasonId || s.Id == otherSeasonId, token) &&
            !await context.SeasonExternalIdentities.AnyAsync(m => m.SeasonId == seasonId || m.SeasonId == otherSeasonId, token) &&
            !await context.FootballTeams.AnyAsync(t => t.Id == homeId || t.Id == awayId, token) &&
            !await context.FootballSeasonTeams.AnyAsync(t => t.SeasonId == seasonId, token) &&
            !await context.FootballTeamExternalIdentities.AnyAsync(m => m.TeamId == homeId || m.TeamId == awayId, token) &&
            !await context.FootballMatches.AnyAsync(m => m.Id == matchId || m.Id == otherMatchId, token) &&
            !await context.FootballMatchExternalIdentities.AnyAsync(m => m.IdentityScope == identity.IdentityScope || m.IdentityScope == alias.IdentityScope, token);
        return result! with { AllFixtureRowsRolledBack = removed };
    }

    // Evaluation gets a separate context in the same rollback-only transaction. All EF reads execute
    // on PostgreSQL; attempted writes and even a no-op SaveChanges are forbidden during evaluation.
    private sealed class MetadataSaveProbe : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Attempts++; throw new InvalidOperationException("Evaluation must not save changes."); }
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        { Attempts++; throw new InvalidOperationException("Evaluation must not save changes."); }
    }
    private sealed class MetadataReadProbe(bool fail, CancellationTokenSource? queryCancellation) : DbCommandInterceptor
    {
        public bool Active { get; set; } = true;
        public int ReadCount { get; private set; }
        public int WriteAttempts { get; private set; }
        private void Check(DbCommand command)
        {
            if (!Active) return;
            if (!command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            { WriteAttempts++; throw new InvalidOperationException("Evaluation must only read."); }
            ReadCount++;
            if (fail) command.CommandText = "SELECT 1 / 0";
            if (queryCancellation is not null)
            {
                command.CommandText = "SELECT pg_sleep(5)";
                queryCancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            }
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Check(command); return ValueTask.FromResult(result); }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Check(command); return result; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Check(command); return ValueTask.FromResult(result); }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        { Check(command); return result; }
    }
}
