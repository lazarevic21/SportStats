using SportStats.Application.Football.Ingestion;

namespace SportStats.Application.Football.InitialImport;

/// <summary>One bounded capture, no retries, no persistence and no recapture inside database locks.</summary>
public sealed class InitialImportFetcher(IFootballDataClient client, InitialImportValidator validator,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public async Task<InitialImportBundle> FetchAsync(InitialImportProfile profile, int year,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (year is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(year));
        cancellationToken.ThrowIfCancellationRequested();
        var competition = await Fetch(client.GetCompetitionAsync, profile, cancellationToken);
        await Pause(cancellationToken);
        var teams = await Fetch(client.GetTeamsAsync, profile, cancellationToken);
        await Pause(cancellationToken);
        var matches = await Fetch(client.GetMatchesAsync, profile, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return validator.Normalize(competition, teams, matches, year, DateTime.UtcNow);
    }

    private Task Pause(CancellationToken token) => (delay ?? Task.Delay)(TimeSpan.FromSeconds(7), token);

    private static async Task<string> Fetch(Func<string, CancellationToken, Task<FootballDataResponse>> request,
        InitialImportProfile profile, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var response = await request(profile.CompetitionCode, token);
        if (!response.IsSuccess) throw new InvalidOperationException("Provider request failed; import stopped without retry.");
        return response.Json!;
    }
}
