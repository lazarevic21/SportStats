using SportStats.Domain.Platform;

namespace SportStats.Domain.Football;

public sealed class FootballStandingSnapshot
{
    private FootballStandingSnapshot() { }
    public FootballStandingSnapshot(Guid id, Season season, string providerKey, DateTime retrievedAtUtc, string tableType, string scope)
    {
        DomainGuard.Football(season);
        Id = DomainGuard.Id(id, nameof(id));
        Season = season;
        SeasonId = season.Id;
        ProviderKey = DomainGuard.Text(providerKey, nameof(providerKey));
        RetrievedAtUtc = DomainGuard.Utc(retrievedAtUtc, nameof(retrievedAtUtc));
        TableType = DomainGuard.Text(tableType, nameof(tableType));
        Scope = DomainGuard.Text(scope, nameof(scope));
    }
    public FootballStandingSnapshot(Season season, string providerKey, DateTime retrievedAtUtc, string tableType, string scope)
        : this(Guid.NewGuid(), season, providerKey, retrievedAtUtc, tableType, scope) { }
    public Guid Id { get; private set; }
    public Guid SeasonId { get; private set; }
    public Season Season { get; private set; } = null!;
    public string ProviderKey { get; private set; } = null!;
    public DateTime RetrievedAtUtc { get; private set; }
    public string TableType { get; private set; } = null!;
    public string Scope { get; private set; } = null!;
}
