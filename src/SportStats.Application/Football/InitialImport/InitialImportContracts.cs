using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Platform;

namespace SportStats.Application.Football.InitialImport;

public sealed record InitialImportCount(string Entity, int Created, int Existing, int Rejected, int Conflicted);
public sealed record InitialImportResult(bool Applied, IReadOnlyList<InitialImportCount> Counts,
    IReadOnlyList<IdentityResolutionFailure> Conflicts)
{
    public bool Success => Conflicts.Count == 0;
}

/// <summary>Preview is strictly read only. Apply owns one complete create-only transaction.</summary>
public interface IInitialImportStore
{
    Task<Guid> ResolveFootballSportIdAsync(CancellationToken cancellationToken = default);
    Task<InitialImportResult> PreviewAsync(IdentityResolutionBatch batch, CancellationToken cancellationToken = default);
    Task<InitialImportResult> ApplyAsync(IdentityResolutionBatch batch, CancellationToken cancellationToken = default);
}

/// <summary>Only PL is enabled. Other leagues require reviewed identity scope and persistence support.</summary>
public sealed record InitialImportProfile
{
    private InitialImportProfile(string code) => CompetitionCode = code;
    public string CompetitionCode { get; }
    public string ProviderKey => "football-data.org";
    public string CompetitionScope => $"competition:{CompetitionCode}";
    public string SeasonScope(int year) => FormattableString.Invariant($"{CompetitionScope}/season:{year}");
    public static InitialImportProfile PremierLeague { get; } = new("PL");
    public static InitialImportProfile Select(string code) => code == "PL"
        ? PremierLeague : throw new ArgumentException("Competition is not enabled.", nameof(code));
}

public sealed record InitialImportBundle(NormalizedCompetition Competition, NormalizedSeason Season,
    IReadOnlyList<NormalizedTeam> Teams, IReadOnlyList<NormalizedMatch> Matches)
{
    public IdentityResolutionBatch ToBatch(Guid footballSportId) => new(
        [new(footballSportId, Competition)],
        [new(new EntityReference<Competition>.External(Competition.Identity), Season)],
        Teams.Select(team => new TeamResolutionRequest(new EntityReference<Season>.External(Season.Identity), team)).ToArray(),
        Matches.Select(match => new MatchResolutionRequest(new EntityReference<Competition>.External(Competition.Identity), match)).ToArray());
}
