using SportStats.Application.Football.Ingestion;

namespace SportStats.Application.Football.IdentityResolution;

/// <summary>Pure contract validation. Does not look up identities or validate persisted relationships.</summary>
public static class IdentityResolutionValidation
{
    public static IReadOnlyList<IdentityResolutionFailure> Validate(IIdentityResolutionRequest? request)
    {
        var errors = new List<IdentityResolutionFailure>();
        void Invalid(string field) => errors.Add(new(IdentityResolutionFailureCode.InvalidInput, field, "Invalid contract value."));
        void Text(string? value, string field, bool naturalKey = false)
        {
            if (string.IsNullOrWhiteSpace(value) || (naturalKey && value != value.Trim())) Invalid(field);
        }
        void Identity(ProviderIdentity? identity, string field)
        {
            if (identity is null) { Invalid(field); return; }
            Text(identity.ProviderKey, field + ".ProviderKey");
            Text(identity.IdentityScope, field + ".IdentityScope");
            Text(identity.ExternalId, field + ".ExternalId");
        }
        void Reference<T>(EntityReference<T>? reference, string field) where T : class
        {
            switch (reference)
            {
                case EntityReference<T>.Internal internalReference:
                    if (internalReference.Id == Guid.Empty) Invalid(field + ".Id");
                    break;
                case EntityReference<T>.External externalReference: Identity(externalReference.Identity, field + ".Identity"); break;
                default: Invalid(field); break;
            }
        }
        void Season(NormalizedSeason? season, string field)
        {
            if (season is null) { Invalid(field); return; }
            Identity(season.Identity, field + ".Identity");
            Text(season.Key, field + ".Key", true);
            if (season.QueryYear is < 1 or > 9999) Invalid(field + ".QueryYear");
            if (season.StartDate > season.EndDate) Invalid(field + ".EndDate");
        }
        void Score(NormalizedScoreField? score, string field)
        {
            if (score is null || !Enum.IsDefined(score.Presence) ||
                (score.Presence == ScoreFieldPresence.Value ? score.Value is null or < 0 : score.Value is not null)) Invalid(field);
        }
        switch (request)
        {
            case CompetitionResolutionRequest competition:
                if (competition.SportId == Guid.Empty) Invalid("SportId");
                if (competition.Competition is null) { Invalid("Competition"); break; }
                Identity(competition.Identity, "Competition.Identity");
                Text(competition.Competition.Code, "Competition.Code", true);
                Text(competition.Competition.Name, "Competition.Name");
                break;
            case SeasonResolutionRequest season:
                Reference(season.Competition, "Competition");
                Season(season.Season, "Season");
                break;
            case TeamResolutionRequest team:
                Reference(team.Season, "Season");
                if (team.Team is null) { Invalid("Team"); break; }
                Identity(team.Identity, "Team.Identity");
                Text(team.Team.Name, "Team.Name");
                break;
            case MatchResolutionRequest matchRequest:
                Reference(matchRequest.Competition, "Competition");
                var match = matchRequest.Match;
                if (match is null) { Invalid("Match"); break; }
                Identity(match.Identity, "Match.Identity");
                Season(match.Season, "Match.Season");
                Identity(match.HomeTeam, "Match.HomeTeam");
                Identity(match.AwayTeam, "Match.AwayTeam");
                if (match.HomeTeam is not null && match.HomeTeam == match.AwayTeam)
                    errors.Add(new(IdentityResolutionFailureCode.RelationshipConflict, "Match.AwayTeam", "Home and away identities must differ."));
                if (match.KickoffUtc.Kind != DateTimeKind.Utc) Invalid("Match.KickoffUtc");
                if (!Enum.IsDefined(match.Status)) Invalid("Match.Status");
                Text(match.ProviderStatus, "Match.ProviderStatus");
                Score(match.HomeGoals, "Match.HomeGoals");
                Score(match.AwayGoals, "Match.AwayGoals");
                break;
            default: Invalid("Request"); break;
        }
        return errors.AsReadOnly();
    }

    public static IReadOnlyList<IdentityResolutionFailure> Validate(IdentityResolutionBatch? batch)
    {
        var errors = new List<IdentityResolutionFailure>();
        if (batch is null)
        {
            errors.Add(new(IdentityResolutionFailureCode.InvalidInput, "Batch", "A batch is required."));
            return errors.AsReadOnly();
        }
        void Requests<T>(IReadOnlyList<T>? requests, string field) where T : IIdentityResolutionRequest
        {
            if (requests is null)
            {
                errors.Add(new(IdentityResolutionFailureCode.InvalidInput, field, "A collection is required."));
                return;
            }
            var seen = new HashSet<ProviderIdentity>();
            for (var index = 0; index < requests.Count; index++)
            {
                var item = requests[index];
                var itemErrors = Validate(item);
                errors.AddRange(itemErrors.Select(error => error with { Field = $"{field}[{index}].{error.Field}" }));
                if (itemErrors.Count == 0 && !seen.Add(item.Identity))
                    errors.Add(new(IdentityResolutionFailureCode.InvalidInput, $"{field}[{index}].Identity", "Repeated identity within the entity type."));
            }
        }
        Requests(batch.Competitions, "Competitions");
        Requests(batch.Seasons, "Seasons");
        Requests(batch.Teams, "Teams");
        Requests(batch.Matches, "Matches");
        return errors.AsReadOnly();
    }
}
