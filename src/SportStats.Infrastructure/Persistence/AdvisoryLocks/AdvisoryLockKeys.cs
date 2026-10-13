using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SportStats.Application.Football.Ingestion;

namespace SportStats.Infrastructure.Persistence.AdvisoryLocks;

public enum IdentityLockEntity { Competition, Season, FootballTeam, FootballMatch }

/// <summary>
/// Wire format v1: each component, including the namespace, is strict UTF-8 preceded by its
/// unsigned 32-bit byte length in big-endian order. SHA-256's first eight bytes are interpreted
/// as a big-endian signed Int64. No runtime string hash, platform endianness, case folding,
/// or Unicode normalization is used. Changing this format requires coordinating all writers.
/// </summary>
public sealed class AdvisoryLockKeys
{
    public const string Namespace = "sportstats/advisory-lock/v1";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Func<byte[], byte[]> hash;

    public AdvisoryLockKeys() : this(bytes => SHA256.HashData(bytes)) { }

    // Test-only seam for simulating digest collisions; production always uses SHA-256.
    internal AdvisoryLockKeys(Func<byte[], byte[]> hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        this.hash = hash;
    }

    public long FootballCoordination() => Derive("coordination", "football");

    public long Identity(IdentityLockEntity entity, ProviderIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var entityName = entity switch
        {
            IdentityLockEntity.Competition => "competition",
            IdentityLockEntity.Season => "season",
            IdentityLockEntity.FootballTeam => "football-team",
            IdentityLockEntity.FootballMatch => "football-match",
            _ => throw new ArgumentOutOfRangeException(nameof(entity))
        };
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ProviderKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.IdentityScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ExternalId);
        return Derive("identity", entityName, identity.ProviderKey, identity.IdentityScope, identity.ExternalId);
    }

    public long Sport(string code) => Derive("natural", "sport", NaturalText(code));
    public long Competition(Guid sportId, string code) => Derive("natural", "competition", ParentId(sportId), NaturalText(code));
    public long Season(Guid competitionId, string key) => Derive("natural", "season", ParentId(competitionId), NaturalText(key));

    private static string NaturalText(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim(); // Matches DomainGuard.Text; case and Unicode forms are preserved.
    }

    private static string ParentId(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A parent identifier is required.", nameof(id));
        return id.ToString("N"); // Fixed lower-case hexadecimal, independent of culture and byte layout.
    }

    private long Derive(params string[] components)
    {
        using var buffer = new MemoryStream();
        Span<byte> prefix = stackalloc byte[4];
        foreach (var component in new[] { Namespace }.Concat(components))
        {
            var bytes = Utf8.GetBytes(component);
            BinaryPrimitives.WriteUInt32BigEndian(prefix, checked((uint)bytes.Length));
            buffer.Write(prefix);
            buffer.Write(bytes);
        }
        var digest = hash(buffer.ToArray());
        if (digest is null || digest.Length != 32) throw new InvalidOperationException("A SHA-256 digest must contain 32 bytes.");
        return BinaryPrimitives.ReadInt64BigEndian(digest);
    }
}

/// <summary>
/// Immutable snapshot of a complete football lock set. Hash collisions only serialize unrelated
/// work: they never identify entities. A data key colliding with coordination is already covered.
/// All cooperating writers must follow this order before reading or changing protected data.
/// </summary>
public sealed class FootballAdvisoryLockPlan
{
    public FootballAdvisoryLockPlan(IEnumerable<long> keys)
        : this(keys, new AdvisoryLockKeys().FootballCoordination()) { }

    internal FootballAdvisoryLockPlan(IEnumerable<long> keys, long coordinationKey)
    {
        ArgumentNullException.ThrowIfNull(keys);
        CoordinationKey = coordinationKey;
        OrderedKeys = Array.AsReadOnly(keys.Where(key => key != coordinationKey).Distinct().Order().ToArray());
    }

    public long CoordinationKey { get; }
    public IReadOnlyList<long> OrderedKeys { get; }
}
