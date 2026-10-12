namespace SportStats.Domain.Platform;

public sealed class Season
{
    private Season() { }
    public Season(Guid id, Competition competition, string key, DateOnly? startDate = null, DateOnly? endDate = null)
    {
        ArgumentNullException.ThrowIfNull(competition);
        if (startDate.HasValue && endDate.HasValue && startDate > endDate) throw new ArgumentException("Season end must not precede its start.", nameof(endDate));
        Id = DomainGuard.Id(id, nameof(id));
        Competition = competition;
        CompetitionId = competition.Id;
        Key = DomainGuard.Text(key, nameof(key));
        StartDate = startDate;
        EndDate = endDate;
    }
    public Season(Competition competition, string key, DateOnly? startDate = null, DateOnly? endDate = null) : this(Guid.NewGuid(), competition, key, startDate, endDate) { }
    public Guid Id { get; private set; }
    public Guid CompetitionId { get; private set; }
    public Competition Competition { get; private set; } = null!;
    public string Key { get; private set; } = null!;
    public DateOnly? StartDate { get; private set; }
    public DateOnly? EndDate { get; private set; }
}
