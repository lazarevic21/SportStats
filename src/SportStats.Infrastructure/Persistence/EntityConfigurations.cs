using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportStats.Domain.Platform;
using SportStats.Domain.Football;

namespace SportStats.Infrastructure.Persistence;

internal sealed class SportConfiguration : IEntityTypeConfiguration<Sport>
{
    public void Configure(EntityTypeBuilder<Sport> b)
    {
        b.ToTable("sports", "platform"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Code).IsRequired(); b.Property(x => x.Name).IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
    }
}
internal sealed class CompetitionConfiguration : IEntityTypeConfiguration<Competition>
{
    public void Configure(EntityTypeBuilder<Competition> b)
    {
        b.ToTable("competitions", "platform"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Code).IsRequired(); b.Property(x => x.Name).IsRequired();
        b.HasIndex(x => new { x.SportId, x.Code }).IsUnique();
        b.HasOne(x => x.Sport).WithMany().HasForeignKey(x => x.SportId).OnDelete(DeleteBehavior.Restrict);
    }
}
internal sealed class SeasonConfiguration : IEntityTypeConfiguration<Season>
{
    public void Configure(EntityTypeBuilder<Season> b)
    {
        b.ToTable("seasons", "platform"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Key).IsRequired(); b.Property(x => x.StartDate).HasColumnType("date"); b.Property(x => x.EndDate).HasColumnType("date");
        b.HasIndex(x => new { x.CompetitionId, x.Key }).IsUnique();
        b.HasOne(x => x.Competition).WithMany().HasForeignKey(x => x.CompetitionId).OnDelete(DeleteBehavior.Restrict);
    }
}
internal sealed class FootballTeamConfiguration : IEntityTypeConfiguration<FootballTeam>
{
    public void Configure(EntityTypeBuilder<FootballTeam> b)
    {
        b.ToTable("teams", "football"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Name).IsRequired();
    }
}
internal sealed class FootballSeasonTeamConfiguration : IEntityTypeConfiguration<FootballSeasonTeam>
{
    public void Configure(EntityTypeBuilder<FootballSeasonTeam> b)
    {
        b.ToTable("season_teams", "football"); b.HasKey(x => new { x.SeasonId, x.TeamId });
        b.HasOne(x => x.Season).WithMany().HasForeignKey(x => x.SeasonId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TeamId, x.SeasonId });
    }
}
internal sealed class FootballMatchConfiguration : IEntityTypeConfiguration<FootballMatch>
{
    public void Configure(EntityTypeBuilder<FootballMatch> b)
    {
        b.ToTable("matches", "football", t =>
        {
            t.HasCheckConstraint("CK_matches_distinct_teams", "\"HomeTeamId\" <> \"AwayTeamId\"");
            t.HasCheckConstraint("CK_matches_home_goals", "\"HomeGoals\" IS NULL OR \"HomeGoals\" >= 0");
            t.HasCheckConstraint("CK_matches_away_goals", "\"AwayGoals\" IS NULL OR \"AwayGoals\" >= 0");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Ignore(x => x.Score);
        b.Property(x => x.KickoffUtc).HasColumnType("timestamp with time zone");
        b.Property(x => x.Status).HasConversion(
            status => status.ToString(),
            value => FootballMatchStatusParser.Parse(value)).IsRequired();
        b.HasOne(x => x.Season).WithMany().HasForeignKey(x => x.SeasonId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FootballSeasonTeam>().WithMany().HasForeignKey(x => new { x.SeasonId, x.HomeTeamId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FootballSeasonTeam>().WithMany().HasForeignKey(x => new { x.SeasonId, x.AwayTeamId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.HomeTeam).WithMany().HasForeignKey(x => x.HomeTeamId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.AwayTeam).WithMany().HasForeignKey(x => x.AwayTeamId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.SeasonId, x.KickoffUtc }); b.HasIndex(x => new { x.SeasonId, x.Status, x.KickoffUtc });
    }
}
internal sealed class FootballStandingSnapshotConfiguration : IEntityTypeConfiguration<FootballStandingSnapshot>
{
    public void Configure(EntityTypeBuilder<FootballStandingSnapshot> b)
    {
        b.ToTable("standing_snapshots", "football"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.HasAlternateKey(x => new { x.Id, x.SeasonId });
        b.Property(x => x.ProviderKey).IsRequired(); b.Property(x => x.TableType).IsRequired(); b.Property(x => x.Scope).IsRequired();
        b.Property(x => x.RetrievedAtUtc).HasColumnType("timestamp with time zone");
        b.HasOne(x => x.Season).WithMany().HasForeignKey(x => x.SeasonId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.SeasonId, x.ProviderKey, x.TableType, x.Scope, x.RetrievedAtUtc });
    }
}
internal sealed class FootballStandingRowConfiguration : IEntityTypeConfiguration<FootballStandingRow>
{
    public void Configure(EntityTypeBuilder<FootballStandingRow> b)
    {
        b.ToTable("standing_rows", "football"); b.HasKey(x => new { x.SnapshotId, x.TeamId });
        b.HasOne(x => x.Snapshot).WithMany().HasForeignKey(x => new { x.SnapshotId, x.SeasonId }).HasPrincipalKey(x => new { x.Id, x.SeasonId }).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<FootballSeasonTeam>().WithMany().HasForeignKey(x => new { x.SeasonId, x.TeamId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
    }
}
internal sealed class CompetitionExternalIdentityConfiguration : IEntityTypeConfiguration<CompetitionExternalIdentity>
{
    public void Configure(EntityTypeBuilder<CompetitionExternalIdentity> b)
    {
        IdentityConfiguration.Configure(b, "competition_external_ids", "platform");
        b.HasOne<Competition>().WithMany().HasForeignKey(x => x.CompetitionId).OnDelete(DeleteBehavior.Restrict);
    }
}
internal sealed class SeasonExternalIdentityConfiguration : IEntityTypeConfiguration<SeasonExternalIdentity>
{
    public void Configure(EntityTypeBuilder<SeasonExternalIdentity> b)
    {
        IdentityConfiguration.Configure(b, "season_external_ids", "platform");
        b.HasOne<Season>().WithMany().HasForeignKey(x => x.SeasonId).OnDelete(DeleteBehavior.Restrict);
    }
}
internal sealed class FootballTeamExternalIdentityConfiguration : IEntityTypeConfiguration<FootballTeamExternalIdentity>
{
    public void Configure(EntityTypeBuilder<FootballTeamExternalIdentity> b)
    {
        IdentityConfiguration.Configure(b, "team_external_ids", "football");
        b.HasOne<FootballTeam>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
    }
}
internal sealed class FootballMatchExternalIdentityConfiguration : IEntityTypeConfiguration<FootballMatchExternalIdentity>
{
    public void Configure(EntityTypeBuilder<FootballMatchExternalIdentity> b)
    {
        IdentityConfiguration.Configure(b, "match_external_ids", "football");
        b.HasOne<FootballMatch>().WithMany().HasForeignKey(x => x.MatchId).OnDelete(DeleteBehavior.Restrict);
    }
}
internal static class IdentityConfiguration
{
    public static void Configure<T>(EntityTypeBuilder<T> b, string table, string schema) where T : class
    {
        b.ToTable(table, schema); b.HasKey("Id"); b.Property<Guid>("Id").ValueGeneratedNever();
        b.Property<string>("ProviderKey").IsRequired(); b.Property<string>("IdentityScope").IsRequired(); b.Property<string>("ExternalId").IsRequired();
        b.HasIndex("ProviderKey", "IdentityScope", "ExternalId");
    }
}
