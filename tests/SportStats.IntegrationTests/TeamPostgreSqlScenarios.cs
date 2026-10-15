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

public enum TeamScenario { CreateInternal, CreateExternal, ReuseAcrossSeasons, TrustedAliases, DistinctIdentities, DuplicateMappings, AmbiguousMappings, Constraints, FailureRollback, CancellationRollback }
public sealed record TeamScenarioObservation(bool BehaviorCorrect, bool CallerTransactionPreserved,
    bool CallerWorkPreserved, bool TrackingEmpty, bool AllFixtureRowsRolledBack);

public sealed partial class PostgreSqlTestDatabaseFixture
{
    public async Task<TeamScenarioObservation> RunTeamScenarioAsync(TeamScenario scenario, CancellationToken token = default)
    {
        ValidateLiveOptIn(Environment.GetEnvironmentVariable("SPORTSTATS_RUN_POSTGRES_TESTS"));
        if (!Enum.IsDefined(scenario) || unusable) throw new InvalidOperationException("The guarded scenario is unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        token = timeout.Token;
        await VerifyOwnedSessionAsync(token);
        if (context.Database.CurrentTransaction is not null || context.ChangeTracker.Entries().Any())
            throw new InvalidOperationException("Scenarios require a clean fixture-owned session.");
        var competitionId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var nextSeasonId = Guid.NewGuid();
        var identity = new ProviderIdentity("fixture-team", Guid.NewGuid().ToString("N"), " 000team opaque ");
        var seasonIdentity = identity with { ExternalId = "season" };
        var teamIds = new HashSet<Guid>();
        TeamScenarioObservation? observation = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        try
        {
            scenarioInterceptor.ActivateTeam(connection, transaction.GetDbTransaction(), () => VerifyOwnedSessionAsync(token));
            await new PostgreSqlAdvisoryLockAcquirer().AcquireAsync((NpgsqlTransaction)transaction.GetDbTransaction(),
                new FootballAdvisoryLockPlan([]), TimeSpan.FromSeconds(10), token);
            var sport = await context.Sports.AsTracking().SingleOrDefaultAsync(s => s.Code == "football", token)
                ?? throw new InvalidOperationException("sportstats_test requires an existing football Sport.");
            if (await context.Competitions.AnyAsync(c => c.SportId == sport.Id && c.Code == "PL", token))
                throw new InvalidOperationException("sportstats_test must have an unoccupied football/PL scope.");
            var competition = new Competition(competitionId, sport, "PL", "Caller-owned team parent");
            var season = new Season(seasonId, competition, "2026", new(2026, 8, 1), new(2027, 5, 31));
            context.Add(season);
            context.Add(new Season(nextSeasonId, competition, "2027", new(2027, 8, 1), new(2028, 5, 31)));
            context.Add(new SeasonExternalIdentity(Guid.NewGuid(), seasonIdentity.ProviderKey, seasonIdentity.IdentityScope, seasonIdentity.ExternalId, seasonId));
            if (scenario is TeamScenario.TrustedAliases or TeamScenario.DuplicateMappings or TeamScenario.AmbiguousMappings)
            {
                var team = new FootballTeam(Guid.NewGuid(), "Original", "OLD", "old-crest");
                teamIds.Add(team.Id); context.Add(team);
                context.Add(new FootballTeamExternalIdentity(Guid.NewGuid(), identity.ProviderKey, identity.IdentityScope, identity.ExternalId, team.Id));
                var target = team.Id;
                if (scenario == TeamScenario.AmbiguousMappings)
                {
                    var other = new FootballTeam(Guid.NewGuid(), "Original");
                    target = other.Id; teamIds.Add(other.Id); context.Add(other);
                }
                var alias = scenario == TeamScenario.TrustedAliases ? identity with { ProviderKey = "trusted-other", IdentityScope = "trusted-scope" } : identity;
                context.Add(new FootballTeamExternalIdentity(Guid.NewGuid(), alias.ProviderKey, alias.IdentityScope, alias.ExternalId, target));
            }
            await context.SaveChangesAsync(token);
            context.ChangeTracker.Clear();
            var failure = scenario == TeamScenario.FailureRollback;
            var cancelled = scenario == TeamScenario.CancellationRollback;
            scenarioInterceptor.ObserveTeamResolution(failure, cancelled ? cancellation : null);
            if (failure || cancelled) context.Database.AutoSavepointsEnabled = false;
            EntityReference<Season> reference = scenario == TeamScenario.CreateExternal
                ? new EntityReference<Season>.External(seasonIdentity) : new EntityReference<Season>.Internal(seasonId);
            var input = new NormalizedTeam(identity, "Same name", "TM", "original-crest");
            IdentityResolutionBatch Batch(EntityReference<Season> parent, params ProviderIdentity[] ids) => new([], [],
                ids.Select(i => new TeamResolutionRequest(parent, input with { Identity = i })).ToArray(), []);
            var identities = scenario switch
            {
                TeamScenario.CreateInternal or TeamScenario.CreateExternal => new[] { identity, identity with { ExternalId = "second" } },
                TeamScenario.TrustedAliases => new[] { identity with { ProviderKey = "trusted-other", IdentityScope = "trusted-scope" }, identity },
                TeamScenario.DistinctIdentities => new[] { identity, identity with { ExternalId = "second" }, identity with { ProviderKey = "other" }, identity with { IdentityScope = identity.IdentityScope + "-other" } },
                _ => new[] { identity }
            };
            var batch = Batch(reference, identities);
            var orchestrator = new FootballResolutionOrchestrator(context, TimeSpan.FromSeconds(10));
            var correct = false;
            if (failure || cancelled)
            {
                var expected = false;
                try { await orchestrator.PersistTeamsAsync(batch, cancellation.Token); }
                catch (DbUpdateException e) when (failure && e.InnerException is InjectedTeamMappingFailure) { expected = true; }
                catch (OperationCanceledException) when (cancelled && cancellation.IsCancellationRequested) { expected = true; }
                teamIds.UnionWith(scenarioInterceptor.InsertedTeamIds);
                correct = expected && scenarioInterceptor.EarlierInsertObserved && teamIds.Count > 0 &&
                    !await context.FootballTeams.AnyAsync(t => teamIds.Contains(t.Id), token) &&
                    !await context.FootballTeamExternalIdentities.AnyAsync(m => teamIds.Contains(m.TeamId), token) &&
                    !await context.FootballSeasonTeams.AnyAsync(m => m.SeasonId == seasonId, token);
            }
            else if (scenario is TeamScenario.DuplicateMappings or TeamScenario.AmbiguousMappings)
            {
                var result = await orchestrator.PersistTeamsAsync(batch, token);
                correct = result is IdentityResolutionBatchResult.Failed failed && failed.Failure.Code ==
                    (scenario == TeamScenario.DuplicateMappings ? IdentityResolutionFailureCode.DuplicateMappings : IdentityResolutionFailureCode.AmbiguousMappings) && scenarioInterceptor.TeamWriteCount == 0;
            }
            else
            {
                var first = await orchestrator.PersistTeamsAsync(batch, token) as IdentityResolutionBatchResult.Resolved
                    ?? throw new InvalidOperationException("Team creation or resolution failed.");
                teamIds.UnionWith(first.Teams.Select(t => t.EntityId));
                var expectedCount = scenario == TeamScenario.TrustedAliases ? 1 : identities.Length;
                correct = first.Teams.Select(t => t.Identity).SequenceEqual(identities) && teamIds.Count == expectedCount &&
                    await context.FootballTeams.CountAsync(t => teamIds.Contains(t.Id), token) == expectedCount &&
                    await context.FootballTeamExternalIdentities.CountAsync(m => teamIds.Contains(m.TeamId), token) == identities.Length &&
                    await context.FootballSeasonTeams.CountAsync(m => m.SeasonId == seasonId && teamIds.Contains(m.TeamId), token) == expectedCount;
                var mappings = await context.FootballTeamExternalIdentities.AsNoTracking().Where(m => teamIds.Contains(m.TeamId)).ToListAsync(token);
                correct &= first.Teams.All(t => mappings.Count(m => m.ProviderKey == t.Identity.ProviderKey && m.IdentityScope == t.Identity.IdentityScope && m.ExternalId == t.Identity.ExternalId && m.TeamId == t.EntityId) == 1);
                correct &= scenario == TeamScenario.TrustedAliases
                    ? first.Teams.All(t => t.Disposition == IdentityResolutionDisposition.ExistingMapping)
                    : first.Teams.All(t => t.Disposition == IdentityResolutionDisposition.CreatedEntity) && scenarioInterceptor.EarlierInsertObserved;
                var writes = scenarioInterceptor.TeamWriteCount;
                var exactReplay = await orchestrator.PersistTeamsAsync(batch, token) as IdentityResolutionBatchResult.Resolved;
                correct &= exactReplay is not null && exactReplay.Teams.Select(t => t.EntityId).SequenceEqual(first.Teams.Select(t => t.EntityId)) &&
                    exactReplay.Teams.All(t => t.Disposition == IdentityResolutionDisposition.ExistingMapping) && scenarioInterceptor.TeamWriteCount == writes && !context.ChangeTracker.Entries().Any();
                var changed = batch with { Teams = batch.Teams.Select(t => t with { Team = t.Team with { Name = "Changed", ShortName = "NEW", CrestUrl = null } }).ToArray() };
                var replay = await orchestrator.PersistTeamsAsync(changed, token) as IdentityResolutionBatchResult.Resolved;
                var stored = await context.FootballTeams.AsNoTracking().Where(t => teamIds.Contains(t.Id)).ToListAsync(token);
                correct &= replay is not null && replay.Teams.Select(t => t.EntityId).SequenceEqual(first.Teams.Select(t => t.EntityId)) &&
                    replay.Teams.All(t => t.Disposition == IdentityResolutionDisposition.ExistingMapping) && scenarioInterceptor.TeamWriteCount == writes &&
                    stored.All(t => scenario == TeamScenario.TrustedAliases ? t.Name == "Original" && t.ShortName == "OLD" && t.CrestUrl == "old-crest" : t.Name == input.Name && t.ShortName == input.ShortName && t.CrestUrl == input.CrestUrl);
                if (scenario == TeamScenario.ReuseAcrossSeasons)
                {
                    var second = await orchestrator.PersistTeamsAsync(Batch(new EntityReference<Season>.Internal(nextSeasonId), identity), token) as IdentityResolutionBatchResult.Resolved;
                    correct &= second?.Teams.Single().EntityId == first.Teams.Single().EntityId &&
                        await context.FootballTeams.CountAsync(t => teamIds.Contains(t.Id), token) == 1 &&
                        await context.FootballTeamExternalIdentities.CountAsync(m => teamIds.Contains(m.TeamId), token) == 1 &&
                        await context.FootballSeasonTeams.CountAsync(m => teamIds.Contains(m.TeamId), token) == 2;
                }
                if (scenario == TeamScenario.Constraints)
                {
                    var id = first.Teams.Single().EntityId;
                    async Task<bool> Constraint(FormattableString sql, string state)
                    {
                        await transaction.CreateSavepointAsync("constraint_probe", token);
                        try { await context.Database.ExecuteSqlInterpolatedAsync(sql, token); return false; }
                        catch (PostgresException e) when (e.SqlState == state) { return true; }
                        finally { await transaction.RollbackToSavepointAsync("constraint_probe", token); await transaction.ReleaseSavepointAsync("constraint_probe", token); }
                    }
                    correct &= await Constraint($"INSERT INTO football.season_teams (\"SeasonId\", \"TeamId\") VALUES ({seasonId}, {id})", PostgresErrorCodes.UniqueViolation);
                    correct &= await Constraint($"INSERT INTO football.season_teams (\"SeasonId\", \"TeamId\") VALUES ({Guid.NewGuid()}, {id})", PostgresErrorCodes.ForeignKeyViolation);
                    correct &= await Constraint($"INSERT INTO football.season_teams (\"SeasonId\", \"TeamId\") VALUES ({seasonId}, {Guid.NewGuid()})", PostgresErrorCodes.ForeignKeyViolation);
                    correct &= await Constraint($"INSERT INTO football.team_external_ids (\"Id\", \"ProviderKey\", \"IdentityScope\", \"ExternalId\", \"TeamId\") VALUES ({Guid.NewGuid()}, {identity.ProviderKey}, {identity.IdentityScope}, {"dangling"}, {Guid.NewGuid()})", PostgresErrorCodes.ForeignKeyViolation);
                }
            }
            observation = new(correct, ReferenceEquals(context.Database.CurrentTransaction, transaction) && transaction.GetDbTransaction().IsolationLevel == IsolationLevel.ReadCommitted,
                await context.Competitions.AsNoTracking().AnyAsync(c => c.Id == competitionId && c.Name == "Caller-owned team parent", token) &&
                await context.Seasons.CountAsync(s => s.CompetitionId == competitionId, token) == 2 &&
                await context.SeasonExternalIdentities.AnyAsync(m => m.SeasonId == seasonId && m.ExternalId == seasonIdentity.ExternalId, token),
                !context.ChangeTracker.Entries().Any(), false);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await transaction.RollbackAsync(cleanup.Token); cleanup.Token.ThrowIfCancellationRequested(); }
            catch { unusable = true; await connection.DisposeAsync(); throw new InvalidOperationException("Full team test rollback could not be confirmed; fixture closed."); }
            finally { scenarioInterceptor.Deactivate(); context.ChangeTracker.Clear(); context.Database.AutoSavepointsEnabled = true; await transaction.DisposeAsync(); }
        }
        await VerifyOwnedSessionAsync(token);
        var removed = !await context.Competitions.AnyAsync(c => c.Id == competitionId, token) &&
            !await context.Seasons.AnyAsync(s => s.CompetitionId == competitionId, token) &&
            !await context.SeasonExternalIdentities.AnyAsync(m => m.SeasonId == seasonId, token) &&
            !await context.FootballTeams.AnyAsync(t => teamIds.Contains(t.Id), token) &&
            !await context.FootballTeamExternalIdentities.AnyAsync(m => teamIds.Contains(m.TeamId) || (m.ProviderKey == identity.ProviderKey && m.IdentityScope == identity.IdentityScope), token) &&
            !await context.FootballSeasonTeams.AnyAsync(m => m.SeasonId == seasonId || m.SeasonId == nextSeasonId, token);
        return observation! with { AllFixtureRowsRolledBack = removed };
    }
}
