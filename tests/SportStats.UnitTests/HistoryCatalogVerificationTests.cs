using Npgsql;
using SportStats.MigrationPreflight;
using Xunit;

namespace SportStats.UnitTests;

public class HistoryCatalogVerificationTests
{
    [Fact]
    public async Task ExactCatalogUsesUnchangedQueriesAndRelation()
    {
        var calls = new List<string>();
        await HistoryCatalogVerification.VerifyAsync((sql, relation) =>
        {
            Assert.Equal(HistoryCatalogVerification.Relation, relation);
            var operation = (HistoryCatalogOperation)calls.Count;
            calls.Add(sql);
            return Task.FromResult(HistoryCatalogVerification.Expected(operation).Reverse().ToList());
        });
        Assert.Equal(new[] { CatalogContract.ColumnsSql, CatalogContract.ConstraintsSql, CatalogContract.IndexesSql }, calls);
    }

    [Theory]
    [InlineData(HistoryCatalogOperation.Columns)]
    [InlineData(HistoryCatalogOperation.Constraints)]
    [InlineData(HistoryCatalogOperation.Indexes)]
    public async Task MissingExtraChangedAndDuplicateRowsFailAtExactOperation(HistoryCatalogOperation selected)
    {
        foreach (var change in new Func<List<string>, List<string>>[]
        {
            rows => rows.Skip(1).ToList(), rows => rows.Append("sensitive-extra").ToList(),
            rows => rows.Select(row => row + "sensitive-change").ToList(), rows => rows.Append(rows[0]).ToList()
        })
        {
            var call = 0;
            var error = await Assert.ThrowsAsync<HistoryCatalogException>(() => HistoryCatalogVerification.VerifyAsync((_, _) =>
            {
                var operation = (HistoryCatalogOperation)call++;
                var rows = HistoryCatalogVerification.Expected(operation).ToList();
                return Task.FromResult(operation == selected ? change(rows) : rows);
            }));
            Assert.Equal(selected, error.Operation);
            Assert.Equal((int)selected + 1, call);
            Assert.Contains($"/{selected.ToString().ToUpperInvariant()}_MISMATCH]", PreflightDiagnostics.Failure(PreflightStage.HistoryCatalog, error));
        }
    }

    [Fact]
    public async Task UnexpectedPostgreSql18NotNullCatalogRowsRejectWithoutBypass()
    {
        var call = 0;
        var error = await Assert.ThrowsAsync<HistoryCatalogException>(() => HistoryCatalogVerification.VerifyAsync((_, _) =>
        {
            var operation = (HistoryCatalogOperation)call++;
            var rows = HistoryCatalogVerification.Expected(operation).ToList();
            if (operation == HistoryCatalogOperation.Constraints)
                rows.Add("synthetic_not_null|true|false|false|NOT NULL \"ProductVersion\"");
            return Task.FromResult(rows);
        }));
        Assert.True(error.Mismatch);
        Assert.Equal(HistoryCatalogOperation.Constraints, error.Operation);
        Assert.Equal(2, call);
    }

    [Fact]
    public async Task PostgreSql18HistoryAcceptsIndependentExpectedRows()
    {
        await HistoryCatalogVerification.VerifyAsync((sql, _) => Task.FromResult(sql == CatalogContract.ConstraintsSql
            ? new List<string> {
                "PK___EFMigrationsHistory|true|false|false|PRIMARY KEY (\"MigrationId\")|true",
                "NOT NULL|MigrationId|true|true|false|false|true|0|false|0|true",
                "NOT NULL|ProductVersion|true|true|false|false|true|0|false|0|true" }
            : HistoryCatalogVerification.Expected(sql == CatalogContract.ColumnsSql
                ? HistoryCatalogOperation.Columns : HistoryCatalogOperation.Indexes).ToList()));
    }

    [Fact]
    public void PostgreSql18MappingCatalogRejectsConstraintAndNullabilityDrift()
    {
        foreach (var subject in CatalogContract.Subjects)
        {
            var actual = new[] {
                $"PK_{subject.Table}|true|false|false|PRIMARY KEY (\"Id\")|true",
                $"FK_{subject.Table}_{subject.Principal}_{subject.Target}|true|false|false|FOREIGN KEY (\"{subject.Target}\") REFERENCES {subject.Schema}.{subject.Principal}(\"Id\") ON DELETE RESTRICT|true",
                "NOT NULL|Id|true|true|false|false|true|0|false|0|true",
                $"NOT NULL|{subject.Target}|true|true|false|false|true|0|false|0|true",
                "NOT NULL|ProviderKey|true|true|false|false|true|0|false|0|true",
                "NOT NULL|IdentityScope|true|true|false|false|true|0|false|0|true",
                "NOT NULL|ExternalId|true|true|false|false|true|0|false|0|true" };
            var expected = CatalogContract.Constraints(subject).ToArray();
            PreflightContract.Exact(actual.Reverse(), expected);
            foreach (var row in actual)
            {
                Assert.Throws<InvalidOperationException>(() => PreflightContract.Exact(actual.Where(x => x != row), expected));
                Assert.Throws<InvalidOperationException>(() => PreflightContract.Exact(actual.Append(row), expected));
                Assert.Throws<InvalidOperationException>(() => PreflightContract.Exact(actual.Select(x => x == row ? x + "drift" : x), expected));
            }
            Assert.Throws<InvalidOperationException>(() => PreflightContract.Exact(actual.Append("unexpected CHECK"), expected));
            Assert.Throws<InvalidOperationException>(() => PreflightContract.Exact(actual.Select(x => x.Replace("PRIMARY KEY (\"Id\")", "PRIMARY KEY (\"ExternalId\")")), expected));
            var columns = CatalogContract.Columns(subject).ToArray();
            Assert.Throws<InvalidOperationException>(() => PreflightContract.Exact(columns.Select(x => x.Replace("|true|", "|false|")), columns));
            foreach (var notNull in actual.Skip(2))
            {
                var fields = notNull.Split('|');
                for (var flag = 2; flag < fields.Length; flag++)
                {
                    var changed = (string[])fields.Clone();
                    changed[flag] = fields[flag] == "true" ? "false" : fields[flag] == "false" ? "true" : "1";
                    Assert.Throws<InvalidOperationException>(() => PreflightContract.Exact(actual.Select(x => x == notNull ? string.Join('|', changed) : x), expected));
                }
            }
        }
    }

    [Fact]
    public void ConstraintQueryRetainsEveryRowAndValidatesColumnAssociation()
    {
        Assert.Contains("CASE WHEN c.contype='n'", CatalogContract.ConstraintsSql);
        Assert.Contains("cardinality(c.conkey)=1", CatalogContract.ConstraintsSql);
        Assert.Contains("NOT a.attisdropped AND a.attnotnull", CatalogContract.ConstraintsSql);
        Assert.Contains("'<invalid>'", CatalogContract.ConstraintsSql);
        Assert.Contains("c.conenforced", CatalogContract.ConstraintsSql);
        Assert.DoesNotContain("contype<>", CatalogContract.ConstraintsSql);
    }

    [Fact]
    public async Task EveryOperationClassifiesFailuresWithoutRenderingSensitiveContent()
    {
        foreach (var selected in Enum.GetValues<HistoryCatalogOperation>())
        foreach (var (cause, reason) in new (Exception, string)[]
        {
            (new PostgresException("secret", "secret", "ERROR", "42501"), "PERMISSION_DENIED"),
            (new PostgresException("secret", "secret", "ERROR", "57014"), "QUERY_CANCELED"),
            (new PostgresException("secret", "secret", "ERROR", "55P03"), "LOCK_UNAVAILABLE"),
            (new PostgresException("secret", "secret", "ERROR", "42601"), "SERVER_REJECTED"),
            (new NpgsqlException("secret"), "PROVIDER_OR_CONNECTIVITY"),
            (new InvalidCastException("secret"), "TYPE_CONVERSION_FAILED"),
            (new InvalidOperationException("secret"), "OPERATION_FAILED"),
            (new TimeoutException("secret"), "TIMEOUT_OR_CANCELED"),
            (new OperationCanceledException("secret"), "TIMEOUT_OR_CANCELED"),
            (new Exception("secret"), "UNCLASSIFIED")
        })
        {
            var call = 0;
            var error = await Assert.ThrowsAsync<HistoryCatalogException>(() => HistoryCatalogVerification.VerifyAsync((_, _) =>
            {
                var operation = (HistoryCatalogOperation)call++;
                return operation == selected ? Task.FromException<List<string>>(cause)
                    : Task.FromResult(HistoryCatalogVerification.Expected(operation).ToList());
            }));
            Assert.False(error.Mismatch);
            Assert.Equal((int)selected + 1, call);
            Assert.Equal($"Read-only preflight failed [PF_HISTORY_CATALOG/{selected.ToString().ToUpperInvariant()}_{reason}]. No migration was attempted. Stop; raw errors are suppressed.",
                PreflightDiagnostics.Failure(PreflightStage.HistoryCatalog, error));
        }
        Assert.Contains("/UNKNOWN_UNCLASSIFIED]", PreflightDiagnostics.Failure(PreflightStage.HistoryCatalog,
            new HistoryCatalogException((HistoryCatalogOperation)999, false)));
    }
}
