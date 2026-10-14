namespace SportStats.Infrastructure.Persistence.IdentityResolution;

/// <summary>Compares plan decisions by value, never by collection reference identity.
/// Both plans must originate from the same owned validated input. Resolution order is retained;
/// memberships and symbolic coverage are sets. Individual lock keys are already sorted.</summary>
internal static class FootballResolutionPlanComparison
{
    internal static bool Equivalent(FootballResolutionPlan original, FootballResolutionPlan refreshed, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        bool Sequence<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
        {
            if (left.Count != right.Count) return false;
            for (var i = 0; i < left.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (!EqualityComparer<T>.Default.Equals(left[i], right[i])) return false;
            }
            return true;
        }
        bool Set<T>(IReadOnlyList<T> left, IReadOnlyList<T> right) where T : notnull
        {
            var values = new HashSet<T>();
            foreach (var item in left) { token.ThrowIfCancellationRequested(); values.Add(item); }
            var compared = new HashSet<T>();
            foreach (var item in right)
            {
                token.ThrowIfCancellationRequested();
                if (!values.Contains(item)) return false;
                compared.Add(item);
            }
            return values.Count == compared.Count;
        }
        return ReferenceEquals(original.Input, refreshed.Input) &&
            Sequence(original.Competitions, refreshed.Competitions) && Sequence(original.Seasons, refreshed.Seasons) &&
            Sequence(original.Teams, refreshed.Teams) && Sequence(original.Matches, refreshed.Matches) &&
            Set(original.Memberships, refreshed.Memberships) &&
            original.IndividualDataLocks.CoordinationKey == refreshed.IndividualDataLocks.CoordinationKey &&
            Sequence(original.IndividualDataLocks.OrderedKeys, refreshed.IndividualDataLocks.OrderedKeys) &&
            original.RequiresCoordinationUntilTransactionEnd == refreshed.RequiresCoordinationUntilTransactionEnd &&
            Set(original.CoordinationProtectedSeasonKeys, refreshed.CoordinationProtectedSeasonKeys) &&
            Set(original.CoordinationProtectedMembershipKeys, refreshed.CoordinationProtectedMembershipKeys);
    }
}
