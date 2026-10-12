using Microsoft.Extensions.Configuration;
using Npgsql;

namespace SportStats.Infrastructure.Configuration;

internal static class DatabaseConnectionString
{
    private const string ConfigurationError =
        "Configure a valid ConnectionStrings:SportStats value using application-role credentials in a secret configuration source.";

    public static string GetApplicationConnectionString(IConfiguration configuration)
    {
        var value = configuration.GetConnectionString("SportStats");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(ConfigurationError);
        }

        try
        {
            var connection = new NpgsqlConnectionStringBuilder(value);
            if (string.IsNullOrWhiteSpace(connection.Host) ||
                string.IsNullOrWhiteSpace(connection.Database) ||
                string.IsNullOrWhiteSpace(connection.Username))
            {
                throw new ArgumentException();
            }

            connection.IncludeErrorDetail = false;
            connection.LogParameters = false;
            connection.PersistSecurityInfo = false;
            return connection.ConnectionString;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            // Parser exceptions can contain configuration input; do not preserve them.
            throw new InvalidOperationException(ConfigurationError);
        }
    }
}
