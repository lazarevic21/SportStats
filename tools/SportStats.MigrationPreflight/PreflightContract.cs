using Npgsql;
using NpgsqlTypes;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;

namespace SportStats.MigrationPreflight;

public static class PreflightContract
{
    public const string Initial = "20261012165516_InitialCreate";
    public const string Target = "20261019102703_ExternalIdentityUniqueness";
    public const string ConnectionVariable = "SPORTSTATS_MIGRATION_PREFLIGHT_CONNECTION_STRING";
    public const string BeginReadOnly = "BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY";
    public sealed record Selection(string Host, int Port, string Database, string Role, bool Migrated, string SystemIdentifier);
    public static Selection Parse(string[] args)
    {
        Require(args.Length == 13 && args[0] == "--host" && args[2] == "--port" && args[4] == "--database" && args[6] == "--role" && args[8] == "--expect" && args[10] == "--system-identifier" && args[12] == "--approve-read-only-audit");
        Require(args[1] == "127.0.0.1" && int.TryParse(args[3], out var port) && port is > 0 and <= 65535);
        Require(args[5] == "sportstats_test" && args[7] == "sportstats_migrator" && args[9] is "baseline" or "migrated");
        Require(args[11].Length is >= 1 and <= 20 && args[11].All(char.IsAsciiDigit));
        return new(args[1], int.Parse(args[3]), args[5], args[7], args[9] == "migrated", args[11]);
    }
    public static string Connection(string? value, Selection selection)
    {
        Require(selection.Host == "127.0.0.1" && selection.Port is > 0 and <= 65535 && selection.Database == "sportstats_test" && selection.Role == "sportstats_migrator" && selection.SystemIdentifier.Length is >= 1 and <= 20 && selection.SystemIdentifier.All(char.IsAsciiDigit));
        try
        {
            Require(!string.IsNullOrWhiteSpace(value));
            var b = new NpgsqlConnectionStringBuilder(value);
            // Reject transport/session overrides: only these explicit fields may be supplied.
            var allowed = new[] { "Host", "Port", "Database", "Username", "Password", "SSL Mode" };
            Require(b.Keys.Cast<string>().All(k => allowed.Contains(k, StringComparer.OrdinalIgnoreCase)));
            Require(b.ContainsKey("Port") && b.ContainsKey("Password") && !string.IsNullOrWhiteSpace(b.Password) && b.Host == selection.Host && b.Port == selection.Port && b.Database == selection.Database && b.Username == selection.Role);
            b.Pooling = false; b.Enlist = false; b.Timeout = 5; b.CommandTimeout = 20;
            b.IncludeErrorDetail = false; b.LogParameters = false; b.PersistSecurityInfo = false;
            b.Options = "-c default_transaction_read_only=on -c statement_timeout=15000 -c lock_timeout=3000 -c search_path=pg_catalog";
            b.ApplicationName = "SportStats.MigrationPreflight";
            return b.ConnectionString;
        }
        catch { throw new InvalidOperationException("Preflight target/configuration guard failed."); }
    }
    public static void History(IEnumerable<string> actual, bool migrated) => Require(actual.SequenceEqual(migrated ? new[] { Initial, Target } : new[] { Initial }));
    public const string IdentitySql = """
        SELECT current_database() || '|' || current_user || '|' || session_user || '|' ||
          inet_server_port()::text || '|' || current_setting('transaction_read_only') || '|' || system_identifier::text,
          inet_server_addr()
        FROM pg_catalog.pg_control_system()
        """;
    public sealed record IdentityRow(string? Fields, NpgsqlInet? Address);

    // IPAddress alone would discard the prefix. Keep the complete PostgreSQL inet.
    public static IdentityRow ReadIdentityRow(DbDataReader reader)
    {
        if (reader.FieldCount != 2) throw new IdentityVerificationException(IdentityFailure.Shape);
        return new(reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? (NpgsqlInet?)null : reader.GetFieldValue<NpgsqlInet>(1));
    }

    // First failure wins; later fields have not been certified when a check fails.
    public static void Identity(IReadOnlyList<IdentityRow> actual, Selection selected)
    {
        void Check(bool condition, IdentityFailure failure)
        {
            if (!condition) throw new IdentityVerificationException(failure);
        }
        Check(actual.Count == 1, IdentityFailure.RowCount);
        var row = actual[0];
        Check(row is not null && row.Fields is not null, IdentityFailure.Shape);
        var fields = row!.Fields!.Split('|');
        Check(fields.Length == 6, IdentityFailure.Shape);
        Check(fields[0] == selected.Database, IdentityFailure.Database);
        Check(fields[1] == selected.Role, IdentityFailure.CurrentRole);
        Check(fields[2] == selected.Role, IdentityFailure.SessionRole);
        Check(selected.Host == "127.0.0.1" && row.Address is { } address &&
            address.Address is { AddressFamily: AddressFamily.InterNetwork } &&
            address.Netmask == 32 && address.Address.Equals(IPAddress.Parse("127.0.0.1")), IdentityFailure.Address);
        Check(fields[3] == selected.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), IdentityFailure.Port);
        Check(fields[4] == "on", IdentityFailure.ReadOnly);
        Check(fields[5] == selected.SystemIdentifier, IdentityFailure.Cluster);
    }
    public static void Exact(IEnumerable<string> actual, IEnumerable<string> expected) => Require(actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)));
    public static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Preflight guard failed; stop and review the selected target and catalog."); }
}
