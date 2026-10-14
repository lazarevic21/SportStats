using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using Npgsql;
using SportStats.Infrastructure.Persistence;

namespace SportStats.IntegrationTests;

/// <summary>
/// Explicitly opened by database tests only; never registered as a startup-test fixture.
/// Does not create databases, migrate, alter schemas, or clean up committed data.
/// </summary>
public sealed partial class PostgreSqlTestDatabaseFixture : IAsyncDisposable
{
    private readonly NpgsqlConnection connection;
    private readonly SportStatsDbContext context;
    private readonly string originalConnectionString;
    private readonly CompetitionScenarioInterceptor scenarioInterceptor = new();

    private PostgreSqlTestDatabaseFixture(NpgsqlConnection connection)
    {
        this.connection = connection;
        originalConnectionString = connection.ConnectionString;
        context = new SportStatsDbContext(CreateScenarioOptions(connection, scenarioInterceptor));
    }

    // Never return a context, IQueryable, connection, command, or transaction.
    public void Add<TEntity>(TEntity entity) where TEntity : class => context.Add(entity);

    public async Task<IReadOnlyList<TEntity>> ReadAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken = default)
        where TEntity : class
    {
        await VerifyOwnedSessionAsync(cancellationToken);
        return await context.Set<TEntity>().AsNoTracking().Where(predicate).ToListAsync(cancellationToken);
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await VerifyOwnedSessionAsync(cancellationToken);
        return await context.SaveChangesAsync(cancellationToken);
    }

    private async Task VerifyOwnedSessionAsync(CancellationToken cancellationToken)
    {
        if (unusable) throw new InvalidOperationException("The PostgreSQL test fixture is unusable after cleanup failure.");
        RequireUnchangedOpenSession(connection, originalConnectionString);
        await using var command = new NpgsqlCommand(ServerIdentitySql, connection,
            context.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Test database identity verification failed.");
        VerifyRestrictedServerIdentity(reader);
    }

    internal static void RequireUnchangedOpenSession(NpgsqlConnection connection, string originalConnectionString)
    {
        if (connection.State != ConnectionState.Open ||
            !string.Equals(connection.ConnectionString, originalConnectionString, StringComparison.Ordinal))
            throw new InvalidOperationException("Test database session is closed or changed; create a new verified fixture.");
    }

    public static async Task<PostgreSqlTestDatabaseFixture> OpenAsync(
        string? explicitlySuppliedConnectionString, CancellationToken cancellationToken = default)
    {
        var settings = ValidateConfiguration(explicitlySuppliedConnectionString);
        var connection = new NpgsqlConnection(settings.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(ServerIdentitySql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Test database identity verification failed.");
            VerifyRestrictedServerIdentity(reader);
            return new PostgreSqlTestDatabaseFixture(connection);
        }
        catch (OperationCanceledException)
        {
            await connection.DisposeAsync();
            throw;
        }
        catch
        {
            await connection.DisposeAsync();
            // Do not propagate provider diagnostics or configuration values.
            throw new InvalidOperationException("Test database connection or identity verification failed.");
        }
    }

    internal static NpgsqlConnectionStringBuilder ValidateConfiguration(string? value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException();
            var settings = new NpgsqlConnectionStringBuilder(value);
            if (!string.Equals(settings.Database, "sportstats_test", StringComparison.Ordinal) ||
                !string.Equals(settings.Username, "sportstats_app", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(settings.Host))
                throw new ArgumentException();
            // These tests must never enlist implicitly or reuse an unrelated pooled session.
            settings.Enlist = false;
            settings.Pooling = false;
            settings.IncludeErrorDetail = false;
            settings.LogParameters = false;
            settings.PersistSecurityInfo = false;
            return settings;
        }
        catch
        {
            throw new InvalidOperationException("Explicit valid sportstats_test application-role configuration is required.");
        }
    }

    internal static void VerifyServerIdentity(string database, string user)
    {
        if (!string.Equals(database, "sportstats_test", StringComparison.Ordinal) ||
            !string.Equals(user, "sportstats_app", StringComparison.Ordinal))
            throw new InvalidOperationException("Test database identity verification failed.");
    }

    private static DbContextOptions<SportStatsDbContext> CreateOptions(NpgsqlConnection connection) =>
        CreateScenarioOptions(connection, null);

    private static DbContextOptions<SportStatsDbContext> CreateScenarioOptions(NpgsqlConnection connection,
        CompetitionScenarioInterceptor? scenario) =>
        new DbContextOptionsBuilder<SportStatsDbContext>()
            .UseNpgsql(connection, postgres => postgres.MaxBatchSize(1))
            .AddInterceptors(new RefuseReopeningInterceptor(), new NeverCommitInterceptor(), scenario ?? new CompetitionScenarioInterceptor())
            .EnableSensitiveDataLogging(false)
            .EnableDetailedErrors(false)
            .Options;

    // An EF operation must not reopen even the original connection after verification.
    private sealed class RefuseReopeningInterceptor : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result) =>
            throw new InvalidOperationException("Test database session must be opened and verified by a new fixture.");

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Test database session must be opened and verified by a new fixture.");
    }

    public async ValueTask DisposeAsync()
    {
        try { await context.DisposeAsync(); }
        finally { await connection.DisposeAsync(); }
    }
}
