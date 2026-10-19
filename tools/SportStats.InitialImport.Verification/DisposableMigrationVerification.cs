using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using System.Runtime.CompilerServices;
using SportStats.Domain.Football;
using SportStats.Domain.Platform;
using SportStats.Infrastructure.Persistence;

namespace SportStats.InitialImport.Verification;

// Only invoked after Program's fresh-server approval/ownership guard. No connection fallback.
internal sealed class DisposableMigrationVerification(string connectionString, string marker, Action<string> stage)
{
    private sealed record Subject(string Schema, string Table, string Target, Guid First, Guid Second)
    {
        internal string Qualified => $"{Schema}.{Table}"; // All identifiers are compile-time fixture constants.
    }

    private static readonly string[] DataTables =
    [
        "platform.sports", "platform.competitions", "platform.competition_external_ids", "platform.seasons",
        "platform.season_external_ids", "football.teams", "football.team_external_ids", "football.season_teams",
        "football.matches", "football.match_external_ids", "football.standing_snapshots", "football.standing_rows",
        "platform.\"__EFMigrationsHistory\""
    ];

    internal async Task<Guid> RunAsync(CancellationToken token)
    {
        stage("migration-baseline");
        await MigrateAsync(DisposableVerificationContract.InitialMigration, token);
        await VerifyStateAsync(false, token);
        var (sportId, subjects) = await SeedAsync(token);
        var baselineSchema = await SchemaSnapshotAsync(token);

        foreach (var subject in subjects)
        foreach (var conflicting in new[] { false, true })
        {
            stage(conflicting ? "migration-conflicting-duplicate" : "migration-identical-duplicate");
            var first = Guid.NewGuid(); var second = Guid.NewGuid();
            await using (var connection = await OpenAsync(token))
            {
                await InsertAsync(connection, null, subject, first, "duplicate", subject.First, token);
                await InsertAsync(connection, null, subject, second, "duplicate", conflicting ? subject.Second : subject.First, token);
            }
            var before = await DataSnapshotAsync(token);
            await ExpectMigrationFailureAsync(PostgresErrorCodes.RaiseException, token);
            Require(before == await DataSnapshotAsync(token));
            Require(baselineSchema == await SchemaSnapshotAsync(token));
            await VerifyStateAsync(false, token);
            // Fixture cleanup only after preservation is proven, on this invocation's fresh server.
            await using var cleanup = await OpenAsync(token);
            await ExecuteAsync(cleanup, null, $"DELETE FROM {subject.Qualified} WHERE \"Id\" = $1 OR \"Id\" = $2", token, first, second);
        }

        // A late failure must roll back indexes already created earlier in Up, not just the preflight.
        stage("migration-late-index-failure");
        await using (var collision = await OpenAsync(token))
            await ExecuteAsync(collision, null, "CREATE INDEX \"UX_competition_external_ids_identity\" ON platform.competition_external_ids (\"Id\")", token);
        var collisionSchema = await SchemaSnapshotAsync(token);
        var collisionData = await DataSnapshotAsync(token);
        await ExpectMigrationFailureAsync(PostgresErrorCodes.DuplicateTable, token);
        Require(collisionSchema == await SchemaSnapshotAsync(token));
        Require(collisionData == await DataSnapshotAsync(token));
        await using (var collision = await OpenAsync(token))
            await ExecuteAsync(collision, null, "DROP INDEX platform.\"UX_competition_external_ids_identity\"", token);
        await VerifyStateAsync(false, token);

        stage("migration-lock-timeout");
        var lockData = await DataSnapshotAsync(token);
        await using (var blocker = await OpenAsync(token))
        await using (var transaction = await blocker.BeginTransactionAsync(token))
        {
            stage("migration-lock-timeout-acquire-blocker");
            await ExecuteAsync(blocker, transaction, "LOCK TABLE platform.competition_external_ids IN ROW EXCLUSIVE MODE", token);
            stage("migration-lock-timeout-expect-55P03");
            try { await ExpectMigrationFailureAsync(PostgresErrorCodes.LockNotAvailable, token); }
            finally
            {
                // Preserve the failing operation's stage unless rollback itself fails.
                try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await transaction.RollbackAsync(cleanup.Token); }
                catch { stage("migration-lock-timeout-release-blocker"); throw; }
            }
        }
        stage("migration-lock-timeout-preserve-data-history");
        Require(lockData == await DataSnapshotAsync(token));
        stage("migration-lock-timeout-preserve-schema");
        Require(baselineSchema == await SchemaSnapshotAsync(token));
        stage("migration-lock-timeout-baseline-catalog");
        await VerifyStateAsync(false, token);

        stage("migration-upgrade");
        var beforeUpgrade = await DataSnapshotAsync(token, includeHistory: false);
        await MigrateAsync(DisposableVerificationContract.TargetMigration, token);
        await VerifyStateAsync(true, token);
        Require(beforeUpgrade == await DataSnapshotAsync(token, includeHistory: false));
        Require(baselineSchema == await SchemaSnapshotAsync(token));

        stage("migration-down-up");
        await MigrateAsync(DisposableVerificationContract.InitialMigration, token);
        await VerifyStateAsync(false, token);
        Require(beforeUpgrade == await DataSnapshotAsync(token, includeHistory: false));
        Require(baselineSchema == await SchemaSnapshotAsync(token));
        await MigrateAsync(DisposableVerificationContract.TargetMigration, token);
        await VerifyStateAsync(true, token);
        Require(beforeUpgrade == await DataSnapshotAsync(token, includeHistory: false));
        Require(baselineSchema == await SchemaSnapshotAsync(token));

        foreach (var subject in subjects)
        {
            stage("migration-distinct-identities");
            await using (var connection = await OpenAsync(token))
            {
                await InsertAsync(connection, null, subject, Guid.NewGuid(), "distinct", subject.First, token);
                await InsertAsync(connection, null, subject, Guid.NewGuid(), "alias", subject.First, token);
                await InsertAsync(connection, null, subject, Guid.NewGuid(), "distinct", subject.First, token, provider: "other");
                await InsertAsync(connection, null, subject, Guid.NewGuid(), "distinct", subject.First, token, scope: "other");
            }
            foreach (var conflicting in new[] { false, true })
                await VerifyRaceAsync(subject, conflicting, winnerCommits: true, token);
            await VerifyRaceAsync(subject, conflicting: true, winnerCommits: false, token);
        }
        stage("migration-final-catalog");
        await VerifyStateAsync(true, token);
        Require(baselineSchema == await SchemaSnapshotAsync(token));
        Console.WriteLine("Disposable migration upgrade, duplicate/late-failure rollback, history, lock timeout, Down/Up, two-session uniqueness and exact catalogs passed.");
        return sportId;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken token)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(token);
            await using var guard = new NpgsqlCommand("SELECT current_setting('sportstats.disposable_marker', true), current_database(), current_user, session_user", connection);
            await using var reader = await guard.ExecuteReaderAsync(token);
            Require(await reader.ReadAsync(token) && !reader.IsDBNull(0) && reader.GetString(0) == marker &&
                reader.GetString(1) == "sportstats_test" && reader.GetString(2) == "postgres" && reader.GetString(3) == "postgres");
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private async Task MigrateAsync(string target, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        var options = new DbContextOptionsBuilder<SportStatsDbContext>().UseNpgsql(connection,
            postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "platform")).Options;
        await using var context = new SportStatsDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target, token);
    }

    private async Task ExpectMigrationFailureAsync(string sqlState, CancellationToken token)
        => await ExpectFailureAsync(() => MigrateAsync(DisposableVerificationContract.TargetMigration, token), sqlState);

    // Npgsql's default non-retrying EF strategy wraps transient PostgreSQL errors (including
    // 55P03) in InvalidOperationException. Accept that precise shape as well as direct errors.
    internal static PostgresException? MigrationError(Exception error) => error switch
    {
        PostgresException postgres => postgres,
        InvalidOperationException { InnerException: PostgresException { IsTransient: true } postgres } => postgres,
        _ => null
    };

    internal static async Task ExpectFailureAsync(Func<Task> migrate, string sqlState)
    {
        try { await migrate(); }
        catch (Exception exception) when (MigrationError(exception)?.SqlState == sqlState)
        {
            var error = MigrationError(exception)!;
            if (sqlState == PostgresErrorCodes.RaiseException)
                Require(error.MessageText == "External identity uniqueness audit failed; no mappings were changed.");
            return;
        }
        Require(false); // A successful migration must never satisfy an expected-failure scenario.
    }

    private async Task<(Guid, Subject[])> SeedAsync(CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        var options = new DbContextOptionsBuilder<SportStatsDbContext>().UseNpgsql(connection).Options;
        await using var context = new SportStatsDbContext(options);
        var entities = CreateSeedGraph();
        var sport = entities.OfType<Sport>().Single();
        var competitions = entities.OfType<Competition>().ToArray();
        var seasons = entities.OfType<Season>().ToArray();
        var teams = entities.OfType<FootballTeam>().ToArray();
        var matches = entities.OfType<FootballMatch>().ToArray();
        context.AddRange(entities);
        await context.SaveChangesAsync(token);
        Subject[] subjects = [new("platform", "competition_external_ids", "CompetitionId", competitions[0].Id, competitions[1].Id),
            new("platform", "season_external_ids", "SeasonId", seasons[0].Id, seasons[1].Id),
            new("football", "team_external_ids", "TeamId", teams[0].Id, teams[1].Id),
            new("football", "match_external_ids", "MatchId", matches[0].Id, matches[1].Id)];
        foreach (var subject in subjects)
            await InsertAsync(connection, null, subject, Guid.NewGuid(), "baseline", subject.First, token);
        return (sport.Id, subjects);
    }

    // Separate graph construction permits offline verification of every required FK dependency.
    internal static object[] CreateSeedGraph()
    {
        var sport = new Sport(Guid.NewGuid(), "football", "Football");
        var firstCompetition = new Competition(sport, "VERIFY-1", "Verification first");
        var secondCompetition = new Competition(sport, "VERIFY-2", "Verification second");
        var firstSeason = new Season(firstCompetition, "verification");
        var secondSeason = new Season(secondCompetition, "verification");
        var firstTeam = new FootballTeam("Verification first"); var secondTeam = new FootballTeam("Verification second");
        var home = new FootballSeasonTeam(firstSeason, firstTeam); var away = new FootballSeasonTeam(firstSeason, secondTeam);
        var firstMatch = new FootballMatch(firstSeason, home, away); var secondMatch = new FootballMatch(firstSeason, away, home);
        return [sport, firstCompetition, secondCompetition, firstSeason, secondSeason, firstTeam, secondTeam,
            home, away, firstMatch, secondMatch];
    }

    private async Task VerifyRaceAsync(Subject subject, bool conflicting, bool winnerCommits, CancellationToken token)
    {
        var raceStage = winnerCommits ? (conflicting ? "migration-race-conflicting" : "migration-race-identical") : "migration-race-winner-rollback";
        void At(string step) => stage(raceStage + "-" + step);
        At("open-sessions");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var raceToken = deadline.Token;
        await using var first = await OpenAsync(raceToken);
        await using var second = await OpenAsync(raceToken);
        await using var observer = await OpenAsync(raceToken);
        Require(first.ProcessID != second.ProcessID);
        await using var winner = await first.BeginTransactionAsync(raceToken);
        await using var loser = await second.BeginTransactionAsync(raceToken);
        var key = Guid.NewGuid().ToString("N"); var winningId = Guid.NewGuid(); var losingId = Guid.NewGuid();
        var provisionalTeam = Guid.NewGuid();
        Task? pending = null;
        var winnerCompleted = false;
        var loserCompleted = false;
        try
        {
            At("first-insert");
            await InsertAsync(first, winner, subject, winningId, key, subject.First, raceToken);
            At("second-provisional-write");
            // An unrelated provisional write must be rolled back along with a losing mapping.
            await ExecuteAsync(second, loser, "INSERT INTO football.teams (\"Id\", \"Name\") VALUES ($1, 'provisional loser')", raceToken, provisionalTeam);
            await ExecuteAsync(second, loser, "SET LOCAL statement_timeout = '12s'", raceToken);
            pending = InsertAsync(second, loser, subject, losingId, key, conflicting ? subject.Second : subject.First, raceToken);
            At("confirm-blocking");
            while (true)
            {
                await using var blocked = new NpgsqlCommand("SELECT $1 = ANY(pg_catalog.pg_blocking_pids($2))", observer);
                blocked.Parameters.AddWithValue(first.ProcessID); blocked.Parameters.AddWithValue(second.ProcessID);
                if ((bool)(await blocked.ExecuteScalarAsync(raceToken))!) break;
                if (pending.IsCompleted)
                {
                    // Observe the actual result; never turn an early failure into a timing assertion.
                    Exception? earlyError = null;
                    try { await pending; } catch (Exception error) { earlyError = error; }
                    ValidateRaceOutcome(true, null, earlyError is null, earlyError, $"UX_{subject.Table}_identity");
                    throw new DisposableRaceException(RaceOutcome.RejectedWithoutObservedBlocking);
                }
                await Task.Delay(20, raceToken);
            }
            if (winnerCommits)
            {
                At("first-commit");
                await winner.CommitAsync(raceToken);
                winnerCompleted = true;
                At("second-outcome");
                Exception? secondError = null;
                try { await pending; } catch (Exception error) { secondError = error; }
                ValidateRaceOutcome(true, null, secondError is null, secondError, $"UX_{subject.Table}_identity");
                At("exactly-one-succeeded-23505");
            }
            else
            {
                At("first-rollback");
                await winner.RollbackAsync(raceToken);
                winnerCompleted = true;
                At("second-insert-after-rollback");
                await pending;
                At("second-commit");
                await loser.CommitAsync(raceToken);
                loserCompleted = true;
            }
        }
        finally
        {
            deadline.Cancel();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // Release the blocker first; always observe the pending command before disposing its connection.
            try { await RollbackActiveAsync(winnerCompleted, () => winner.RollbackAsync(cleanup.Token)); }
            catch { At("cleanup-first-rollback"); throw; }
            finally
            {
                try { if (pending is not null) { try { await pending; } catch { /* Observed; normal path classified above. */ } } }
                finally
                {
                    try { await RollbackActiveAsync(loserCompleted, () => loser.RollbackAsync(cleanup.Token)); }
                    catch { At("cleanup-second-rollback"); throw; }
                }
            }
        }
        At("persisted-mapping");
        await using var fresh = await OpenAsync(token);
        await using var row = new NpgsqlCommand($"SELECT \"Id\", \"{subject.Target}\" FROM {subject.Qualified} WHERE \"ProviderKey\" = 'verification' AND \"IdentityScope\" = 'scope' AND \"ExternalId\" = $1", fresh);
        row.Parameters.AddWithValue(key);
        await using (var reader = await row.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) throw new DisposableRaceException(RaceOutcome.RowCountZero);
            Require(reader.GetGuid(0) == (winnerCommits ? winningId : losingId));
            Require(reader.GetGuid(1) == (winnerCommits || !conflicting ? subject.First : subject.Second));
            if (await reader.ReadAsync(token)) throw new DisposableRaceException(RaceOutcome.RowCountMultiple);
        }
        At("provisional-row");
        await using var residual = new NpgsqlCommand("SELECT count(*) FROM football.teams WHERE \"Id\" = $1", fresh);
        residual.Parameters.AddWithValue(provisionalTeam);
        Require((long)(await residual.ExecuteScalarAsync(token))! == (winnerCommits ? 0 : 1));
    }

    // Npgsql Connection remains non-null after completion; it does not indicate an active transaction.
    internal static Task RollbackActiveAsync(bool completed, Func<Task> rollback) =>
        completed ? Task.CompletedTask : rollback();

    internal static void ValidateRaceOutcome(bool firstSucceeded, Exception? firstError,
        bool secondSucceeded, Exception? secondError, string expectedConstraint)
    {
        if (firstSucceeded && secondSucceeded) throw new DisposableRaceException(RaceOutcome.BothSucceeded);
        if (!firstSucceeded && !secondSucceeded) throw new DisposableRaceException(RaceOutcome.BothFailed, firstError ?? secondError);
        if (!firstSucceeded) throw new DisposableRaceException(RaceOutcome.UnexpectedWinner, firstError);
        if (secondError is not PostgresException postgres)
            throw new DisposableRaceException(RaceOutcome.ExactlyOneSucceededUnexpectedException, secondError);
        if (postgres.SqlState != PostgresErrorCodes.UniqueViolation)
            throw new DisposableRaceException(RaceOutcome.ExactlyOneSucceededUnexpectedSqlState, postgres);
        if (postgres.ConstraintName != expectedConstraint)
            throw new DisposableRaceException(RaceOutcome.ExactlyOneSucceededUnexpectedConstraint, postgres);
    }

    private async Task VerifyStateAsync(bool upgraded, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using (var history = new NpgsqlCommand("SELECT \"MigrationId\" FROM platform.\"__EFMigrationsHistory\" ORDER BY \"MigrationId\"", connection))
        await using (var reader = await history.ExecuteReaderAsync(token))
        {
            var values = new List<string>();
            while (await reader.ReadAsync(token)) values.Add(reader.GetString(0));
            Require(DisposableVerificationContract.ValidHistory(values, upgraded));
        }
        foreach (var (schema, table) in new[] { ("platform", "competition_external_ids"), ("platform", "season_external_ids"),
            ("football", "team_external_ids"), ("football", "match_external_ids") })
        {
            await using var command = new NpgsqlCommand("""
                SELECT n.nspname, t.relname, c.relname, i.indisunique, i.indisvalid, i.indisready,
                    i.indpred IS NOT NULL, i.indexprs IS NOT NULL,
                    ARRAY(SELECT a.attname::text FROM unnest(i.indkey) WITH ORDINALITY k(attnum, ordinality)
                        JOIN pg_catalog.pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum ORDER BY k.ordinality),
                    i.indnatts::integer
                FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace ixn ON ixn.oid=c.relnamespace
                JOIN pg_catalog.pg_index i ON i.indexrelid=c.oid
                JOIN pg_catalog.pg_class t ON t.oid=i.indrelid
                JOIN pg_catalog.pg_namespace n ON n.oid=t.relnamespace
                WHERE ixn.nspname=$1 AND c.relname=$2
                """, connection);
            command.Parameters.AddWithValue(schema); command.Parameters.AddWithValue($"UX_{table}_identity");
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!upgraded) { Require(!await reader.ReadAsync(token)); continue; }
            Require(await reader.ReadAsync(token));
            Require(DisposableVerificationContract.ValidIndex(schema, table, new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7),
                reader.GetFieldValue<string[]>(8), reader.GetInt32(9))));
            Require(!await reader.ReadAsync(token));
        }
    }

    private async Task<string> DataSnapshotAsync(CancellationToken token, bool includeHistory = true)
    {
        await using var connection = await OpenAsync(token);
        var rows = new List<string>();
        foreach (var table in DataTables.Where(t => includeHistory || !t.Contains("__EFMigrationsHistory", StringComparison.Ordinal)))
        {
            await using var command = new NpgsqlCommand($"SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text)::text, '[]') FROM {table} t", connection);
            rows.Add(table + ":" + (string)(await command.ExecuteScalarAsync(token))!);
        }
        return string.Join('\n', rows);
    }

    // Excludes only the new UX indexes. All baseline columns, index definitions and constraints must survive.
    private async Task<string> SchemaSnapshotAsync(CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(jsonb_agg(value ORDER BY value)::text, '[]') FROM (
                SELECT n.nspname || '.' || t.relname || ':' || pg_get_indexdef(i.indexrelid) AS value
                FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class t ON t.oid=i.indrelid
                JOIN pg_catalog.pg_namespace n ON n.oid=t.relnamespace JOIN pg_catalog.pg_class ix ON ix.oid=i.indexrelid
                WHERE n.nspname IN ('platform','football') AND ix.relname NOT IN
                    ('UX_competition_external_ids_identity','UX_season_external_ids_identity','UX_team_external_ids_identity','UX_match_external_ids_identity')
                UNION ALL SELECT n.nspname || '.' || t.relname || ':' || c.conname || ':' || pg_get_constraintdef(c.oid)
                FROM pg_catalog.pg_constraint c JOIN pg_catalog.pg_class t ON t.oid=c.conrelid
                JOIN pg_catalog.pg_namespace n ON n.oid=t.relnamespace WHERE n.nspname IN ('platform','football')
                UNION ALL SELECT table_schema || '.' || table_name || ':' || column_name || ':' || data_type || ':' || is_nullable
                FROM information_schema.columns WHERE table_schema IN ('platform','football')
            ) definitions
            """, connection);
        return (string)(await command.ExecuteScalarAsync(token))!;
    }

    private static Task InsertAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Subject subject,
        Guid id, string key, Guid target, CancellationToken token, string provider = "verification", string scope = "scope") =>
        ExecuteAsync(connection, transaction, $"INSERT INTO {subject.Qualified} (\"Id\", \"ProviderKey\", \"IdentityScope\", \"ExternalId\", \"{subject.Target}\") VALUES ($1,$2,$3,$4,$5)",
            token, id, provider, scope, key, target);

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql,
        CancellationToken token, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync(token);
    }

    private static void Require(bool condition, [CallerLineNumber] int line = 0)
    { if (!condition) throw new DisposableAssertionException(line); }
}

internal sealed class DisposableAssertionException(int line) : InvalidOperationException
{
    internal int AssertionLine { get; } = line;
}

internal enum RaceOutcome
{
    BothSucceeded, BothFailed, UnexpectedWinner, ExactlyOneSucceededUnexpectedException,
    ExactlyOneSucceededUnexpectedSqlState, ExactlyOneSucceededUnexpectedConstraint, RowCountZero, RowCountMultiple,
    RejectedWithoutObservedBlocking
}

internal sealed class DisposableRaceException(RaceOutcome outcome, Exception? inner = null) : InvalidOperationException(null, inner)
{
    internal RaceOutcome Outcome { get; } = outcome;
}

internal static class DisposableFailureDiagnostics
{
    internal static string Describe(Exception error)
    {
        if (error is DisposableRaceException race)
            return $"race={race.Outcome}; {(race.InnerException is null ? "database-error=none" : Describe(race.InnerException))}";
        if (error is DisposableAssertionException assertion)
            return $"assertion=DisposableMigrationVerification:{assertion.AssertionLine}";
        var postgres = DisposableMigrationVerification.MigrationError(error);
        if (postgres is null) return "database-error=none-or-unrecognized-wrapper";
        // Only fixed codes; never emit messages, details, SQL, identifiers, or exception data.
        var code = postgres.SqlState switch
        {
            "55P03" => "55P03", "P0001" => "P0001", "42P07" => "42P07",
            "23505" => "23505", "57014" => "57014", "40P01" => "40P01",
            "25P02" => "25P02", "42501" => "42501", _ => "other"
        };
        return $"sqlstate={code}; shape={(ReferenceEquals(error, postgres) ? "direct" : "ef-transient-wrapper")}";
    }
}
