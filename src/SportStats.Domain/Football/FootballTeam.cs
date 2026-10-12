namespace SportStats.Domain.Football;

public sealed class FootballTeam
{
    private FootballTeam() { }
    public FootballTeam(Guid id, string name, string? shortName = null, string? crestUrl = null)
    {
        Id = DomainGuard.Id(id, nameof(id));
        Name = DomainGuard.Text(name, nameof(name));
        ShortName = shortName;
        CrestUrl = crestUrl;
    }
    public FootballTeam(string name, string? shortName = null, string? crestUrl = null) : this(Guid.NewGuid(), name, shortName, crestUrl) { }
    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string? ShortName { get; private set; }
    public string? CrestUrl { get; private set; }
}
