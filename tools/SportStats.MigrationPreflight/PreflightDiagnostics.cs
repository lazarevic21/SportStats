using Npgsql;

namespace SportStats.MigrationPreflight;

public enum IdentityFailure
{
    RowCount, Shape, Database, CurrentRole, SessionRole,
    Address, Port, ReadOnly, Cluster
}

public sealed class IdentityVerificationException(IdentityFailure failure) : Exception("Identity verification failed.")
{
    public IdentityFailure Failure { get; } = failure;
}

public enum PreflightStage
{
    Arguments, Configuration, Connect, BeginReadOnly, IdentityRead, IdentityMatch,
    RolePrivileges, SchemaPrivileges, History, HistoryPrivileges, RelationSafety,
    HistoryCatalog, MappingPrivileges, Columns, Indexes, Constraints, Triggers,
    Duplicates, Statistics, Rollback
}

public static class PreflightDiagnostics
{
    // Only compile-time tokens and allowlisted SQLSTATE classifications reach output.
    // Never inspect exception messages, details, query text or inner exception strings.
    public static string Failure(PreflightStage stage, Exception error)
    {
        if (stage == PreflightStage.HistoryCatalog && error is HistoryCatalogException catalog)
        {
            var operation = catalog.Operation switch
            {
                HistoryCatalogOperation.Columns => "COLUMNS",
                HistoryCatalogOperation.Constraints => "CONSTRAINTS",
                HistoryCatalogOperation.Indexes => "INDEXES",
                _ => "UNKNOWN"
            };
            var classification = catalog.Mismatch ? "MISMATCH" : catalog.InnerException switch
            {
                PostgresException { SqlState: "42501" } => "PERMISSION_DENIED",
                PostgresException { SqlState: "57014" } => "QUERY_CANCELED",
                PostgresException { SqlState: "55P03" } => "LOCK_UNAVAILABLE",
                PostgresException => "SERVER_REJECTED",
                OperationCanceledException or TimeoutException => "TIMEOUT_OR_CANCELED",
                NpgsqlException => "PROVIDER_OR_CONNECTIVITY",
                InvalidCastException => "TYPE_CONVERSION_FAILED",
                InvalidOperationException => "OPERATION_FAILED",
                _ => "UNCLASSIFIED"
            };
            return $"Read-only preflight failed [PF_HISTORY_CATALOG/{operation}_{classification}]. No migration was attempted. Stop; raw errors are suppressed.";
        }
        var stageCode = stage switch
        {
            PreflightStage.Arguments => "ARGUMENTS",
            PreflightStage.Configuration => "CONFIGURATION",
            PreflightStage.Connect => "CONNECT",
            PreflightStage.BeginReadOnly => "BEGIN_READ_ONLY",
            PreflightStage.IdentityRead => "IDENTITY_READ",
            PreflightStage.IdentityMatch => "IDENTITY_MATCH",
            PreflightStage.RolePrivileges => "ROLE_PRIVILEGES",
            PreflightStage.SchemaPrivileges => "SCHEMA_PRIVILEGES",
            PreflightStage.History => "HISTORY",
            PreflightStage.HistoryPrivileges => "HISTORY_PRIVILEGES",
            PreflightStage.RelationSafety => "RELATION_SAFETY",
            PreflightStage.HistoryCatalog => "HISTORY_CATALOG",
            PreflightStage.MappingPrivileges => "MAPPING_PRIVILEGES",
            PreflightStage.Columns => "COLUMNS",
            PreflightStage.Indexes => "INDEXES",
            PreflightStage.Constraints => "CONSTRAINTS",
            PreflightStage.Triggers => "TRIGGERS",
            PreflightStage.Duplicates => "DUPLICATES",
            PreflightStage.Statistics => "STATISTICS",
            PreflightStage.Rollback => "ROLLBACK",
            _ => "UNKNOWN"
        };
        var reason = error switch
        {
            IdentityVerificationException identity when stage == PreflightStage.IdentityMatch => identity.Failure switch
            {
                IdentityFailure.RowCount => "IDENTITY_ROW_COUNT",
                IdentityFailure.Shape => "IDENTITY_SHAPE",
                IdentityFailure.Database => "DATABASE_MISMATCH",
                IdentityFailure.CurrentRole => "CURRENT_ROLE_MISMATCH",
                IdentityFailure.SessionRole => "SESSION_ROLE_MISMATCH",
                IdentityFailure.Address => "ADDRESS_MISMATCH",
                IdentityFailure.Port => "PORT_MISMATCH",
                IdentityFailure.ReadOnly => "READ_ONLY_NOT_VERIFIED",
                IdentityFailure.Cluster => "CLUSTER_MISMATCH",
                _ => "IDENTITY_OPERATION_FAILED"
            },
            PostgresException { SqlState: "28P01" or "28000" } => "AUTHENTICATION",
            PostgresException { SqlState: "42501" } => "PERMISSION_DENIED",
            PostgresException { SqlState: "57014" } => "QUERY_CANCELED",
            PostgresException { SqlState: "55P03" } => "LOCK_UNAVAILABLE",
            PostgresException { SqlState: "3D000" } => "DATABASE_UNAVAILABLE",
            PostgresException => "SERVER_REJECTED",
            OperationCanceledException or TimeoutException => "TIMEOUT_OR_CANCELED",
            NpgsqlException => "PROVIDER_OR_CONNECTIVITY",
            InvalidCastException when stage is PreflightStage.IdentityRead or PreflightStage.IdentityMatch => "IDENTITY_TYPE_CONVERSION_FAILED",
            InvalidOperationException when stage == PreflightStage.IdentityMatch => "IDENTITY_OPERATION_FAILED",
            InvalidOperationException => "GUARD_OR_OPERATION_FAILED",
            _ when stage == PreflightStage.IdentityMatch => "IDENTITY_UNEXPECTED_EXCEPTION",
            _ => "UNCLASSIFIED"
        };
        return $"Read-only preflight failed [PF_{stageCode}/{reason}]. No migration was attempted. Stop; raw errors are suppressed.";
    }
}
