using Npgsql;
using SportStats.MigrationPreflight;

var stage = PreflightStage.Arguments;
try
{
    var selected = PreflightContract.Parse(args);
    stage = PreflightStage.Configuration;
    var config = PreflightContract.Connection(Environment.GetEnvironmentVariable(PreflightContract.ConnectionVariable), selected);
    await using var connection = new NpgsqlConnection(config);
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    stage = PreflightStage.Connect;
    await connection.OpenAsync(deadline.Token);
    async Task<List<string>> Rows(string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await using var reader = await command.ExecuteReaderAsync(deadline.Token);
        var rows = new List<string>();
        while (await reader.ReadAsync(deadline.Token)) rows.Add(reader.GetString(0));
        return rows;
    }
    stage = PreflightStage.BeginReadOnly;
    await Rows(PreflightContract.BeginReadOnly);
    // Identity check precedes all application/history/catalog reads. No credentials or raw server errors are emitted.
    stage = PreflightStage.IdentityRead;
    var identity = new List<PreflightContract.IdentityRow>();
    await using (var command = new NpgsqlCommand(PreflightContract.IdentitySql, connection))
    await using (var reader = await command.ExecuteReaderAsync(deadline.Token))
    {
        while (await reader.ReadAsync(deadline.Token))
            identity.Add(PreflightContract.ReadIdentityRow(reader));
    }
    stage = PreflightStage.IdentityMatch;
    PreflightContract.Identity(identity, selected);
    stage = PreflightStage.RolePrivileges;
    PreflightContract.Exact(await Rows("""
        SELECT CASE WHEN NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication
          AND has_database_privilege(current_database(),'CONNECT') THEN 'ok' ELSE 'reject' END
        FROM pg_catalog.pg_roles WHERE rolname=current_user
        """), ["ok"]);
    stage = PreflightStage.SchemaPrivileges;
    foreach (var schema in new[] { "platform", "football" })
        PreflightContract.Exact(await Rows("SELECT CASE WHEN has_schema_privilege($1,'USAGE') AND has_schema_privilege($1,'CREATE') THEN 'ok' ELSE 'reject' END", schema), ["ok"]);
    stage = PreflightStage.History;
    PreflightContract.History(await Rows("SELECT \"MigrationId\"::text FROM platform.\"__EFMigrationsHistory\" ORDER BY \"MigrationId\""), selected.Migrated);
    stage = PreflightStage.HistoryPrivileges;
    PreflightContract.Exact(await Rows("""
        SELECT CASE WHEN c.relkind='r' AND c.relowner=(SELECT oid FROM pg_catalog.pg_roles WHERE rolname=current_user)
          AND has_table_privilege(c.oid,'SELECT') AND has_table_privilege(c.oid,'INSERT') AND has_table_privilege(c.oid,'DELETE') THEN 'ok' ELSE 'reject' END
        FROM pg_catalog.pg_class c WHERE c.oid='platform."__EFMigrationsHistory"'::regclass
        """), ["ok"]);
    stage = PreflightStage.RelationSafety;
    foreach (var relation in CatalogContract.Subjects.Select(s => s.Schema + "." + s.Table).Append("platform.\"__EFMigrationsHistory\""))
        PreflightContract.Exact(await Rows(CatalogContract.RelationSafetySql, relation), ["ok"]);
    stage = PreflightStage.HistoryCatalog;
    await HistoryCatalogVerification.VerifyAsync((sql, relation) => Rows(sql, relation));
    foreach (var subject in CatalogContract.Subjects)
    {
        var table = subject.Schema + "." + subject.Table; // Compile-time identifiers only.
        stage = PreflightStage.MappingPrivileges;
        PreflightContract.Exact(await Rows("""
            SELECT CASE WHEN c.relkind='r' AND NOT c.relrowsecurity AND NOT c.relforcerowsecurity
              AND c.relowner=(SELECT oid FROM pg_catalog.pg_roles WHERE rolname=current_user)
              AND has_table_privilege(c.oid,'SELECT') THEN 'ok' ELSE 'reject' END
            FROM pg_catalog.pg_class c WHERE c.oid=pg_catalog.to_regclass($1)
            """, table), ["ok"]);
        stage = PreflightStage.Columns;
        PreflightContract.Exact(await Rows(CatalogContract.ColumnsSql, table), CatalogContract.Columns(subject));
        stage = PreflightStage.Indexes;
        PreflightContract.Exact(await Rows(CatalogContract.IndexesSql, table), CatalogContract.Indexes(subject, selected.Migrated));
        stage = PreflightStage.Constraints;
        PreflightContract.Exact(await Rows(CatalogContract.ConstraintsSql, table), CatalogContract.Constraints(subject));
        stage = PreflightStage.Triggers;
        PreflightContract.Exact(await Rows("""
            SELECT tgname::text FROM pg_catalog.pg_trigger WHERE tgrelid=pg_catalog.to_regclass($1) AND NOT tgisinternal
            """, table), []);
        stage = PreflightStage.Duplicates;
        var duplicates = await Rows($"SELECT count(*)::text FROM (SELECT 1 FROM {table} GROUP BY \"ProviderKey\",\"IdentityScope\",\"ExternalId\" HAVING count(*)>1) duplicates");
        PreflightContract.Exact(duplicates, ["0"]);
        stage = PreflightStage.Statistics;
        var count = (await Rows($"SELECT count(*)::text FROM {table}"))[0];
        var bytes = (await Rows("SELECT pg_catalog.pg_total_relation_size(pg_catalog.to_regclass($1))::text", table))[0];
        Console.WriteLine("Mapping audit stage passed; values suppressed. Full audit still pending.");
    }
    stage = PreflightStage.Rollback;
    await Rows("ROLLBACK");
    Console.WriteLine("Read-only audit passed; selected identity/history/catalog verified. No deployment authorization implied.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(PreflightDiagnostics.Failure(stage, error));
    return 1;
}
