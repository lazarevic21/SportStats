using System.Globalization;
using System.Text.Json;
using SportStats.Application.Football.Ingestion;

namespace SportStats.ProviderValidation;

/// <summary>Developer-only, one-shot observations. No storage, ordering decisions or automatic retries.</summary>
public sealed class ProviderValidationRunner(IFootballDataClient client,
    IFootballMatchObservationNormalizer normalizer,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> pause = delay ?? Task.Delay;
    private static readonly HashSet<string> KnownStatuses = new(StringComparer.Ordinal)
    { "SCHEDULED", "TIMED", "IN_PLAY", "PAUSED", "FINISHED", "SUSPENDED", "POSTPONED", "CANCELLED", "AWARDED", "LIVE" };

    public async Task<int> RunAsync(ValidationOptions options, TextWriter output, TextReader input,
        CancellationToken cancellationToken = default)
    {
        if (!options.Live || !options.IsValid)
        {
            await output.WriteLineAsync("No requests made: explicit --live and valid options are required.");
            return 2;
        }
        cancellationToken.ThrowIfCancellationRequested();
        await output.WriteLineAsync("Read-only live validation; no retries. Local pacing does not reserve account-wide quota.");
        await output.WriteLineAsync("Observed behavior is not a documented guarantee; timestamps do not establish freshness ordering.");
        var requested = false;
        MatchProviderObservations? first = null;
        for (var round = 0; round < (options.Compare ? 2 : 1); round++)
        {
            if (round == 1)
            {
                await output.WriteLineAsync("Type compare to manually authorize one additional observation; any other input stops.");
                if (await input.ReadLineAsync(cancellationToken) != "compare") return 0;
            }
            int? year = options.Year;
            var methods = new Func<string, CancellationToken, Task<FootballDataResponse>>[]
            { client.GetCompetitionAsync, client.GetTeamsAsync, client.GetMatchesAsync };
            var labels = new[] { "competition", "teams", "matches" };
            for (var endpoint = 0; endpoint < methods.Length; endpoint++)
            {
                if (requested) await pause(TimeSpan.FromSeconds(options.SpacingSeconds), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                requested = true;
                var response = await methods[endpoint](options.Competition, cancellationToken);
                if (!response.IsSuccess)
                {
                    var failure = response.Failure;
                    await output.WriteLineAsync($"{labels[endpoint]}: failed kind={failure?.Kind}; HTTP={failure?.StatusCode}; retryAfterSeconds={failure?.RetryAfter?.TotalSeconds.ToString(CultureInfo.InvariantCulture)}; retryAfterUtc={failure?.RetryAfterUtc?.ToString("O", CultureInfo.InvariantCulture)}. Stopped; no retry.");
                    return 1;
                }
                await output.WriteLineAsync($"{labels[endpoint]}: success; HTTP={response.Metadata?.StatusCode}; bytes={response.Metadata?.ResponseBytes}; serverDateUtc={response.Metadata?.DateUtc?.ToString("O", CultureInfo.InvariantCulture)}.");
                try
                {
                    using var document = JsonDocument.Parse(response.Json!);
                    if (endpoint == 0)
                    {
                        if (!year.HasValue)
                        {
                            var start = document.RootElement.GetProperty("currentSeason").GetProperty("startDate").GetString();
                            if (!DateOnly.TryParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.Year is < 2000 or > 2100)
                                throw new FormatException();
                            year = date.Year;
                        }
                    }
                    else if (endpoint == 1)
                    {
                        var teams = document.RootElement.GetProperty("teams");
                        if (teams.ValueKind != JsonValueKind.Array) throw new FormatException();
                        await output.WriteLineAsync($"teams: observed count={teams.GetArrayLength()}.");
                    }
                    else if (!string.Equals(options.Competition, "PL", StringComparison.Ordinal))
                        await output.WriteLineAsync("matches: normalization unsupported for this competition; no PL provenance assigned.");
                    else
                    {
                        if (document.RootElement.TryGetProperty("matches", out var rawMatches) && rawMatches.ValueKind == JsonValueKind.Array)
                        {
                            var invalid = rawMatches.EnumerateArray().Count(match =>
                                match.ValueKind == JsonValueKind.Object && match.TryGetProperty("lastUpdated", out var stamp) &&
                                stamp.ValueKind != JsonValueKind.Null &&
                                (stamp.ValueKind != JsonValueKind.String ||
                                 !DateTimeOffset.TryParse(stamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) || parsed.Offset != TimeSpan.Zero));
                            await output.WriteLineAsync($"lastUpdated preliminary invalid timestamp count={invalid}; exact format validation follows normalization.");
                        }
                        var observations = normalizer.NormalizeMatchObservations(response.Json!, year!.Value, DateTime.UtcNow);
                        await Summarize(output, observations);
                        if (round == 0) first = observations;
                        else if (first is not null)
                        {
                            var previous = first.Matches.ToDictionary(x => x.Identity);
                            var matched = observations.Matches.Where(x => previous.ContainsKey(x.Identity)).ToArray();
                            var changed = matched.Count(x => !Equals(previous[x.Identity] with { RetrievedAtUtc = x.RetrievedAtUtc }, x));
                            await output.WriteLineAsync($"comparison: shared identities={matched.Length}; changed observations={changed}; no freshness or monotonicity conclusion.");
                        }
                    }
                }
                catch (Exception error) when (error is JsonException or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException)
                {
                    await output.WriteLineAsync($"{labels[endpoint]}: invalid observation; payload and exception details suppressed. Stopped.");
                    return 1;
                }
            }
        }
        return 0;
    }

    private static async Task Summarize(TextWriter output, MatchProviderObservations observations)
    {
        var matches = observations.Matches;
        await output.WriteLineAsync($"matches: normalized={matches.Count}; identity, season and ordered participant references validated.");
        await Presence(output, "kickoff", matches.Select(x => x.KickoffUtc.Presence));
        await Presence(output, "matchday", matches.Select(x => x.Matchday.Presence));
        await Presence(output, "status", matches.Select(x => x.Status.Presence));
        await Presence(output, "homeGoals", matches.Select(x => x.HomeGoals.Presence));
        await Presence(output, "awayGoals", matches.Select(x => x.AwayGoals.Presence));
        await Presence(output, "lastUpdated", matches.Select(x => x.ProviderLastUpdatedUtc.Presence));
        await output.WriteLineAsync($"goals: explicit zero home={matches.Count(x => x.HomeGoals.Value == 0)}; away={matches.Count(x => x.AwayGoals.Value == 0)}.");
        foreach (var status in KnownStatuses.Order(StringComparer.Ordinal))
            await output.WriteLineAsync($"raw status {status}: {matches.Count(x => x.RawProviderStatus == status)}.");
        await output.WriteLineAsync($"raw status other: {matches.Count(x => x.RawProviderStatus is not null && !KnownStatuses.Contains(x.RawProviderStatus))}.");
        foreach (var group in matches.Where(x => x.Status.Value.HasValue).GroupBy(x => x.Status.Value!.Value).OrderBy(x => x.Key))
            await output.WriteLineAsync($"canonical status {group.Key}: {group.Count()}.");
        await output.WriteLineAsync("Timestamp values present were UTC-parseable; provider lastUpdated and local retrieval time remain distinct.");
    }

    private static Task Presence(TextWriter output, string name, IEnumerable<MatchObservationFieldPresence> values)
    {
        var counts = values.GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
        return output.WriteLineAsync($"{name}: missing={counts.GetValueOrDefault(MatchObservationFieldPresence.Missing)}; null={counts.GetValueOrDefault(MatchObservationFieldPresence.Null)}; value={counts.GetValueOrDefault(MatchObservationFieldPresence.Value)}.");
    }
}
