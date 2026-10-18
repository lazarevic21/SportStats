using Npgsql;

namespace SportStats.InitialImport;

public static class ImportDatabaseConfiguration
{
    public static string Validate(string? value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException();
            var settings = new NpgsqlConnectionStringBuilder(value);
            if (settings.Database != "sportstats_test" || settings.Username != "sportstats_app" ||
                string.IsNullOrWhiteSpace(settings.Host)) throw new ArgumentException();
            settings.Enlist = false;
            settings.Pooling = false;
            settings.IncludeErrorDetail = false;
            settings.LogParameters = false;
            settings.PersistSecurityInfo = false;
            return settings.ConnectionString;
        }
        catch
        {
            throw new InvalidOperationException("Explicit sportstats_test application-role configuration is required.");
        }
    }
}
