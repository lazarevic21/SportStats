namespace SportStats.MigrationPreflight;

public enum HistoryCatalogOperation { Columns, Constraints, Indexes }

// Retain the cause for classification only; never render this exception or its inner error.
public sealed class HistoryCatalogException(HistoryCatalogOperation operation, bool mismatch, Exception? cause = null)
    : Exception("History catalog verification failed.", cause)
{
    public HistoryCatalogOperation Operation { get; } = operation;
    public bool Mismatch { get; } = mismatch;
}

public static class HistoryCatalogVerification
{
    public const string Relation = "platform.\"__EFMigrationsHistory\"";

    public static string[] Expected(HistoryCatalogOperation operation) => operation switch
    {
        HistoryCatalogOperation.Columns => ["MigrationId|character varying(150)|true||||default", "ProductVersion|character varying(32)|true||||default"],
        HistoryCatalogOperation.Constraints => new[] { "PK___EFMigrationsHistory|true|false|false|PRIMARY KEY (\"MigrationId\")|true" }
            .Concat(CatalogContract.NotNullConstraints(["MigrationId", "ProductVersion"])).ToArray(),
        HistoryCatalogOperation.Indexes => ["PK___EFMigrationsHistory|true|true|true|true|true|btree|CREATE UNIQUE INDEX \"PK___EFMigrationsHistory\" ON platform.\"__EFMigrationsHistory\" USING btree (\"MigrationId\")"],
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    public static async Task VerifyAsync(Func<string, string, Task<List<string>>> rows)
    {
        foreach (var operation in Enum.GetValues<HistoryCatalogOperation>())
        {
            var sql = operation switch
            {
                HistoryCatalogOperation.Columns => CatalogContract.ColumnsSql,
                HistoryCatalogOperation.Constraints => CatalogContract.ConstraintsSql,
                HistoryCatalogOperation.Indexes => CatalogContract.IndexesSql,
                _ => throw new InvalidOperationException()
            };
            List<string> actual;
            try { actual = await rows(sql, Relation); }
            catch (Exception cause) { throw new HistoryCatalogException(operation, false, cause); }
            // Only this explicit comparison can produce MISMATCH; provider failures cannot.
            if (!actual.Order(StringComparer.Ordinal).SequenceEqual(Expected(operation).Order(StringComparer.Ordinal)))
                throw new HistoryCatalogException(operation, true);
        }
    }
}
