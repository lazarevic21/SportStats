namespace SportStats.Domain.Football;

public sealed class FootballStandingRow
{
    private FootballStandingRow() { }
    public FootballStandingRow(FootballStandingSnapshot snapshot, FootballSeasonTeam participant, int position)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(participant);
        if (snapshot.SeasonId != participant.SeasonId) throw new ArgumentException("The team must participate in the snapshot season.", nameof(participant));
        if (position < 1) throw new ArgumentOutOfRangeException(nameof(position));
        Snapshot = snapshot;
        SnapshotId = snapshot.Id;
        SeasonId = snapshot.SeasonId;
        Team = participant.Team;
        TeamId = participant.TeamId;
        Position = position;
    }
    public Guid SnapshotId { get; private set; }
    public Guid SeasonId { get; private set; }
    public Guid TeamId { get; private set; }
    public FootballStandingSnapshot Snapshot { get; private set; } = null!;
    public FootballTeam Team { get; private set; } = null!;
    public int Position { get; private set; }
}
