using System.Data;
using NpgsqlTypes;
using SportStats.MigrationPreflight;
using Xunit;

namespace SportStats.UnitTests;

public class IdentityVerificationTests
{
    private static readonly PreflightContract.Selection Selected = new("127.0.0.1", 5432,
        "sportstats_test", "sportstats_migrator", false, "7695811824955025708");
    private const string Valid = "sportstats_test|sportstats_migrator|sportstats_migrator|5432|on|7695811824955025708";
    private static PreflightContract.IdentityRow Row(string? fields = Valid, NpgsqlInet? address = null) =>
        new(fields, address ?? new NpgsqlInet("127.0.0.1/32"));

    [Fact]
    public void ExactIpv4HostEndpointPasses() => PreflightContract.Identity([Row()], Selected);

    [Theory]
    [InlineData("127.0.0.2/32")]
    [InlineData("0.0.0.0/32")]
    [InlineData("::1/128")]
    [InlineData("::ffff:127.0.0.1/128")]
    public void RejectsOtherAddressesAndFamilies(string address) => AssertAddressRejected(new NpgsqlInet(address));

    [Fact]
    public void RejectsEveryNonHostIpv4Prefix()
    {
        for (var prefix = 0; prefix < 32; prefix++)
            AssertAddressRejected(new NpgsqlInet($"127.0.0.1/{prefix}"));
    }

    [Fact]
    public void RejectsNullAndDefaultInet()
    {
        AssertAddressRejected(null);
        AssertAddressRejected(default(NpgsqlInet));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.2")]
    [InlineData("::1")]
    public void RejectsSelectionAliasesEvenWithCorrectServerAddress(string host)
    {
        var error = Assert.Throws<IdentityVerificationException>(() => PreflightContract.Identity([Row()], Selected with { Host = host }));
        Assert.Equal(IdentityFailure.Address, error.Failure);
    }

    private static void AssertAddressRejected(NpgsqlInet? address)
    {
        var error = Assert.Throws<IdentityVerificationException>(() =>
            PreflightContract.Identity([new(Valid, address)], Selected));
        Assert.Equal(IdentityFailure.Address, error.Failure);
        Assert.Contains("[PF_IDENTITY_MATCH/ADDRESS_MISMATCH]", PreflightDiagnostics.Failure(PreflightStage.IdentityMatch, error));
    }

    [Theory]
    [InlineData(0, "secret", "DATABASE_MISMATCH")]
    [InlineData(1, "secret", "CURRENT_ROLE_MISMATCH")]
    [InlineData(2, "secret", "SESSION_ROLE_MISMATCH")]
    [InlineData(3, "05432", "PORT_MISMATCH")]
    [InlineData(4, "off", "READ_ONLY_NOT_VERIFIED")]
    [InlineData(5, "7695811824955025709", "CLUSTER_MISMATCH")]
    public void RejectsEachDifferenceWithoutExposingValues(int field, string value, string code)
    {
        var fields = Valid.Split('|');
        fields[field] = value;
        var error = Assert.Throws<IdentityVerificationException>(() => PreflightContract.Identity([Row(string.Join('|', fields))], Selected));
        Assert.Contains($"[PF_IDENTITY_MATCH/{code}]", PreflightDiagnostics.Failure(PreflightStage.IdentityMatch, error));
        Assert.DoesNotContain(value, error.ToString());
    }

    [Fact]
    public void RejectsRowCountNullAndMalformedRows()
    {
        foreach (var rows in new PreflightContract.IdentityRow[][] {
            [], [Row(), Row()], [null!], [Row(null)], [Row("secret")], [Row(Valid + "|secret")] })
        {
            var error = Assert.Throws<IdentityVerificationException>(() => PreflightContract.Identity(rows, Selected));
            Assert.Contains(error.Failure is IdentityFailure.RowCount ? "/IDENTITY_ROW_COUNT]" : "/IDENTITY_SHAPE]",
                PreflightDiagnostics.Failure(PreflightStage.IdentityMatch, error));
        }
    }

    [Fact]
    public void MaterializesTypedInetWithoutTextConversion()
    {
        using var table = IdentityTable(typeof(NpgsqlInet));
        table.Rows.Add(Valid, new NpgsqlInet("127.0.0.1/32"));
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        var row = PreflightContract.ReadIdentityRow(reader);
        Assert.Equal(32, row.Address!.Value.Netmask);
        PreflightContract.Identity([row], Selected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DatabaseNullsMaterializeAndFailClosed(bool nullFields)
    {
        using var table = IdentityTable(typeof(NpgsqlInet));
        table.Rows.Add(nullFields ? DBNull.Value : Valid, nullFields ? new NpgsqlInet("127.0.0.1/32") : DBNull.Value);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        var row = PreflightContract.ReadIdentityRow(reader);
        var error = Assert.Throws<IdentityVerificationException>(() => PreflightContract.Identity([row], Selected));
        Assert.Equal(nullFields ? IdentityFailure.Shape : IdentityFailure.Address, error.Failure);
    }

    [Theory]
    [InlineData("127.0.0.1/32")]
    [InlineData("secret malformed address")]
    public void TextAddressIsNeverParsedOrNormalized(string value)
    {
        using var table = IdentityTable(typeof(string));
        table.Rows.Add(Valid, value);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        var error = Assert.Throws<InvalidCastException>(() => PreflightContract.ReadIdentityRow(reader));
        var output = PreflightDiagnostics.Failure(PreflightStage.IdentityRead, error);
        Assert.Contains("[PF_IDENTITY_READ/IDENTITY_TYPE_CONVERSION_FAILED]", output);
        Assert.DoesNotContain(value, output);
    }

    [Fact]
    public void RejectsNonStringIdentityFieldConversion()
    {
        using var table = new DataTable();
        table.Columns.Add("fields", typeof(int));
        table.Columns.Add("address", typeof(NpgsqlInet));
        table.Rows.Add(123, new NpgsqlInet("127.0.0.1/32"));
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        Assert.Throws<InvalidCastException>(() => PreflightContract.ReadIdentityRow(reader));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void RejectsUnexpectedReaderColumnCount(int count)
    {
        using var table = new DataTable();
        for (var i = 0; i < count; i++) table.Columns.Add($"column{i}", typeof(string));
        using var reader = table.CreateDataReader();
        var error = Assert.Throws<IdentityVerificationException>(() => PreflightContract.ReadIdentityRow(reader));
        Assert.Equal(IdentityFailure.Shape, error.Failure);
    }

    private static DataTable IdentityTable(Type addressType)
    {
        var table = new DataTable();
        table.Columns.Add("fields", typeof(string));
        table.Columns.Add("address", addressType);
        return table;
    }

    [Theory]
    [InlineData(PreflightStage.IdentityMatch)]
    [InlineData(PreflightStage.IdentityRead)]
    public void OperationalFailuresAreNeverReportedAsMismatches(PreflightStage stage)
    {
        foreach (var (error, code) in new (Exception, string)[] {
            (new InvalidOperationException("secret"), stage == PreflightStage.IdentityMatch ? "IDENTITY_OPERATION_FAILED" : "GUARD_OR_OPERATION_FAILED"),
            (new InvalidCastException("secret"), "IDENTITY_TYPE_CONVERSION_FAILED"),
            (new Exception("secret"), stage == PreflightStage.IdentityMatch ? "IDENTITY_UNEXPECTED_EXCEPTION" : "UNCLASSIFIED"),
            (new Npgsql.NpgsqlException("secret"), "PROVIDER_OR_CONNECTIVITY"),
            (new IdentityVerificationException((IdentityFailure)int.MaxValue), stage == PreflightStage.IdentityMatch ? "IDENTITY_OPERATION_FAILED" : "UNCLASSIFIED") })
        {
            var output = PreflightDiagnostics.Failure(stage, error);
            Assert.Contains($"/{code}]", output);
            Assert.DoesNotContain("secret", output);
        }
    }
}
