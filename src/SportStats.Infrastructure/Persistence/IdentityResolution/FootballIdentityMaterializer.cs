using System.Collections.ObjectModel;
using SportStats.Application.Football.Ingestion;
using SportStats.Infrastructure.Persistence.AdvisoryLocks;

namespace SportStats.Infrastructure.Persistence.IdentityResolution;

// Canonical execution-local identity handles, not EF entities or persistence instructions.
// Existing domain objects must later be loaded once under the owning executor's transaction.
internal sealed record MaterializedEntity(PlannedEntity Reference, Guid Id)
{
    public IdentityLockEntity Kind => Reference.Kind;
    public bool IsNew => Reference.ExistingId is null;
}

internal sealed record MaterializedResolution(ProviderIdentity Identity, MaterializedEntity Entity,
    MaterializedEntity? Parent, MaterializedEntity? Home, MaterializedEntity? Away);
internal sealed record MaterializedMembership(MaterializedEntity Season, MaterializedEntity Team, bool Exists);
internal sealed record MaterializedExternalMapping(IdentityLockEntity Kind, ProviderIdentity Identity, Guid Id, Guid EntityId);

internal sealed record FootballIdentityMaterialization(
    IReadOnlyDictionary<PlannedEntity, MaterializedEntity> Entities,
    IReadOnlyList<MaterializedResolution> Competitions,
    IReadOnlyList<MaterializedResolution> Seasons,
    IReadOnlyList<MaterializedResolution> Teams,
    IReadOnlyList<MaterializedMembership> Memberships,
    IReadOnlyList<MaterializedResolution> Matches,
    IReadOnlyList<MaterializedExternalMapping> NewMappings);

/// <summary>
/// Converts an approved plan's references into execution-local IDs without loading/constructing
/// domain objects, accessing persistence, or changing the plan. Every call is an independent
/// attempt; no allocation dictionary survives a call. Allocations are not committed resolutions
/// and confer no transaction or lock ownership. Symbolic natural keys still require coordination
/// through the future owning transaction's completion, as required by FootballResolutionPlan.
/// </summary>
internal sealed class FootballIdentityMaterializer
{
    private readonly Func<Guid> generateId;

    public FootballIdentityMaterializer() : this(Guid.NewGuid) { }

    // Offline seam; production uses Guid.NewGuid. Invalid/colliding generators fail, never retry.
    internal FootballIdentityMaterializer(Func<Guid> generateId)
    {
        ArgumentNullException.ThrowIfNull(generateId);
        this.generateId = generateId;
    }

    public FootballIdentityMaterialization Materialize(FootballResolutionPlan plan, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        token.ThrowIfCancellationRequested();
        var entities = new Dictionary<PlannedEntity, MaterializedEntity>();
        var usedIds = new HashSet<Guid>();
        var memberships = new List<MaterializedMembership>();
        var membershipKeys = new Dictionary<(Guid SeasonId, Guid TeamId), MaterializedMembership>();
        var mappings = new List<MaterializedExternalMapping>();
        var mappingKeys = new Dictionary<(IdentityLockEntity Kind, ProviderIdentity Identity), MaterializedExternalMapping>();

        void CheckReference(PlannedEntity reference)
        {
            token.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(reference.Kind) ||
                (reference.ExistingId is Guid id ? id == Guid.Empty || reference.NewIdentity is not null : reference.NewIdentity is null))
                throw new InvalidOperationException("The plan contains an invalid entity reference.");
        }

        // Reserve existing IDs first so generator collisions cannot alias newly allocated handles.
        void Reserve(PlannedEntity? reference)
        {
            token.ThrowIfCancellationRequested();
            if (reference is null) return;
            CheckReference(reference);
            if (reference.ExistingId is Guid id) usedIds.Add(id);
        }
        foreach (var resolution in plan.Competitions.Concat(plan.Seasons).Concat(plan.Teams).Concat(plan.Matches))
        {
            Reserve(resolution.Entity); Reserve(resolution.Parent); Reserve(resolution.Home); Reserve(resolution.Away);
        }
        foreach (var membership in plan.Memberships) { Reserve(membership.Season); Reserve(membership.Team); }

        Guid NewId()
        {
            token.ThrowIfCancellationRequested();
            var id = generateId();
            token.ThrowIfCancellationRequested();
            if (id == Guid.Empty || !usedIds.Add(id))
                throw new InvalidOperationException("The identifier generator returned an empty or reused ID.");
            return id;
        }

        MaterializedEntity Entity(PlannedEntity reference)
        {
            CheckReference(reference);
            if (!entities.TryGetValue(reference, out var entity))
            {
                entity = new(reference, reference.ExistingId ?? NewId());
                entities.Add(reference, entity);
            }
            return entity;
        }

        IReadOnlyList<MaterializedResolution> Resolutions(IReadOnlyList<PlannedResolution> source)
        {
            var result = new List<MaterializedResolution>();
            foreach (var resolution in source)
            {
                token.ThrowIfCancellationRequested();
                var entity = Entity(resolution.Entity);
                var parent = resolution.Parent is null ? null : Entity(resolution.Parent);
                var home = resolution.Home is null ? null : Entity(resolution.Home);
                var away = resolution.Away is null ? null : Entity(resolution.Away);
                result.Add(new(resolution.Identity, entity, parent, home, away));
                if (entity.IsNew)
                {
                    var key = (entity.Kind, resolution.Identity);
                    if (mappingKeys.TryGetValue(key, out var prior))
                    {
                        if (prior.EntityId != entity.Id)
                            throw new InvalidOperationException("The plan assigns an identity to incompatible entities.");
                    }
                    else
                    {
                        var mapping = new MaterializedExternalMapping(entity.Kind, resolution.Identity, NewId(), entity.Id);
                        mappingKeys.Add(key, mapping);
                        mappings.Add(mapping);
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            return result.AsReadOnly();
        }

        var competitions = Resolutions(plan.Competitions);
        var seasons = Resolutions(plan.Seasons); // Explicit input seasons first, followed by implicit match dependencies.
        var teams = Resolutions(plan.Teams);
        foreach (var membership in plan.Memberships)
        {
            token.ThrowIfCancellationRequested();
            var season = Entity(membership.Season);
            var team = Entity(membership.Team);
            var key = (season.Id, team.Id);
            if (membershipKeys.TryGetValue(key, out var prior))
            {
                if (prior.Exists != membership.Exists)
                    throw new InvalidOperationException("The plan contains inconsistent membership decisions.");
            }
            else
            {
                var item = new MaterializedMembership(season, team, membership.Exists);
                membershipKeys.Add(key, item);
                memberships.Add(item);
            }
        }
        var matches = Resolutions(plan.Matches);
        token.ThrowIfCancellationRequested();
        return new(new ReadOnlyDictionary<PlannedEntity, MaterializedEntity>(entities), competitions, seasons, teams,
            memberships.AsReadOnly(), matches, mappings.AsReadOnly());
    }
}
