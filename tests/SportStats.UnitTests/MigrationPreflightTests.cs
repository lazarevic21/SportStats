using Npgsql;
using SportStats.MigrationPreflight;
using Xunit;

namespace SportStats.UnitTests;

public class MigrationPreflightTests
{
    private static string[] Arguments => ["--host","127.0.0.1","--port","5432","--database","sportstats_test","--role","sportstats_migrator","--expect","baseline","--system-identifier","123456789","--approve-read-only-audit"];
    private const string Valid = "Host=127.0.0.1;Port=5432;Database=sportstats_test;Username=sportstats_migrator;Password=secret";
    [Fact] public void ExplicitSelectionEnforcesEveryGuard()
    {
        Assert.False(PreflightContract.Parse(Arguments).Migrated);
        for(var i=0;i<Arguments.Length;i++)
        {
            var changed=Arguments; changed[i]="invalid";
            Assert.Throws<InvalidOperationException>(()=>PreflightContract.Parse(changed));
        }
        Assert.Throws<InvalidOperationException>(()=>PreflightContract.Parse([]));
        Assert.Throws<InvalidOperationException>(()=>PreflightContract.Parse(Arguments.Concat(["extra"]).ToArray()));
    }
    [Theory]
    [InlineData("Host=localhost;Port=5432;Database=sportstats_test;Username=sportstats_migrator")]
    [InlineData("Host=127.0.0.1;Database=sportstats_test;Username=sportstats_migrator")]
    [InlineData("Host=127.0.0.1;Port=5433;Database=sportstats_test;Username=sportstats_migrator")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=sportstats_dev;Username=sportstats_migrator")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=sportstats_test;Username=postgres")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=sportstats_test;Username=sportstats_migrator;Options=-c default_transaction_read_only=off")]
    [InlineData("Host=127.0.0.1,production;Port=5432;Database=sportstats_test;Username=sportstats_migrator")]
    [InlineData(null)]
    [InlineData("Password=secret;bad")]
    public void RejectsFallbacksOverridesAndOtherTargets(string? value)
    {
        var error=Assert.Throws<InvalidOperationException>(()=>PreflightContract.Connection(value,PreflightContract.Parse(Arguments)));
        Assert.DoesNotContain("secret",error.ToString()); Assert.Null(error.InnerException);
    }
    [Fact] public void ConnectionStartsReadOnlyWithBoundedUnpooledSession()
    {
        var b=new NpgsqlConnectionStringBuilder(PreflightContract.Connection(Valid,PreflightContract.Parse(Arguments)));
        Assert.Contains("default_transaction_read_only=on",b.Options);
        Assert.Contains("statement_timeout=15000",b.Options);
        Assert.False(b.Pooling); Assert.False(b.Enlist); Assert.False(b.IncludeErrorDetail); Assert.False(b.LogParameters);
        Assert.Contains("READ ONLY",PreflightContract.BeginReadOnly);
    }
    [Fact] public void ConfigGuardsRejectOverridesEvenWithValidPassword()
    {
        var selected = PreflightContract.Parse(Arguments);
        foreach (var (key, value) in new[]
        {
            ("Host", "production"), ("Host", "localhost"), ("Host", "127.0.0.1,production"),
            ("Database", "sportstats_dev"), ("Username", "postgres"), ("Port", "5433"),
            ("Options", "-c default_transaction_read_only=off"), ("Passfile", "unused"),
            ("Include Error Detail", "true"), ("Password", "")
        })
        {
            var configuration = new NpgsqlConnectionStringBuilder(Valid);
            configuration[key] = value;
            Assert.Throws<InvalidOperationException>(() => PreflightContract.Connection(configuration.ConnectionString, selected));
        }
        var missingPassword = new NpgsqlConnectionStringBuilder(Valid);
        missingPassword.Remove("Password");
        Assert.Throws<InvalidOperationException>(() => PreflightContract.Connection(missingPassword.ConnectionString, selected));
    }
    [Fact] public void RejectsUnexpectedHistoryIncludingDuplicatesAndReordering()
    {
        PreflightContract.History([PreflightContract.Initial],false);
        PreflightContract.History([PreflightContract.Initial,PreflightContract.Target],true);
        foreach(var history in new string[][] { [],[PreflightContract.Target],[PreflightContract.Initial,PreflightContract.Initial],[PreflightContract.Initial,"future"],[PreflightContract.Target,PreflightContract.Initial] })
            Assert.Throws<InvalidOperationException>(()=>PreflightContract.History(history,true));
        Assert.Throws<InvalidOperationException>(()=>PreflightContract.History([PreflightContract.Initial,PreflightContract.Target],false));
    }
    [Fact] public void ExactCatalogRejectsMissingExtraOrChangedObjects()
    {
        foreach(var subject in CatalogContract.Subjects)
        {
            var expected=CatalogContract.Indexes(subject,true).ToArray();
            Assert.Equal(4,expected.Length);
            Assert.Throws<InvalidOperationException>(()=>PreflightContract.Exact(expected.Skip(1),expected));
            Assert.Throws<InvalidOperationException>(()=>PreflightContract.Exact(expected.Append("unexpected"),expected));
            Assert.Throws<InvalidOperationException>(()=>PreflightContract.Exact(expected.Select(x=>x.Replace("|true|true|btree", "|false|true|btree")),expected));
            Assert.Equal(5,CatalogContract.Columns(subject).Count()); Assert.Equal(7,CatalogContract.Constraints(subject).Count());
        }
    }
}
