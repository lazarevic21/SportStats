using Npgsql;

namespace SportStats.InitialImport.Verification;

internal sealed record IdentityIndexState(string Schema, string Table, string Name,
    bool Unique, bool Valid, bool Ready, bool HasPredicate, bool HasExpression,
    string[] Columns, int AttributeCount);

internal static class DisposableVerificationContract
{
    internal const string InitialMigration = "20261012165516_InitialCreate";
    internal const string TargetMigration = "20261019102703_ExternalIdentityUniqueness";

    internal static NpgsqlConnectionStringBuilder ValidateConfiguration(string[] args, string? approval,
        string? marker, string? connectionString)
    {
        if (args.Length != 1 || args[0] != "--approved-disposable-commit-replay" ||
            approval != "I APPROVE DISPOSABLE IMPORT COMMITS" || !Guid.TryParseExact(marker, "N", out _))
            throw new InvalidOperationException();
        NpgsqlConnectionStringBuilder settings;
        try { settings = new(connectionString ?? ""); }
        catch { throw new InvalidOperationException(); }
        if (settings.Host != "127.0.0.1" || settings.Database != "sportstats_test" ||
            settings.Username != "postgres" || settings.Port == 5432 || settings.Port <= 0 ||
            !string.IsNullOrEmpty(settings.Options) || string.IsNullOrEmpty(settings.Password))
            throw new InvalidOperationException();
        settings.Pooling = false; settings.Enlist = false; settings.IncludeErrorDetail = false;
        settings.LogParameters = false; settings.PersistSecurityInfo = false;
        settings.Timeout = 10; settings.CommandTimeout = 20; settings.CancellationTimeout = 5000;
        return settings;
    }

    internal static bool ValidIndex(string expectedSchema, string expectedTable, IdentityIndexState state) =>
        state.Schema == expectedSchema && state.Table == expectedTable && state.Name == $"UX_{expectedTable}_identity" &&
        state.Unique && state.Valid && state.Ready && !state.HasPredicate && !state.HasExpression &&
        state.AttributeCount == 3 && state.Columns.SequenceEqual(new[] { "ProviderKey", "IdentityScope", "ExternalId" });

    internal static bool ValidHistory(IEnumerable<string> history, bool upgraded) =>
        history.SequenceEqual(upgraded ? new[] { InitialMigration, TargetMigration } : new[] { InitialMigration });
}
