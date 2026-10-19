using Npgsql;
using SportStats.MigrationPreflight;
using Xunit;

namespace SportStats.UnitTests;

public class PreflightDiagnosticsTests
{
    [Theory]
    [InlineData("28P01", "AUTHENTICATION")]
    [InlineData("28000", "AUTHENTICATION")]
    [InlineData("42501", "PERMISSION_DENIED")]
    [InlineData("57014", "QUERY_CANCELED")]
    [InlineData("55P03", "LOCK_UNAVAILABLE")]
    [InlineData("3D000", "DATABASE_UNAVAILABLE")]
    [InlineData("XXXXX", "SERVER_REJECTED")]
    public void ServerErrorsOnlyExposeAllowlistedClassification(string sqlState, string reason)
    {
        var error = new PostgresException("secret-password", "sensitive-severity", "ERROR", sqlState,
            detail: "sensitive-detail", hint: "sensitive-hint", internalQuery: "sensitive-sql",
            where: "sensitive-where", schemaName: "sensitive-schema", tableName: "sensitive-table",
            columnName: "sensitive-column", constraintName: "sensitive-constraint")
        {
            Data = { ["connection-string"] = "sensitive-identity" }
        };
        Assert.Equal($"Read-only preflight failed [PF_IDENTITY_READ/{reason}]. No migration was attempted. Stop; raw errors are suppressed.",
            PreflightDiagnostics.Failure(PreflightStage.IdentityRead, error));
    }

    [Fact]
    public void EveryStageHasUniqueFixedCodeAndUnknownEnumsAreSanitized()
    {
        var messages = Enum.GetValues<PreflightStage>()
            .Select(stage => PreflightDiagnostics.Failure(stage, new InvalidOperationException("secret"))).ToArray();
        Assert.Equal(messages.Length, messages.Distinct().Count());
        Assert.All(messages, message =>
        {
            Assert.DoesNotContain("secret", message);
            Assert.DoesNotContain("UNKNOWN", message);
            Assert.Matches(@"^Read-only preflight failed \[PF_[A-Z_]+/(GUARD_OR_OPERATION_FAILED|IDENTITY_OPERATION_FAILED)\]\. No migration was attempted\. Stop; raw errors are suppressed\.$", message);
        });
        Assert.Contains("[PF_UNKNOWN/UNCLASSIFIED]",
            PreflightDiagnostics.Failure((PreflightStage)int.MaxValue, new Exception("secret")));
    }

    [Fact]
    public void ClientErrorsNeverRenderMessagesInnerErrorsOrData()
    {
        foreach (var (error, reason) in new (Exception, string)[]
        {
            (new TimeoutException("secret"), "TIMEOUT_OR_CANCELED"),
            (new OperationCanceledException("secret"), "TIMEOUT_OR_CANCELED"),
            (new NpgsqlException("secret", new Exception("connection-string")), "PROVIDER_OR_CONNECTIVITY"),
            (new InvalidOperationException("identity-value"), "GUARD_OR_OPERATION_FAILED"),
            (new Exception("secret"), "UNCLASSIFIED")
        })
        {
            Assert.Equal($"Read-only preflight failed [PF_CONNECT/{reason}]. No migration was attempted. Stop; raw errors are suppressed.",
                PreflightDiagnostics.Failure(PreflightStage.Connect, error));
        }
    }
}
