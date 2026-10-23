using System.Data;
using System.Diagnostics;
using System.Text;
using NpgsqlTypes;
using SportStats.MigrationPreflight;
using Xunit;

namespace SportStats.UnitTests;

public class IdentityVerificationTests
{
    // Deliberately synthetic cluster identities; never copy live target inventory here.
    private static readonly PreflightContract.Selection Selected = new("127.0.0.1", 5432,
        "sportstats_test", "sportstats_migrator", false, "123456789");
    private const string Valid = "sportstats_test|sportstats_migrator|sportstats_migrator|5432|on|123456789";
    private static PreflightContract.IdentityRow Row(string? fields = Valid, NpgsqlInet? address = null) =>
        new(fields, address ?? new NpgsqlInet("127.0.0.1/32"));

    [Fact]
    public void ExactIpv4HostEndpointPasses() => PreflightContract.Identity([Row()], Selected);

    [Fact]
    public void DisabledWrapperSentinelCannotSelectACluster()
    {
        string[] arguments = ["--host", "127.0.0.1", "--port", "5432", "--database", "sportstats_test",
            "--role", "sportstats_migrator", "--expect", "baseline", "--system-identifier",
            "DISABLED_REQUIRES_SEPARATE_REVIEW", "--approve-read-only-audit"];
        Assert.Throws<InvalidOperationException>(() => PreflightContract.Parse(arguments));
    }

    [Theory]
    [InlineData(false, "DIAGNOSTIC|REFUSED|AUTHORIZATION_REQUIRED")]
    [InlineData(true, "DIAGNOSTIC|REFUSED|RECONFIGURATION_REQUIRED")]
    public async Task DiagnosticWrapperRefusesBeforeAnySideEffects(bool approved, string expected)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SportStats.slnx")))
            root = root.Parent;
        Assert.True(root is not null, "Repository root is required for the offline wrapper regression.");
        var wrapper = File.ReadAllText(Path.Combine(root!.FullName, "tools", "SportStats.MigrationPreflight",
            "Invoke-ReadOnlyDiagnostic.ps1"));
        Assert.Contains("--system-identifier 'DISABLED_REQUIRES_SEPARATE_REVIEW'", wrapper);

        // Run the real refusal prefix, never the dormant implementation. Validate the prefix's
        // complete AST before execution, then replace all dormant code with a side-effect sentinel.
        // Thus even removal of either refusal exit cannot reach a build, credentials or a database.
        var source = Convert.ToBase64String(Encoding.UTF8.GetBytes(wrapper));
        var command = $$"""
            $source=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{source}}'))
            $tokens=$null; $errors=$null
            $whole=[Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors)
            if($errors.Count -ne 0) { [Console]::Out.WriteLine('TEST_SYNTAX_REJECTED'); exit 90 }
            $marker="$"+"ErrorActionPreference = 'Stop'"
            $boundary=$source.IndexOf($marker,[StringComparison]::Ordinal)
            if($boundary -lt 0) { [Console]::Out.WriteLine('TEST_BOUNDARY_REJECTED'); exit 90 }
            $prefix=$source.Substring(0,$boundary)
            $ast=[Management.Automation.Language.Parser]::ParseInput($prefix,[ref]$tokens,[ref]$errors)
            if($errors.Count -ne 0 -or $null -ne $ast.BeginBlock -or $null -ne $ast.ProcessBlock -or
                $ast.ParamBlock.Extent.Text -ne 'param([switch]$ApproveReadOnlyAudit)') { exit 90 }
            $allowed=@(
                "[Console]::Out.WriteLine('DIAGNOSTIC|REFUSED|AUTHORIZATION_REQUIRED')",
                "[Console]::Out.WriteLine('DIAGNOSTIC|REFUSED|RECONFIGURATION_REQUIRED')"
            )
            $statements=$ast.FindAll({param($node) $node -is [Management.Automation.Language.StatementAst]},$true)
            foreach($statement in $statements) {
                if($statement -is [Management.Automation.Language.IfStatementAst]) {
                    if($statement.Clauses.Count -ne 1 -or $null -ne $statement.ElseClause -or
                        $statement.Clauses[0].Item1.Extent.Text -ne '-not $ApproveReadOnlyAudit') { exit 90 }
                } elseif($statement -is [Management.Automation.Language.PipelineAst] -or
                    $statement -is [Management.Automation.Language.CommandExpressionAst]) {
                    if($statement.Extent.Text -ne '-not $ApproveReadOnlyAudit' -and
                        $allowed -cnotcontains $statement.Extent.Text -and $statement.Extent.Text -ne '1') { exit 90 }
                } elseif($statement -is [Management.Automation.Language.ExitStatementAst]) {
                    if($statement.Pipeline.Extent.Text -ne '1') { exit 90 }
                } else { exit 90 }
            }
            $safe=$prefix+"`n[Console]::Out.WriteLine('TEST_SIDE_EFFECT_REACHED'); exit 91"
            & ([scriptblock]::Create($safe)) {{(approved ? "-ApproveReadOnlyAudit" : "")}}
            exit 92
            """;
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        using var process = new Process { StartInfo = start };
        try { Assert.True(process.Start(), "Offline PowerShell host could not start."); }
        catch (System.ComponentModel.Win32Exception)
        { throw new InvalidOperationException("A local PowerShell host is required for the offline wrapper regression."); }
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Offline wrapper refusal exceeded its deadline.");
        }
        var output = await outputTask;
        var errors = await errorTask;
        Assert.True(process.ExitCode == 1, "Wrapper must refuse with exit code 1 before side effects.");
        Assert.True(output.TrimEnd('\r', '\n') == expected, "Wrapper must emit only its fixed refusal.");
        Assert.True(string.IsNullOrEmpty(errors), "Wrapper must not emit exception records or private details.");
    }

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
    [InlineData(5, "987654321", "CLUSTER_MISMATCH")]
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
