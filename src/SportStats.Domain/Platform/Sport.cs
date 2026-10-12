namespace SportStats.Domain.Platform;

public sealed class Sport
{
    private Sport() { }
    public Sport(Guid id, string code, string name)
    {
        Id = DomainGuard.Id(id, nameof(id));
        Code = DomainGuard.Text(code, nameof(code));
        Name = DomainGuard.Text(name, nameof(name));
    }
    public Sport(string code, string name) : this(Guid.NewGuid(), code, name) { }
    public Guid Id { get; private set; }
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
}
