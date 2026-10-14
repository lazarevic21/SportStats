using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Infrastructure.Persistence.AdvisoryLocks;
using SportStats.Domain.Platform;
using SportStats.Domain.Football;

namespace SportStats.Infrastructure.Persistence.IdentityResolution;

/// <summary>Created only after the owned snapshot has passed all input validation.</summary>
internal sealed class ValidatedFootballResolutionInput
{
    private ValidatedFootballResolutionInput(IdentityResolutionBatch input) => Input = input;
    internal IdentityResolutionBatch Input { get; }
    internal static (ValidatedFootballResolutionInput? Snapshot, IdentityResolutionFailure? Failure) Create(
        IdentityResolutionBatch? batch, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (batch is null) return (null, new(IdentityResolutionFailureCode.InvalidInput, "Batch", "A batch is required."));
        if (batch.Competitions is null) return Missing("Competitions");
        if (batch.Seasons is null) return Missing("Seasons");
        if (batch.Teams is null) return Missing("Teams");
        if (batch.Matches is null) return Missing("Matches");
        static (ValidatedFootballResolutionInput?, IdentityResolutionFailure?) Missing(string field) =>
            (null, new(IdentityResolutionFailureCode.InvalidInput, field, "A collection is required."));
        IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source)
        {
            var copy = new List<T>();
            foreach (var item in source) { token.ThrowIfCancellationRequested(); copy.Add(item); }
            token.ThrowIfCancellationRequested();
            return Array.AsReadOnly(copy.ToArray());
        }
        var snapshot = new IdentityResolutionBatch(Copy(batch.Competitions), Copy(batch.Seasons), Copy(batch.Teams), Copy(batch.Matches));
        var failure = FootballResolutionPlanner.Validate(snapshot, token);
        return (failure is null ? new(snapshot) : null, failure);
    }
}

/// <summary>Pure planner over complete read-only discovery data. Invoke under the coordination
/// lock, then acquire Plan.IndividualDataLocks before any future executor writes. New parents are symbolic:
/// coordination protects their natural keys until real IDs can be assigned by an executor.</summary>
internal sealed class FootballResolutionPlanner
{
    private readonly AdvisoryLockKeys keys = new();

    internal static (ValidatedFootballResolutionInput? Snapshot, IdentityResolutionFailure? Failure) SnapshotAndValidate(
        IdentityResolutionBatch? batch, CancellationToken token = default) => ValidatedFootballResolutionInput.Create(batch, token);

    internal static IdentityResolutionFailure? Validate(IdentityResolutionBatch batch, CancellationToken token = default)
    {
        IdentityResolutionFailure? Check<T>(IReadOnlyList<T> requests, string field, string identityField) where T : IIdentityResolutionRequest
        {
            var seen = new HashSet<ProviderIdentity>();
            for (var i = 0; i < requests.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var error = IdentityResolutionValidation.Validate(requests[i]).FirstOrDefault();
                if (error is not null) return error with { Field = $"{field}[{i}].{error.Field}" };
                if (!seen.Add(requests[i].Identity)) return new(IdentityResolutionFailureCode.InvalidInput,
                    $"{field}[{i}].{identityField}", "Repeated identity within the entity type.");
            }
            return null;
        }
        var error = Check(batch.Competitions, "Competitions", "Competition.Identity") ?? Check(batch.Seasons, "Seasons", "Season.Identity") ??
            Check(batch.Teams, "Teams", "Team.Identity") ?? Check(batch.Matches, "Matches", "Match.Identity");
        if (error is not null) return error;
        var requestIndex = -1;
        var previousType = typeof(object);
        foreach (var request in batch.Competitions.Cast<IIdentityResolutionRequest>()
                     .Concat(batch.Seasons).Concat(batch.Teams).Concat(batch.Matches))
        {
            token.ThrowIfCancellationRequested();
            if (request.GetType() != previousType) { requestIndex = 0; previousType = request.GetType(); } else requestIndex++;
            var collection = request switch { CompetitionResolutionRequest => "Competitions", SeasonResolutionRequest => "Seasons", TeamResolutionRequest => "Teams", _ => "Matches" };
            var texts = new List<(string? Text, string Field)>();
            void Identity(ProviderIdentity identity, string field) => texts.AddRange([(identity.ProviderKey, field + ".ProviderKey"), (identity.IdentityScope, field + ".IdentityScope"), (identity.ExternalId, field + ".ExternalId")]);
            void Reference<T>(EntityReference<T> reference, string field) where T : class
            { if (reference is EntityReference<T>.External external) Identity(external.Identity, field + ".Identity"); }
            void Season(NormalizedSeason season, string field) { Identity(season.Identity, field + ".Identity"); texts.Add((season.Key, field + ".Key")); }
            switch (request)
            {
                case CompetitionResolutionRequest c: Identity(c.Identity, "Competition.Identity"); texts.AddRange([(c.Competition.Code, "Competition.Code"), (c.Competition.Name, "Competition.Name")]); break;
                case SeasonResolutionRequest s: Reference(s.Competition, "Competition"); Season(s.Season, "Season"); break;
                case TeamResolutionRequest t: Identity(t.Identity, "Team.Identity"); Reference(t.Season, "Season"); texts.AddRange([(t.Team.Name, "Team.Name"), (t.Team.ShortName, "Team.ShortName"), (t.Team.CrestUrl, "Team.CrestUrl")]); break;
                case MatchResolutionRequest m:
                    Identity(m.Identity, "Match.Identity"); Reference(m.Competition, "Competition"); Season(m.Match.Season, "Match.Season"); Identity(m.Match.HomeTeam, "Match.HomeTeam"); Identity(m.Match.AwayTeam, "Match.AwayTeam");
                    texts.Add((m.Match.ProviderStatus, "Match.ProviderStatus")); break;
            }
            foreach (var (text, field) in texts.Where(t => t.Text is not null))
                for (var i = 0; i < text!.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (text[i] == '\0' || char.IsLowSurrogate(text[i]) ||
                        (char.IsHighSurrogate(text[i]) && (++i == text.Length || !char.IsLowSurrogate(text[i]))))
                        return new(IdentityResolutionFailureCode.InvalidInput, $"{collection}[{requestIndex}].{field}", "Text must be PostgreSQL-compatible Unicode.");
                }
        }
        return null;
    }

    public FootballPlanningResult Plan(IdentityResolutionBatch batch, FootballDiscovery data, CancellationToken token = default)
    {
        var (snapshot, failure) = SnapshotAndValidate(batch, token);
        return failure is not null ? new(null, failure) : PlanValidated(snapshot!, data, token);
    }

    internal FootballPlanningResult PlanValidated(ValidatedFootballResolutionInput snapshot, FootballDiscovery data, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var batch = snapshot.Input;
        Dictionary<TKey, List<T>> Index<T, TKey>(IEnumerable<T> rows, Func<T, TKey> key) where TKey : notnull
        {
            var index = new Dictionary<TKey, List<T>>();
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                var value = key(row);
                if (!index.TryGetValue(value, out var values)) index.Add(value, values = []);
                values.Add(row);
            }
            return index;
        }
        var sportRows = Index(data.Sports, s => s.Id);
        var competitionRows = Index(data.Competitions, c => c.Id);
        var seasonRows = Index(data.Seasons, s => s.Id);
        var matchRows = Index(data.Matches, m => m.Id);
        var mappingRows = Index(data.Mappings, m => (m.Kind, m.Identity));
        var competitionCandidates = Index(data.Competitions, c => (c.SportId, c.Code));
        var seasonCandidates = Index(data.Seasons, s => (s.CompetitionId, s.Key));
        var discoveredTeams = Index(data.Teams, id => id);
        var discoveredMemberships = Index(data.Memberships, m => m);
        var requestedSeasons = Index(batch.Seasons.Select(s => s.Season).Concat(batch.Matches.Select(m => m.Match.Season)), s => s.Identity);
        var competitions = new List<PlannedResolution>();
        var seasons = new List<PlannedResolution>();
        var teams = new List<PlannedResolution>();
        var matches = new List<PlannedResolution>();
        var memberships = new List<PlannedMembership>();
        var membershipKeys = new HashSet<(PlannedEntity Season, PlannedEntity Team)>();
        var locks = new List<long>();
        var competitionKeys = new Dictionary<(Guid, string), PlannedEntity>();
        var seasonKeys = new Dictionary<(PlannedEntity, string), PlannedEntity>();
        var resolved = new Dictionary<(IdentityLockEntity, ProviderIdentity), PlannedResolution>();
        var requestPath = "Batch";
        try
        {
            void Fail(IdentityResolutionFailureCode code, string field) => throw new PlanningFailure(new(code, $"{requestPath}.{field}", "Identity planning conflict."));
            Guid? Mapping(IdentityLockEntity kind, ProviderIdentity identity, string field)
            {
                token.ThrowIfCancellationRequested();
                locks.Add(keys.Identity(kind, identity));
                var rows = mappingRows.GetValueOrDefault((kind, identity)) ?? [];
                Guid? first = null;
                foreach (var row in rows)
                {
                    token.ThrowIfCancellationRequested();
                    if (first is not null && first != row.TargetId) Fail(IdentityResolutionFailureCode.AmbiguousMappings, field);
                    first = row.TargetId;
                }
                if (rows.Count > 1) Fail(IdentityResolutionFailureCode.DuplicateMappings, field);
                return rows.FirstOrDefault()?.TargetId;
            }
            PlannedResolution Add(IdentityLockEntity kind, ProviderIdentity identity, Guid? id, PlannedEntity? parent = null,
                PlannedEntity? home = null, PlannedEntity? away = null)
            {
                var item = new PlannedResolution(identity, new(kind, id, id is null ? identity : null), parent, home, away);
                resolved.Add((kind, identity), item);
                return item;
            }
            PlannedEntity Existing(IdentityLockEntity kind, Guid id, string field)
            {
                token.ThrowIfCancellationRequested();
                var exists = kind switch
                {
                    IdentityLockEntity.Competition => competitionRows.ContainsKey(id),
                    IdentityLockEntity.Season => seasonRows.ContainsKey(id),
                    IdentityLockEntity.FootballTeam => discoveredTeams.ContainsKey(id),
                    _ => matchRows.ContainsKey(id)
                };
                if (!exists) Fail(IdentityResolutionFailureCode.MissingReference, field);
                return new(kind, id, null);
            }
            PlannedEntity Reference<T>(EntityReference<T> reference, IdentityLockEntity kind, string field, string? identityField = null) where T : class
            {
                token.ThrowIfCancellationRequested();
                if (reference is EntityReference<T>.Internal i) return Existing(kind, i.Id, field);
                var identity = ((EntityReference<T>.External)reference).Identity;
                if (resolved.TryGetValue((kind, identity), out var planned)) return planned.Entity;
                var id = Mapping(kind, identity, identityField ?? field + ".Identity");
                if (id is null) Fail(IdentityResolutionFailureCode.MissingReference, field);
                return Existing(kind, id!.Value, field);
            }
            void CompetitionScope(PlannedEntity competition, string field)
            {
                token.ThrowIfCancellationRequested();
                if (competition.ExistingId is not Guid id) return;
                var row = competitionRows[id].Single();
                var sport = sportRows.GetValueOrDefault(row.SportId)?.SingleOrDefault();
                if (sport is null) Fail(IdentityResolutionFailureCode.MissingReference, field);
                if (sport!.Code != "football" || row.Code != "PL") Fail(IdentityResolutionFailureCode.RelationshipConflict, field);
                locks.Add(keys.Sport(sport.Code)); locks.Add(keys.Competition(row.SportId, row.Code));
            }
            PlannedEntity SeasonParent(PlannedEntity season)
            {
                if (season.ExistingId is Guid id)
                {
                    var row = seasonRows[id].Single();
                    var parent = Existing(IdentityLockEntity.Competition, row.CompetitionId, "Season");
                    CompetitionScope(parent, "Season"); locks.Add(keys.Season(row.CompetitionId, row.Key)); return parent;
                }
                return resolved[(IdentityLockEntity.Season, season.NewIdentity!)].Parent!;
            }
            PlannedResolution Season(PlannedEntity parent, NormalizedSeason request, string field, string parentField)
            {
                token.ThrowIfCancellationRequested();
                CompetitionScope(parent, parentField);
                if (resolved.TryGetValue((IdentityLockEntity.Season, request.Identity), out var prior))
                {
                    var original = requestedSeasons[request.Identity][0];
                    if (prior.Parent != parent) Fail(IdentityResolutionFailureCode.RelationshipConflict, parentField);
                    if (original != request) Fail(IdentityResolutionFailureCode.NaturalKeyConflict, field);
                    return prior;
                }
                var id = Mapping(IdentityLockEntity.Season, request.Identity, field + ".Identity");
                var target = new PlannedEntity(IdentityLockEntity.Season, id, id is null ? request.Identity : null);
                if (seasonKeys.TryGetValue((parent, request.Key), out var owner) && owner != target)
                    Fail(IdentityResolutionFailureCode.NaturalKeyConflict, field + ".Key");
                seasonKeys[(parent, request.Key)] = target;
                if (parent.ExistingId is Guid parentId)
                {
                    locks.Add(keys.Season(parentId, request.Key));
                    foreach (var candidate in seasonCandidates.GetValueOrDefault((parentId, request.Key)) ?? [])
                    {
                        token.ThrowIfCancellationRequested();
                        if (candidate.Id != id) Fail(IdentityResolutionFailureCode.NaturalKeyConflict, field + ".Key");
                    }
                }
                if (id is Guid existing)
                {
                    Existing(IdentityLockEntity.Season, existing, field + ".Identity");
                    var row = seasonRows[existing].Single();
                    if (row.CompetitionId != parent.ExistingId) Fail(IdentityResolutionFailureCode.RelationshipConflict, parentField);
                    if (row.Key != request.Key) Fail(IdentityResolutionFailureCode.NaturalKeyConflict, field + ".Key");
                }
                var item = Add(IdentityLockEntity.Season, request.Identity, id, parent); seasons.Add(item); return item;
            }
            foreach (var (c, index) in batch.Competitions.Select((value, index) => (value, index)))
            {
                token.ThrowIfCancellationRequested();
                requestPath = $"Competitions[{index}]";
                var sport = sportRows.GetValueOrDefault(c.SportId)?.SingleOrDefault();
                if (sport is null) Fail(IdentityResolutionFailureCode.MissingReference, "SportId");
                if (sport!.Code != "football" || c.Competition.Code != "PL") Fail(IdentityResolutionFailureCode.RelationshipConflict, "Competition");
                locks.Add(keys.Sport(sport.Code)); locks.Add(keys.Competition(c.SportId, c.Competition.Code));
                var id = Mapping(IdentityLockEntity.Competition, c.Identity, "Competition.Identity");
                var target = new PlannedEntity(IdentityLockEntity.Competition, id, id is null ? c.Identity : null);
                var candidateConflict = false;
                foreach (var candidate in competitionCandidates.GetValueOrDefault((c.SportId, c.Competition.Code)) ?? [])
                { token.ThrowIfCancellationRequested(); if (candidate.Id != id) { candidateConflict = true; break; } }
                if (candidateConflict ||
                    (competitionKeys.TryGetValue((c.SportId, c.Competition.Code), out var owner) && owner != target))
                    Fail(IdentityResolutionFailureCode.NaturalKeyConflict, "Competition.Code");
                competitionKeys[(c.SportId, c.Competition.Code)] = target;
                if (id is Guid existing)
                {
                    Existing(IdentityLockEntity.Competition, existing, "Competition.Identity");
                    var row = competitionRows[existing].Single();
                    if (row.SportId != c.SportId) Fail(IdentityResolutionFailureCode.RelationshipConflict, "SportId");
                    if (row.Code != c.Competition.Code) Fail(IdentityResolutionFailureCode.NaturalKeyConflict, "Competition.Code");
                }
                competitions.Add(Add(IdentityLockEntity.Competition, c.Identity, id));
            }
            foreach (var (s, index) in batch.Seasons.Select((value, index) => (value, index)))
            {
                token.ThrowIfCancellationRequested();
                requestPath = $"Seasons[{index}]";
                Season(Reference(s.Competition, IdentityLockEntity.Competition, "Competition"), s.Season, "Season", "Competition");
            }
            foreach (var (t, index) in batch.Teams.Select((value, index) => (value, index)))
            {
                token.ThrowIfCancellationRequested();
                requestPath = $"Teams[{index}]";
                var season = Reference(t.Season, IdentityLockEntity.Season, "Season"); SeasonParent(season);
                var id = Mapping(IdentityLockEntity.FootballTeam, t.Identity, "Team.Identity");
                if (id is Guid existing) Existing(IdentityLockEntity.FootballTeam, existing, "Team.Identity");
                var item = Add(IdentityLockEntity.FootballTeam, t.Identity, id, season); teams.Add(item);
                var membership = new PlannedMembership(season, item.Entity,
                    season.ExistingId is Guid sid && id is Guid tid && discoveredMemberships.ContainsKey((sid, tid)));
                if (season.ExistingId is Guid membershipSeason && id is Guid membershipTeam)
                    locks.Add(keys.SeasonTeam(membershipSeason, membershipTeam));
                if (membershipKeys.Add((season, item.Entity))) memberships.Add(membership);
            }
            foreach (var (m, index) in batch.Matches.Select((value, index) => (value, index)))
            {
                token.ThrowIfCancellationRequested();
                requestPath = $"Matches[{index}]";
                var parent = Reference(m.Competition, IdentityLockEntity.Competition, "Competition");
                var season = Season(parent, m.Match.Season, "Match.Season", "Competition").Entity;
                var home = Reference(new EntityReference<FootballTeam>.External(m.Match.HomeTeam), IdentityLockEntity.FootballTeam, "Match.HomeTeam", "Match.HomeTeam");
                var away = Reference(new EntityReference<FootballTeam>.External(m.Match.AwayTeam), IdentityLockEntity.FootballTeam, "Match.AwayTeam", "Match.AwayTeam");
                if (home == away) Fail(IdentityResolutionFailureCode.RelationshipConflict, "Match.AwayTeam");
                foreach (var team in new[] { home, away })
                {
                    token.ThrowIfCancellationRequested();
                    if (season.ExistingId is Guid membershipSeason && team.ExistingId is Guid membershipTeam)
                        locks.Add(keys.SeasonTeam(membershipSeason, membershipTeam));
                    if (!membershipKeys.Contains((season, team)) &&
                        !(season.ExistingId is Guid sid && team.ExistingId is Guid tid && discoveredMemberships.ContainsKey((sid, tid))))
                        Fail(IdentityResolutionFailureCode.MissingReference, team == home ? "Match.HomeTeam" : "Match.AwayTeam");
                    if (membershipKeys.Add((season, team)))
                        memberships.Add(new(season, team, true));
                }
                var id = Mapping(IdentityLockEntity.FootballMatch, m.Identity, "Match.Identity");
                if (id is Guid existing)
                {
                    Existing(IdentityLockEntity.FootballMatch, existing, "Match.Identity");
                    var row = matchRows[existing].Single();
                    if (row.SeasonId != season.ExistingId || row.HomeTeamId != home.ExistingId || row.AwayTeamId != away.ExistingId)
                        Fail(IdentityResolutionFailureCode.RelationshipConflict, "Match");
                }
                matches.Add(Add(IdentityLockEntity.FootballMatch, m.Identity, id, season, home, away));
            }
            var allSeasons = new HashSet<PlannedEntity>();
            foreach (var season in teams.Select(t => t.Parent!).Concat(seasons.Select(s => s.Entity)))
            { token.ThrowIfCancellationRequested(); allSeasons.Add(season); }
            if (allSeasons.Count > 1)
                throw new PlanningFailure(new(IdentityResolutionFailureCode.RelationshipConflict, "Seasons", "Only one season may be planned."));
            var symbolicSeasons = new List<CoordinationProtectedSeasonKey>();
            var symbolicSeasonKeys = new HashSet<CoordinationProtectedSeasonKey>();
            foreach (var season in seasons)
            {
                token.ThrowIfCancellationRequested();
                if (season.Parent!.ExistingId is null)
                {
                    var key = new CoordinationProtectedSeasonKey(season.Parent, requestedSeasons[season.Identity][0].Key);
                    if (symbolicSeasonKeys.Add(key)) symbolicSeasons.Add(key);
                }
            }
            var symbolicMemberships = new List<CoordinationProtectedMembershipKey>();
            foreach (var membership in memberships)
            {
                token.ThrowIfCancellationRequested();
                if (membership.Season.ExistingId is null || membership.Team.ExistingId is null)
                    symbolicMemberships.Add(new(membership.Season, membership.Team));
            }
            token.ThrowIfCancellationRequested();
            return new(new(batch, competitions.AsReadOnly(), seasons.AsReadOnly(), teams.AsReadOnly(), memberships.AsReadOnly(),
                matches.AsReadOnly(), new(locks), symbolicSeasons.AsReadOnly(), symbolicMemberships.AsReadOnly()), null);
        }
        catch (PlanningFailure failure) { return new(null, failure.Failure); }
    }

    private sealed class PlanningFailure(IdentityResolutionFailure failure) : Exception
    { public IdentityResolutionFailure Failure { get; } = failure; }
}
