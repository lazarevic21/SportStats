namespace SportStats.Domain.Platform;

public sealed class Competition
{
    private Competition() { }
    public Competition(Guid id, Sport sport, string code, string name)
    {
        ArgumentNullException.ThrowIfNull(sport);
        Id = DomainGuard.Id(id, nameof(id));
        Sport = sport;
        SportId = sport.Id;
        Code = DomainGuard.Text(code, nameof(code));
        Name = DomainGuard.Text(name, nameof(name));
    }
    public Competition(Sport sport, string code, string name) : this(Guid.NewGuid(), sport, code, name) { }
    public Guid Id { get; private set; }
    public Guid SportId { get; private set; }
    public Sport Sport { get; private set; } = null!;
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
}
