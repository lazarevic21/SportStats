namespace SportStats.Infrastructure.Persistence;

// IdentityScope is an opaque application key, pending validation of provider scope rules.
public sealed class CompetitionExternalIdentity
{
    private CompetitionExternalIdentity() { }
    public CompetitionExternalIdentity(Guid id, string providerKey, string identityScope, string externalId, Guid competitionId)
    {
        IdentityGuard.Validate(id, providerKey, identityScope, externalId, competitionId);
        Id = id; ProviderKey = providerKey; IdentityScope = identityScope; ExternalId = externalId; CompetitionId = competitionId;
    }
    public Guid Id { get; private set; }
    public string ProviderKey { get; private set; } = null!;
    public string IdentityScope { get; private set; } = null!;
    public string ExternalId { get; private set; } = null!;
    public Guid CompetitionId { get; private set; }
}
public sealed class SeasonExternalIdentity
{
    private SeasonExternalIdentity() { }
    public SeasonExternalIdentity(Guid id, string providerKey, string identityScope, string externalId, Guid seasonId)
    {
        IdentityGuard.Validate(id, providerKey, identityScope, externalId, seasonId);
        Id = id; ProviderKey = providerKey; IdentityScope = identityScope; ExternalId = externalId; SeasonId = seasonId;
    }
    public Guid Id { get; private set; }
    public string ProviderKey { get; private set; } = null!;
    public string IdentityScope { get; private set; } = null!;
    public string ExternalId { get; private set; } = null!;
    public Guid SeasonId { get; private set; }
}
public sealed class FootballTeamExternalIdentity
{
    private FootballTeamExternalIdentity() { }
    public FootballTeamExternalIdentity(Guid id, string providerKey, string identityScope, string externalId, Guid teamId)
    {
        IdentityGuard.Validate(id, providerKey, identityScope, externalId, teamId);
        Id = id; ProviderKey = providerKey; IdentityScope = identityScope; ExternalId = externalId; TeamId = teamId;
    }
    public Guid Id { get; private set; }
    public string ProviderKey { get; private set; } = null!;
    public string IdentityScope { get; private set; } = null!;
    public string ExternalId { get; private set; } = null!;
    public Guid TeamId { get; private set; }
}
public sealed class FootballMatchExternalIdentity
{
    private FootballMatchExternalIdentity() { }
    public FootballMatchExternalIdentity(Guid id, string providerKey, string identityScope, string externalId, Guid matchId)
    {
        IdentityGuard.Validate(id, providerKey, identityScope, externalId, matchId);
        Id = id; ProviderKey = providerKey; IdentityScope = identityScope; ExternalId = externalId; MatchId = matchId;
    }
    public Guid Id { get; private set; }
    public string ProviderKey { get; private set; } = null!;
    public string IdentityScope { get; private set; } = null!;
    public string ExternalId { get; private set; } = null!;
    public Guid MatchId { get; private set; }
}
internal static class IdentityGuard
{
    public static void Validate(Guid id, string providerKey, string identityScope, string externalId, Guid targetId)
    {
        if (id == Guid.Empty || targetId == Guid.Empty) throw new ArgumentException("Identity keys must be nonempty.");
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
    }
}
