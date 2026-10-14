using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Platform;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.AdvisoryLocks;
using SportStats.Infrastructure.Persistence.IdentityResolution;

namespace SportStats.IntegrationTests;

public enum CompetitionScenario { CreationReplay, ExistingReuseConflict, FailureRollback }
public sealed record CompetitionScenarioObservation(bool IdsAndRelationshipsCorrect, bool ExactIdentityPreserved,
    bool ReusedWithoutDuplicates, bool StrictConflictRejected, bool EarlierInsertObserved,
    bool CallerTransactionPreserved, bool CallerWorkPreserved, bool TrackingEmpty, bool AllFixtureRowsRolledBack);

public sealed partial class PostgreSqlTestDatabaseFixture
{
    private const string ServerIdentitySql = "SELECT current_database(), current_user, session_user, r.rolsuper, r.rolcreatedb, r.rolcreaterole, r.rolbypassrls, EXISTS (SELECT 1 FROM pg_catalog.pg_roles elevated WHERE (elevated.rolsuper OR elevated.rolcreatedb OR elevated.rolcreaterole OR elevated.rolbypassrls) AND pg_catalog.pg_has_role(current_user, elevated.oid, 'MEMBER')) FROM pg_catalog.pg_roles r WHERE r.rolname = current_user";
    private bool unusable;

    internal static void ValidateLiveOptIn(string? flag)
    {
        if (flag != "1") throw new InvalidOperationException("Explicit approval opt-in is required for live PostgreSQL scenarios.");
    }
    internal static void VerifyRestrictedRole(bool superuser, bool createDb, bool createRole, bool bypassRls, bool elevatedMembership)
    {
        if (superuser || createDb || createRole || bypassRls || elevatedMembership)
            throw new InvalidOperationException("The PostgreSQL test role must not have elevated privileges.");
    }
    private static void VerifyRestrictedServerIdentity(NpgsqlDataReader reader)
    {
        VerifyServerIdentity(reader.GetString(0), reader.GetString(1));
        if (reader.GetString(2) != "sportstats_app") throw new InvalidOperationException("The verified test role must own the session.");
        VerifyRestrictedRole(reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7));
    }

    /// <summary>Runs a fixed scenario only. Owns a ReadCommitted transaction; never commits,
    /// exports database capabilities, or edits permanent rows. An occupied PL scope fails closed.</summary>
    public async Task<CompetitionScenarioObservation> RunCompetitionScenarioAsync(CompetitionScenario scenario, CancellationToken token = default)
    {
        ValidateLiveOptIn(Environment.GetEnvironmentVariable("SPORTSTATS_RUN_POSTGRES_TESTS"));
        if (!Enum.IsDefined(scenario) || unusable) throw new InvalidOperationException("The guarded scenario is unavailable.");
        await VerifyOwnedSessionAsync(token);
        if (context.Database.CurrentTransaction is not null || context.ChangeTracker.Entries().Any())
            throw new InvalidOperationException("Scenarios require a clean fixture-owned session.");
        var markerId = Guid.NewGuid();
        var markerCode = "fixture-caller-" + markerId.ToString("N");
        var providerIdentity = new ProviderIdentity("fixture", Guid.NewGuid().ToString("N"), " 000PL opaque ");
        var competitionIds = new HashSet<Guid>();
        var mappingIds = new HashSet<Guid>();
        CompetitionScenarioObservation? observed = null;
        var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        try
        {
            scenarioInterceptor.Activate(connection, transaction.GetDbTransaction(), () => VerifyOwnedSessionAsync(token));
            await new PostgreSqlAdvisoryLockAcquirer().AcquireAsync((NpgsqlTransaction)transaction.GetDbTransaction(),
                new FootballAdvisoryLockPlan([]), TimeSpan.FromSeconds(10), token);
            var sport = await context.Sports.AsTracking().SingleOrDefaultAsync(s => s.Code == "football", token)
                ?? throw new InvalidOperationException("sportstats_test requires an existing football Sport in platform.sports; no fixture rows will be inserted.");
            if (await context.Competitions.AnyAsync(c => c.SportId == sport.Id && c.Code == "PL", token))
                throw new InvalidOperationException("sportstats_test must have an unoccupied football/PL scope; permanent rows will not be changed.");
            context.Entry(new Competition(markerId, sport, markerCode, "Caller-owned marker")).State = EntityState.Added;
            competitionIds.Add(markerId);
            Competition? seeded = null;
            if (scenario == CompetitionScenario.ExistingReuseConflict)
            {
                seeded = new Competition(Guid.NewGuid(), sport, "PL", "Existing metadata");
                var mapping = new CompetitionExternalIdentity(Guid.NewGuid(), providerIdentity.ProviderKey,
                    providerIdentity.IdentityScope, providerIdentity.ExternalId, seeded.Id);
                context.Add(seeded); context.Add(mapping);
                competitionIds.Add(seeded.Id); mappingIds.Add(mapping.Id);
            }
            await context.SaveChangesAsync(token);
            context.ChangeTracker.Clear();
            scenarioInterceptor.ObserveResolution(scenario == CompetitionScenario.FailureRollback);
            // For this failure case, test the resolver's outer savepoint directly, without EF's
            // per-SaveChanges savepoint hiding the earlier INSERT from the resolver rollback.
            if (scenario == CompetitionScenario.FailureRollback) context.Database.AutoSavepointsEnabled = false;
            var batch = new IdentityResolutionBatch([new(sport.Id, new(providerIdentity, "PL", "Requested metadata"))], [], [], []);
            var orchestrator = new FootballResolutionOrchestrator(context, TimeSpan.FromSeconds(10));
            var strictRejected = false;
            var idsCorrect = false;
            var exactIdentity = false;
            var reused = false;
            if (scenario == CompetitionScenario.FailureRollback)
            {
                var failedAsExpected = false;
                try { await orchestrator.PersistCompetitionsAsync(batch, token); }
                catch (DbUpdateException exception) when (exception.InnerException is InjectedCompetitionMappingFailure)
                { failedAsExpected = true; }
                if (!failedAsExpected || !scenarioInterceptor.EarlierInsertObserved)
                    throw new InvalidOperationException("The failure scenario did not observe the required successful earlier INSERT.");
                competitionIds.UnionWith(scenarioInterceptor.InsertedCompetitionIds);
            }
            else
            {
                var first = await orchestrator.PersistCompetitionsAsync(batch, token) as IdentityResolutionBatchResult.Resolved
                    ?? throw new InvalidOperationException("Competition resolution did not succeed.");
                var id = first.Competitions.Single().EntityId;
                competitionIds.Add(id);
                var persisted = await context.Competitions.AsNoTracking().SingleAsync(c => c.Id == id, token);
                var mapping = await context.CompetitionExternalIdentities.AsNoTracking().SingleAsync(m => m.CompetitionId == id, token);
                mappingIds.Add(mapping.Id);
                idsCorrect = id != Guid.Empty && mapping.Id != Guid.Empty && mapping.Id != id &&
                    persisted.SportId == sport.Id && mapping.CompetitionId == id &&
                    (seeded is null || (id == seeded.Id && persisted.Name == "Existing metadata"));
                exactIdentity = mapping.ProviderKey == providerIdentity.ProviderKey && mapping.IdentityScope == providerIdentity.IdentityScope &&
                    mapping.ExternalId == providerIdentity.ExternalId;
                var second = await orchestrator.PersistCompetitionsAsync(batch, token) as IdentityResolutionBatchResult.Resolved
                    ?? throw new InvalidOperationException("Competition replay did not succeed.");
                reused = second.Competitions.Single().EntityId == id && second.Competitions[0].Disposition == IdentityResolutionDisposition.ExistingMapping &&
                    await context.Competitions.CountAsync(c => c.SportId == sport.Id && c.Code == "PL", token) == 1 &&
                    await context.CompetitionExternalIdentities.CountAsync(m => m.CompetitionId == id, token) == 1;
                if (scenario == CompetitionScenario.ExistingReuseConflict)
                {
                    var foreign = batch with { Competitions = [new(sport.Id, new(providerIdentity with { ExternalId = "unmapped" }, "PL", "Other metadata"))] };
                    strictRejected = await orchestrator.PersistCompetitionsAsync(foreign, token) is IdentityResolutionBatchResult.Failed
                        { Failure.Code: IdentityResolutionFailureCode.NaturalKeyConflict };
                }
            }
            var emptyTracker = !context.ChangeTracker.Entries().Any();
            var callerPreserved = ReferenceEquals(context.Database.CurrentTransaction, transaction) &&
                transaction.GetDbTransaction().IsolationLevel == IsolationLevel.ReadCommitted;
            var markerPreserved = await context.Competitions.AsNoTracking().AnyAsync(
                c => c.Id == markerId && c.SportId == sport.Id && c.Code == markerCode && c.Name == "Caller-owned marker", token);
            if (scenario == CompetitionScenario.FailureRollback)
            {
                if (await context.Competitions.AnyAsync(c => c.SportId == sport.Id && c.Code == "PL", token) ||
                    await context.CompetitionExternalIdentities.AnyAsync(m => m.ProviderKey == providerIdentity.ProviderKey && m.IdentityScope == providerIdentity.IdentityScope, token))
                    throw new InvalidOperationException("Resolver savepoint rollback did not remove all resolution rows.");
            }
            observed = new(idsCorrect, exactIdentity, reused, strictRejected, scenarioInterceptor.EarlierInsertObserved,
                callerPreserved, markerPreserved, emptyTracker, false);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await transaction.RollbackAsync(cleanup.Token);
                cleanup.Token.ThrowIfCancellationRequested();
            }
            catch
            {
                unusable = true;
                await connection.DisposeAsync();
                throw new InvalidOperationException("Full test transaction rollback could not be confirmed; the fixture was closed and is unusable.");
            }
            finally
            {
                scenarioInterceptor.Deactivate();
                context.ChangeTracker.Clear();
                context.Database.AutoSavepointsEnabled = true;
                await transaction.DisposeAsync();
            }
        }
        // Reads after full rollback prove that successful resolver results were never committed.
        await VerifyOwnedSessionAsync(token);
        var removed = !await context.Competitions.AnyAsync(c => competitionIds.Contains(c.Id), token) &&
            !await context.CompetitionExternalIdentities.AnyAsync(m => mappingIds.Contains(m.Id) ||
                (m.ProviderKey == providerIdentity.ProviderKey && m.IdentityScope == providerIdentity.IdentityScope), token);
        return observed! with { AllFixtureRowsRolledBack = removed };
    }

    private sealed class InjectedCompetitionMappingFailure : Exception { }
    private sealed class NeverCommitInterceptor : DbTransactionInterceptor
    {
        public override InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
            => throw new InvalidOperationException("PostgreSQL integration fixtures must never commit.");
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("PostgreSQL integration fixtures must never commit.");
    }

    private sealed class CompetitionScenarioInterceptor : DbCommandInterceptor
    {
        private DbConnection? ownedConnection;
        private DbTransaction? ownedTransaction;
        private Func<Task>? verify;
        private bool observing;
        private bool inject;
        public bool EarlierInsertObserved { get; private set; }
        public HashSet<Guid> InsertedCompetitionIds { get; } = [];
        public void Activate(DbConnection connection, DbTransaction transaction, Func<Task> verification)
        { ownedConnection = connection; ownedTransaction = transaction; verify = verification; }
        public void Deactivate() { ownedConnection = null; ownedTransaction = null; verify = null; observing = false; inject = false; }
        public void ObserveResolution(bool failure)
        { observing = true; inject = failure; EarlierInsertObserved = false; InsertedCompetitionIds.Clear(); }
        private static bool Insert(DbCommand command, string table) => command.CommandText.Replace("\"", "", StringComparison.Ordinal)
            .TrimStart().StartsWith("INSERT INTO platform." + table + " ", StringComparison.OrdinalIgnoreCase);
        private async Task GuardAsync(DbCommand command)
        {
            var sql = command.CommandText.TrimStart();
            if (sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("SAVEPOINT", StringComparison.OrdinalIgnoreCase) ||
                sql.StartsWith("ROLLBACK TO", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("RELEASE SAVEPOINT", StringComparison.OrdinalIgnoreCase)) return;
            if (!(Insert(command, "competitions") || Insert(command, "competition_external_ids")) ||
                ownedTransaction is null || !ReferenceEquals(command.Transaction, ownedTransaction) ||
                !ReferenceEquals(command.Connection, ownedConnection) || ownedTransaction.IsolationLevel != IsolationLevel.ReadCommitted || verify is null)
                throw new InvalidOperationException("Fixture writes require the verified owned ReadCommitted transaction and approved tables.");
            await verify();
            if (observing && inject && Insert(command, "competition_external_ids") && EarlierInsertObserved)
                throw new InjectedCompetitionMappingFailure();
        }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { await GuardAsync(command); return result; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { await GuardAsync(command); return result; }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { GuardAsync(command).GetAwaiter().GetResult(); return result; }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        { GuardAsync(command).GetAwaiter().GetResult(); return result; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (observing && Insert(command, "competitions"))
            {
                EarlierInsertObserved = true;
                foreach (var entry in eventData.Context!.ChangeTracker.Entries<Competition>())
                    if (entry.State == EntityState.Added) InsertedCompetitionIds.Add(entry.Entity.Id);
            }
            return ValueTask.FromResult(result);
        }
    }
}
