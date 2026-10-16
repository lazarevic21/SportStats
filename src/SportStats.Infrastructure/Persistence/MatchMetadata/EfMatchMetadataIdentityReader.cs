using Microsoft.EntityFrameworkCore;
using SportStats.Application.Football.Ingestion;
using SportStats.Application.Football.MatchMetadata;

namespace SportStats.Infrastructure.Persistence.MatchMetadata;

/// <summary>
/// Six set-based reads at most; no tracking, locks, writes, or transaction ownership.
/// Concurrent commits between reads may produce a best-effort view. Evaluation remains advisory:
/// a later write would require fresh identity validation and its own concurrency policy.
/// </summary>
public sealed class EfMatchMetadataIdentityReader(SportStatsDbContext context) : IMatchMetadataIdentityReader
{
    public async Task<MatchMetadataIdentitySnapshot> LoadAsync(IReadOnlyList<MatchProviderObservation> observations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observations);
        cancellationToken.ThrowIfCancellationRequested();
        var input = observations.ToArray();
        var matchKeys = Keys(input.Select(x => x?.Identity));
        var seasonKeys = Keys(input.Select(x => x?.Season?.Identity));
        var teamKeys = Keys(input.SelectMany(x => new[] { x?.HomeTeam, x?.AwayTeam }));
        var mappings = new List<MatchMetadataIdentityMapping>();

        // The SQL predicates narrow each dimension. Recheck the complete ordinal tuple after
        // materialization: independently matching dimensions do not establish an exact mapping.
        if (matchKeys.Count > 0)
        {
            var providers = matchKeys.Select(x => x.ProviderKey).Distinct().ToArray();
            var scopes = matchKeys.Select(x => x.IdentityScope).Distinct().ToArray();
            var ids = matchKeys.Select(x => x.ExternalId).Distinct().ToArray();
            var rows = await context.FootballMatchExternalIdentities.AsNoTracking()
                .Where(x => providers.Contains(x.ProviderKey) && scopes.Contains(x.IdentityScope) && ids.Contains(x.ExternalId))
                .Select(x => new { x.ProviderKey, x.IdentityScope, x.ExternalId, EntityId = x.MatchId })
                .ToArrayAsync(cancellationToken);
            mappings.AddRange(rows.Select(x => new MatchMetadataIdentityMapping(MatchMetadataIdentityKind.Match,
                new(x.ProviderKey, x.IdentityScope, x.ExternalId), x.EntityId)).Where(x => matchKeys.Contains(x.Identity)));
        }
        if (seasonKeys.Count > 0)
        {
            var providers = seasonKeys.Select(x => x.ProviderKey).Distinct().ToArray();
            var scopes = seasonKeys.Select(x => x.IdentityScope).Distinct().ToArray();
            var ids = seasonKeys.Select(x => x.ExternalId).Distinct().ToArray();
            var rows = await context.SeasonExternalIdentities.AsNoTracking()
                .Where(x => providers.Contains(x.ProviderKey) && scopes.Contains(x.IdentityScope) && ids.Contains(x.ExternalId))
                .Select(x => new { x.ProviderKey, x.IdentityScope, x.ExternalId, EntityId = x.SeasonId })
                .ToArrayAsync(cancellationToken);
            mappings.AddRange(rows.Select(x => new MatchMetadataIdentityMapping(MatchMetadataIdentityKind.Season,
                new(x.ProviderKey, x.IdentityScope, x.ExternalId), x.EntityId)).Where(x => seasonKeys.Contains(x.Identity)));
        }
        if (teamKeys.Count > 0)
        {
            var providers = teamKeys.Select(x => x.ProviderKey).Distinct().ToArray();
            var scopes = teamKeys.Select(x => x.IdentityScope).Distinct().ToArray();
            var ids = teamKeys.Select(x => x.ExternalId).Distinct().ToArray();
            var rows = await context.FootballTeamExternalIdentities.AsNoTracking()
                .Where(x => providers.Contains(x.ProviderKey) && scopes.Contains(x.IdentityScope) && ids.Contains(x.ExternalId))
                .Select(x => new { x.ProviderKey, x.IdentityScope, x.ExternalId, EntityId = x.TeamId })
                .ToArrayAsync(cancellationToken);
            mappings.AddRange(rows.Select(x => new MatchMetadataIdentityMapping(MatchMetadataIdentityKind.Team,
                new(x.ProviderKey, x.IdentityScope, x.ExternalId), x.EntityId)).Where(x => teamKeys.Contains(x.Identity)));
        }

        // Retain duplicate mapping rows; only target IDs are deduplicated for database lookup.
        var matchIds = mappings.Where(x => x.Kind == MatchMetadataIdentityKind.Match).Select(x => x.EntityId).Distinct().ToArray();
        var matches = matchIds.Length == 0 ? [] : await context.FootballMatches.AsNoTracking()
            .Where(x => matchIds.Contains(x.Id))
            .Select(x => new MatchMetadataSnapshot(x.Id, x.SeasonId, x.HomeTeamId, x.AwayTeamId,
                x.KickoffUtc, x.Matchday, x.Status, x.HomeGoals, x.AwayGoals)).ToArrayAsync(cancellationToken);
        var seasonIds = mappings.Where(x => x.Kind == MatchMetadataIdentityKind.Season).Select(x => x.EntityId)
            .Concat(matches.Select(x => x.SeasonId)).Distinct().ToArray();
        var teamIds = mappings.Where(x => x.Kind == MatchMetadataIdentityKind.Team).Select(x => x.EntityId)
            .Concat(matches.SelectMany(x => new[] { x.HomeTeamId, x.AwayTeamId })).Distinct().ToArray();
        var existingSeasonIds = seasonIds.Length == 0 ? [] : await context.Seasons.AsNoTracking()
            .Where(x => seasonIds.Contains(x.Id)).Select(x => x.Id).ToArrayAsync(cancellationToken);
        var existingTeamIds = teamIds.Length == 0 ? [] : await context.FootballTeams.AsNoTracking()
            .Where(x => teamIds.Contains(x.Id)).Select(x => x.Id).ToArrayAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(Array.AsReadOnly(matches), Array.AsReadOnly(existingSeasonIds), Array.AsReadOnly(existingTeamIds),
            mappings.AsReadOnly());
    }

    private static HashSet<ProviderIdentity> Keys(IEnumerable<ProviderIdentity?> identities)
        => identities.Where(x => x is not null && !string.IsNullOrWhiteSpace(x.ProviderKey) &&
                !string.IsNullOrWhiteSpace(x.IdentityScope) && !string.IsNullOrWhiteSpace(x.ExternalId))
            .Select(x => x!).ToHashSet();
}
