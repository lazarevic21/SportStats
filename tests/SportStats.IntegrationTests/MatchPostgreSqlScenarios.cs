using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Football;
using SportStats.Domain.Platform;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.AdvisoryLocks;
using SportStats.Infrastructure.Persistence.IdentityResolution;

namespace SportStats.IntegrationTests;

public enum MatchScenario { Create, NullMetadata, TrustedAliases, DistinctIdentities, WrongSeason, WrongHome, WrongAway, MissingMembership, SameTeam, MissingSeason, MissingTeam, DuplicateMappings, AmbiguousMappings, Constraints, FailureRollback, CancellationRollback }
public sealed record MatchScenarioObservation(bool BehaviorCorrect, bool CallerTransactionPreserved, bool CallerWorkPreserved, bool TrackingEmpty, bool AllFixtureRowsRolledBack);

public sealed partial class PostgreSqlTestDatabaseFixture
{
    public async Task<MatchScenarioObservation> RunMatchScenarioAsync(MatchScenario scenario, CancellationToken token = default)
    {
        ValidateLiveOptIn(Environment.GetEnvironmentVariable("SPORTSTATS_RUN_POSTGRES_TESTS"));
        if (!Enum.IsDefined(scenario) || unusable) throw new InvalidOperationException("The guarded scenario is unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30)); token = timeout.Token;
        await VerifyOwnedSessionAsync(token);
        if (context.Database.CurrentTransaction is not null || context.ChangeTracker.Entries().Any()) throw new InvalidOperationException("A clean fixture session is required.");
        var competitionId = Guid.NewGuid(); var seasonId = Guid.NewGuid(); var otherSeasonId = Guid.NewGuid();
        var identity = new ProviderIdentity("fixture-match", Guid.NewGuid().ToString("N"), " opaque match ");
        var seasonIdentity = identity with { ExternalId = "season" };
        var homeIdentity = identity with { ExternalId = "home" }; var awayIdentity = identity with { ExternalId = "away" };
        var matchIds = new HashSet<Guid>(); var teamIds = new HashSet<Guid>();
        MatchScenarioObservation? observation = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        try
        {
            scenarioInterceptor.ActivateMatch(connection, transaction.GetDbTransaction(), () => VerifyOwnedSessionAsync(token));
            await new PostgreSqlAdvisoryLockAcquirer().AcquireAsync((NpgsqlTransaction)transaction.GetDbTransaction(), new FootballAdvisoryLockPlan([]), TimeSpan.FromSeconds(10), token);
            var sport = await context.Sports.AsTracking().SingleOrDefaultAsync(s => s.Code == "football", token) ?? throw new InvalidOperationException("Existing football sport required.");
            if (await context.Competitions.AnyAsync(c => c.SportId == sport.Id && c.Code == "PL", token)) throw new InvalidOperationException("Unoccupied football/PL scope required.");
            var competition = new Competition(competitionId, sport, "PL", "Caller-owned match parent");
            var season = new Season(seasonId, competition, "2026", new(2026, 8, 1), new(2027, 5, 31));
            var otherSeason = new Season(otherSeasonId, competition, "2027", new(2027, 8, 1), new(2028, 5, 31));
            var home = new FootballTeam(Guid.NewGuid(), "Home"); var away = new FootballTeam(Guid.NewGuid(), "Away"); var third = new FootballTeam(Guid.NewGuid(), "Third");
            teamIds.UnionWith([home.Id, away.Id, third.Id]);
            var homeMembership = new FootballSeasonTeam(season, home); var awayMembership = new FootballSeasonTeam(season, away);
            context.AddRange(season, otherSeason, homeMembership, third);
            if (scenario != MatchScenario.MissingMembership) context.Add(awayMembership); else context.Add(away);
            var otherHomeMembership = new FootballSeasonTeam(otherSeason, home); var otherAwayMembership = new FootballSeasonTeam(otherSeason, away);
            context.AddRange(otherHomeMembership, otherAwayMembership);
            context.Add(new SeasonExternalIdentity(Guid.NewGuid(), seasonIdentity.ProviderKey, seasonIdentity.IdentityScope, seasonIdentity.ExternalId, seasonId));
            context.Add(new FootballTeamExternalIdentity(Guid.NewGuid(), homeIdentity.ProviderKey, homeIdentity.IdentityScope, homeIdentity.ExternalId, home.Id));
            context.Add(new FootballTeamExternalIdentity(Guid.NewGuid(), awayIdentity.ProviderKey, awayIdentity.IdentityScope, awayIdentity.ExternalId, scenario == MatchScenario.SameTeam ? home.Id : away.Id));
            var seeded = scenario is MatchScenario.TrustedAliases or MatchScenario.DuplicateMappings or MatchScenario.AmbiguousMappings or MatchScenario.WrongSeason or MatchScenario.WrongHome or MatchScenario.WrongAway;
            var alias = identity with { ProviderKey = "trusted-other", IdentityScope = "trusted-scope" };
            if (seeded)
            {
                var storedSeason = scenario == MatchScenario.WrongSeason ? otherSeason : season;
                var storedHome = scenario == MatchScenario.WrongSeason ? otherHomeMembership : scenario == MatchScenario.WrongHome ? awayMembership : homeMembership;
                var storedAway = scenario == MatchScenario.WrongSeason ? otherAwayMembership : scenario == MatchScenario.WrongHome ? homeMembership : awayMembership;
                if (scenario == MatchScenario.WrongAway)
                {
                    var member = new FootballSeasonTeam(season, third); context.Add(member); storedAway = member;
                }
                var match = new FootballMatch(Guid.NewGuid(), storedSeason, storedHome, storedAway);
                context.Add(match); matchIds.Add(match.Id);
                context.Add(new FootballMatchExternalIdentity(Guid.NewGuid(), identity.ProviderKey, identity.IdentityScope, identity.ExternalId, match.Id));
                if (scenario is MatchScenario.TrustedAliases or MatchScenario.DuplicateMappings or MatchScenario.AmbiguousMappings)
                {
                    var target = match.Id;
                    if (scenario == MatchScenario.AmbiguousMappings)
                    {
                        var other = new FootballMatch(Guid.NewGuid(), season, homeMembership, awayMembership); context.Add(other); matchIds.Add(other.Id); target = other.Id;
                    }
                    var mappingIdentity = scenario == MatchScenario.TrustedAliases ? alias : identity;
                    context.Add(new FootballMatchExternalIdentity(Guid.NewGuid(), mappingIdentity.ProviderKey, mappingIdentity.IdentityScope, mappingIdentity.ExternalId, target));
                }
            }
            if (scenario == MatchScenario.NullMetadata)
            {
                var match = new FootballMatch(Guid.NewGuid(), season, homeMembership, awayMembership); context.Add(match); matchIds.Add(match.Id);
            }
            await context.SaveChangesAsync(token); context.ChangeTracker.Clear();
            var failure = scenario == MatchScenario.FailureRollback; var cancelled = scenario == MatchScenario.CancellationRollback;
            scenarioInterceptor.ObserveMatchResolution(failure, cancelled ? cancellation : null);
            if (failure || cancelled) context.Database.AutoSavepointsEnabled = false;
            var inputSeason = new NormalizedSeason(scenario == MatchScenario.MissingSeason ? seasonIdentity with { ExternalId = "absent-season" } : seasonIdentity, 2026, scenario == MatchScenario.MissingSeason ? "2028" : "2026", new(2026, 8, 1), new(2027, 5, 31));
            var input = new NormalizedMatch(identity, inputSeason, homeIdentity, scenario == MatchScenario.MissingTeam ? awayIdentity with { ExternalId = "absent-team" } : awayIdentity,
                new DateTime(2026, 9, 1, 18, 0, 0, DateTimeKind.Utc), FootballMatchStatus.Finished, "FINISHED", scenario == MatchScenario.NullMetadata ? null : 3,
                new(scenario == MatchScenario.NullMetadata ? ScoreFieldPresence.Missing : ScoreFieldPresence.Value, scenario == MatchScenario.NullMetadata ? null : 0),
                new(scenario == MatchScenario.NullMetadata ? ScoreFieldPresence.Null : ScoreFieldPresence.Value, scenario == MatchScenario.NullMetadata ? null : 2));
            var identities = scenario switch
            {
                MatchScenario.Create => new[] { identity, identity with { ExternalId = "second" } },
                MatchScenario.TrustedAliases => new[] { alias, identity },
                MatchScenario.DistinctIdentities => new[] { identity, identity with { ExternalId = "second" }, identity with { ProviderKey = "other" }, identity with { IdentityScope = identity.IdentityScope + "-other" } },
                _ => new[] { identity }
            };
            var batch = new IdentityResolutionBatch([], [], [], identities.Select(i => new MatchResolutionRequest(new EntityReference<Competition>.Internal(competitionId), input with { Identity = i })).ToArray());
            var orchestrator = new FootballResolutionOrchestrator(context, TimeSpan.FromSeconds(10)); var correct = false;
            if (failure || cancelled)
            {
                var expected = false;
                try { await orchestrator.PersistMatchesAsync(batch, cancellation.Token); }
                catch (DbUpdateException e) when (failure && e.InnerException is InjectedMatchMappingFailure) { expected = true; }
                catch (OperationCanceledException) when (cancelled && cancellation.IsCancellationRequested) { expected = true; }
                matchIds.UnionWith(scenarioInterceptor.InsertedMatchIds);
                correct = expected && scenarioInterceptor.EarlierInsertObserved && matchIds.Count > 0 &&
                    !await context.FootballMatches.AnyAsync(m => matchIds.Contains(m.Id), token) && !await context.FootballMatchExternalIdentities.AnyAsync(m => matchIds.Contains(m.MatchId), token);
            }
            else if (scenario is MatchScenario.DuplicateMappings or MatchScenario.AmbiguousMappings or MatchScenario.WrongSeason or MatchScenario.WrongHome or MatchScenario.WrongAway or MatchScenario.MissingMembership or MatchScenario.SameTeam or MatchScenario.MissingSeason or MatchScenario.MissingTeam)
            {
                var expected = scenario switch { MatchScenario.DuplicateMappings => IdentityResolutionFailureCode.DuplicateMappings, MatchScenario.AmbiguousMappings => IdentityResolutionFailureCode.AmbiguousMappings,
                    MatchScenario.MissingMembership or MatchScenario.MissingSeason or MatchScenario.MissingTeam => IdentityResolutionFailureCode.MissingReference, _ => IdentityResolutionFailureCode.RelationshipConflict };
                var result = await orchestrator.PersistMatchesAsync(batch, token);
                correct = result is IdentityResolutionBatchResult.Failed failed && failed.Failure.Code == expected && scenarioInterceptor.MatchWriteCount == 0;
            }
            else
            {
                var first = await orchestrator.PersistMatchesAsync(batch, token) as IdentityResolutionBatchResult.Resolved ?? throw new InvalidOperationException("Match resolution failed.");
                matchIds.UnionWith(first.Matches.Select(m => m.EntityId));
                var expectedCount = scenario == MatchScenario.TrustedAliases ? 1 : identities.Length;
                var mappings = await context.FootballMatchExternalIdentities.AsNoTracking().Where(m => matchIds.Contains(m.MatchId)).ToListAsync(token);
                var resolvedIds = first.Matches.Select(r => r.EntityId).ToArray();
                var stored = await context.FootballMatches.AsNoTracking().Where(m => resolvedIds.Contains(m.Id)).ToListAsync(token);
                correct = first.Matches.Select(m => m.Identity).SequenceEqual(identities) && first.Matches.Select(m => m.EntityId).Distinct().Count() == expectedCount && stored.Count == expectedCount && mappings.Count == identities.Length &&
                    first.Matches.All(r => mappings.Count(m => m.ProviderKey == r.Identity.ProviderKey && m.IdentityScope == r.Identity.IdentityScope && m.ExternalId == r.Identity.ExternalId && m.MatchId == r.EntityId) == 1);
                correct &= stored.All(m => m.SeasonId == seasonId && m.HomeTeamId == home.Id && m.AwayTeamId == away.Id);
                if (scenario != MatchScenario.TrustedAliases) correct &= stored.All(m => m.KickoffUtc == input.KickoffUtc && m.Status == input.Status && m.Matchday == input.Matchday && m.HomeGoals == input.HomeGoals.Value && m.AwayGoals == input.AwayGoals.Value) && scenarioInterceptor.EarlierInsertObserved;
                if (scenario == MatchScenario.NullMetadata)
                {
                    var direct = await context.FootballMatches.AsNoTracking().SingleAsync(m => matchIds.Contains(m.Id) && m.Id != first.Matches[0].EntityId, token);
                    correct &= direct.KickoffUtc is null && direct.Matchday is null && direct.HomeGoals is null && direct.AwayGoals is null;
                }
                var writes = scenarioInterceptor.MatchWriteCount;
                var exactReplay = await orchestrator.PersistMatchesAsync(batch, token) as IdentityResolutionBatchResult.Resolved;
                correct &= exactReplay is not null && exactReplay.Matches.Select(m => m.EntityId).SequenceEqual(first.Matches.Select(m => m.EntityId)) && exactReplay.Matches.All(m => m.Disposition == IdentityResolutionDisposition.ExistingMapping) && scenarioInterceptor.MatchWriteCount == writes && !context.ChangeTracker.Entries().Any();
                var replayBatch = batch with { Matches = batch.Matches.Select(r => r with { Match = r.Match with { KickoffUtc = input.KickoffUtc.AddDays(3), Status = FootballMatchStatus.Scheduled, Matchday = 99, HomeGoals = new(ScoreFieldPresence.Value, 7), AwayGoals = new(ScoreFieldPresence.Value, 8) } }).ToArray() };
                var replay = await orchestrator.PersistMatchesAsync(replayBatch, token) as IdentityResolutionBatchResult.Resolved;
                var after = await context.FootballMatches.AsNoTracking().Where(m => resolvedIds.Contains(m.Id)).ToListAsync(token);
                correct &= replay is not null && replay.Matches.Select(m => m.EntityId).SequenceEqual(first.Matches.Select(m => m.EntityId)) && replay.Matches.All(m => m.Disposition == IdentityResolutionDisposition.ExistingMapping) && scenarioInterceptor.MatchWriteCount == writes &&
                    after.All(m => { var before = stored.Single(s => s.Id == m.Id); return m.KickoffUtc == before.KickoffUtc && m.Status == before.Status && m.Matchday == before.Matchday && m.HomeGoals == before.HomeGoals && m.AwayGoals == before.AwayGoals; });
                if (scenario == MatchScenario.Constraints)
                {
                    async Task<bool> Constraint(FormattableString sql, string state)
                    {
                        await transaction.CreateSavepointAsync("match_constraint_probe", token);
                        try { await context.Database.ExecuteSqlInterpolatedAsync(sql, token); return false; }
                        catch (PostgresException e) when (e.SqlState == state) { return true; }
                        finally { await transaction.RollbackToSavepointAsync("match_constraint_probe", token); await transaction.ReleaseSavepointAsync("match_constraint_probe", token); }
                    }
                    correct &= await Constraint($"INSERT INTO football.matches (\"Id\", \"SeasonId\", \"HomeTeamId\", \"AwayTeamId\", \"Status\") VALUES ({Guid.NewGuid()}, {seasonId}, {home.Id}, {third.Id}, {"Unknown"})", PostgresErrorCodes.ForeignKeyViolation);
                    correct &= await Constraint($"INSERT INTO football.matches (\"Id\", \"SeasonId\", \"HomeTeamId\", \"AwayTeamId\", \"Status\") VALUES ({Guid.NewGuid()}, {seasonId}, {third.Id}, {away.Id}, {"Unknown"})", PostgresErrorCodes.ForeignKeyViolation);
                    correct &= await Constraint($"INSERT INTO football.matches (\"Id\", \"SeasonId\", \"HomeTeamId\", \"AwayTeamId\", \"Status\") VALUES ({Guid.NewGuid()}, {seasonId}, {home.Id}, {home.Id}, {"Unknown"})", PostgresErrorCodes.CheckViolation);
                    correct &= await Constraint($"INSERT INTO football.matches (\"Id\", \"SeasonId\", \"HomeTeamId\", \"AwayTeamId\", \"Status\", \"HomeGoals\") VALUES ({Guid.NewGuid()}, {seasonId}, {home.Id}, {away.Id}, {"Unknown"}, {-1})", PostgresErrorCodes.CheckViolation);
                    correct &= await Constraint($"INSERT INTO football.matches (\"Id\", \"SeasonId\", \"HomeTeamId\", \"AwayTeamId\", \"Status\", \"AwayGoals\") VALUES ({Guid.NewGuid()}, {seasonId}, {home.Id}, {away.Id}, {"Unknown"}, {-1})", PostgresErrorCodes.CheckViolation);
                    correct &= await Constraint($"INSERT INTO football.match_external_ids (\"Id\", \"ProviderKey\", \"IdentityScope\", \"ExternalId\", \"MatchId\") VALUES ({Guid.NewGuid()}, {identity.ProviderKey}, {identity.IdentityScope}, {"dangling"}, {Guid.NewGuid()})", PostgresErrorCodes.ForeignKeyViolation);
                }
            }
            observation = new(correct, ReferenceEquals(context.Database.CurrentTransaction, transaction) && transaction.GetDbTransaction().IsolationLevel == IsolationLevel.ReadCommitted,
                await context.Competitions.AsNoTracking().AnyAsync(c => c.Id == competitionId && c.Name == "Caller-owned match parent", token) && await context.Seasons.CountAsync(s => s.CompetitionId == competitionId, token) == 2 && await context.FootballSeasonTeams.CountAsync(m => m.SeasonId == otherSeasonId, token) == 2,
                !context.ChangeTracker.Entries().Any(), false);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await transaction.RollbackAsync(cleanup.Token); cleanup.Token.ThrowIfCancellationRequested(); }
            catch { unusable = true; await connection.DisposeAsync(); throw new InvalidOperationException("Full match rollback could not be confirmed; fixture closed."); }
            finally { scenarioInterceptor.Deactivate(); context.ChangeTracker.Clear(); context.Database.AutoSavepointsEnabled = true; await transaction.DisposeAsync(); }
        }
        await VerifyOwnedSessionAsync(token);
        var removed = !await context.Competitions.AnyAsync(c => c.Id == competitionId, token) && !await context.Seasons.AnyAsync(s => s.CompetitionId == competitionId, token) &&
            !await context.SeasonExternalIdentities.AnyAsync(m => m.SeasonId == seasonId, token) && !await context.FootballTeams.AnyAsync(t => teamIds.Contains(t.Id), token) &&
            !await context.FootballTeamExternalIdentities.AnyAsync(m => teamIds.Contains(m.TeamId), token) && !await context.FootballSeasonTeams.AnyAsync(m => m.SeasonId == seasonId || m.SeasonId == otherSeasonId, token) &&
            !await context.FootballMatches.AnyAsync(m => matchIds.Contains(m.Id), token) && !await context.FootballMatchExternalIdentities.AnyAsync(m => matchIds.Contains(m.MatchId) || (m.ProviderKey == identity.ProviderKey && m.IdentityScope == identity.IdentityScope), token);
        return observation! with { AllFixtureRowsRolledBack = removed };
    }
}





