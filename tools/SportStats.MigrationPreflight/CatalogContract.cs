namespace SportStats.MigrationPreflight;

public static class CatalogContract
{
    public const string RelationSafetySql = """
        SELECT CASE WHEN c.relkind='r' AND NOT c.relispartition AND NOT c.relrowsecurity AND NOT c.relforcerowsecurity
          AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_inherits WHERE inhrelid=c.oid OR inhparent=c.oid)
          AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_trigger WHERE tgrelid=c.oid AND NOT tgisinternal)
          AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_rewrite WHERE ev_class=c.oid)
          THEN 'ok' ELSE 'reject' END FROM pg_catalog.pg_class c WHERE c.oid=pg_catalog.to_regclass($1)
        """;
    public sealed record Subject(string Schema, string Table, string Target, string Principal);
    public static readonly Subject[] Subjects = [new("platform","competition_external_ids","CompetitionId","competitions"), new("platform","season_external_ids","SeasonId","seasons"), new("football","team_external_ids","TeamId","teams"), new("football","match_external_ids","MatchId","matches")];
    public const string ColumnsSql = """
        SELECT a.attname || '|' || pg_catalog.format_type(a.atttypid,a.atttypmod) || '|' || a.attnotnull::text || '|' || a.attidentity::text || '|' || a.attgenerated::text || '|' || COALESCE(pg_catalog.pg_get_expr(d.adbin,d.adrelid),'') || '|' || CASE WHEN a.attcollation='pg_catalog.default'::regcollation THEN 'default' ELSE a.attcollation::text END
        FROM pg_catalog.pg_attribute a LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
        WHERE a.attrelid=pg_catalog.to_regclass($1) AND a.attnum>0 AND NOT a.attisdropped
        """;
    // Text must use the database's default collation; compare OIDs in SQL to avoid environment-specific OIDs.
    public static IEnumerable<string> Columns(Subject s) => new[] { "Id|uuid|true||||0", $"{s.Target}|uuid|true||||0", "ProviderKey|text|true||||default", "IdentityScope|text|true||||default", "ExternalId|text|true||||default" };
    public const string IndexesSql = """
        SELECT c.relname || '|' || i.indisunique::text || '|' || i.indisprimary::text || '|' || i.indisvalid::text || '|' || i.indisready::text || '|' || i.indislive::text || '|' || am.amname || '|' ||
          pg_catalog.pg_get_indexdef(i.indexrelid,0,false)
        FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class c ON c.oid=i.indexrelid JOIN pg_catalog.pg_am am ON am.oid=c.relam
        WHERE i.indrelid=pg_catalog.to_regclass($1)
        """;
    public static IEnumerable<string> Indexes(Subject s, bool migrated)
    {
        var keys = "\"ProviderKey\", \"IdentityScope\", \"ExternalId\"";
        var identity = $"IX_{s.Table}_ProviderKey_IdentityScope_ExternalId";
        if(s.Table == "competition_external_ids") identity = "IX_competition_external_ids_ProviderKey_IdentityScope_External~";
        yield return Index(s, $"PK_{s.Table}", "\"Id\"", true, true);
        yield return Index(s, $"IX_{s.Table}_{s.Target}", $"\"{s.Target}\"", false, false);
        yield return Index(s, identity, keys, false, false);
        if(migrated) yield return Index(s, $"UX_{s.Table}_identity", keys, true, false);
    }
    private static string Index(Subject s,string name,string keys,bool unique,bool primary) => $"{name}|{unique.ToString().ToLowerInvariant()}|{primary.ToString().ToLowerInvariant()}|true|true|true|btree|CREATE {(unique ? "UNIQUE " : "")}INDEX \"{name}\" ON {s.Schema}.{s.Table} USING btree ({keys})";
    public const string ConstraintsSql = """
        SELECT CASE WHEN c.contype='n' THEN
          'NOT NULL|' || COALESCE((SELECT a.attname::text FROM pg_catalog.pg_attribute a
            WHERE a.attrelid=c.conrelid AND cardinality(c.conkey)=1 AND a.attnum=c.conkey[1]
              AND a.attnum>0 AND NOT a.attisdropped AND a.attnotnull), '<invalid>')
          || '|' || c.convalidated::text || '|' || c.conenforced::text || '|' || c.condeferrable::text
          || '|' || c.condeferred::text || '|' || c.conislocal::text || '|' || c.coninhcount::text
          || '|' || c.connoinherit::text || '|' || c.conparentid::text || '|true'
        ELSE c.conname || '|' || c.convalidated::text || '|' || c.condeferrable::text || '|' || c.condeferred::text
          || '|' || pg_catalog.pg_get_constraintdef(c.oid,false) || '|' || c.conenforced::text END
        FROM pg_catalog.pg_constraint c WHERE c.conrelid=pg_catalog.to_regclass($1)
        """;
    public static IEnumerable<string> Constraints(Subject s) => new[] { $"PK_{s.Table}|true|false|false|PRIMARY KEY (\"Id\")|true", $"FK_{s.Table}_{s.Principal}_{s.Target}|true|false|false|FOREIGN KEY (\"{s.Target}\") REFERENCES {s.Schema}.{s.Principal}(\"Id\") ON DELETE RESTRICT|true" }
        .Concat(NotNullConstraints(["Id", s.Target, "ProviderKey", "IdentityScope", "ExternalId"]));

    // Generated NOT NULL names are incidental; column identity and semantic flags remain exact.
    // Missing, duplicate, invalid, inherited or unenforced constraints must still reject.
    public static IEnumerable<string> NotNullConstraints(IEnumerable<string> columns) =>
        columns.Select(column => $"NOT NULL|{column}|true|true|false|false|true|0|false|0|true");
}
