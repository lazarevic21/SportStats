using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Platform;
using SportStats.Infrastructure.Persistence.AdvisoryLocks;
using Xunit;

namespace SportStats.UnitTests;

public sealed class AdvisoryLockKeyTests
{
    private readonly AdvisoryLockKeys keys = new();
    private static readonly Guid Parent = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static ProviderIdentity Identity => new("Provider", "Scope ", "001");

    [Fact]
    public void SeasonTeamKeyHasStableOrderedParentComponents()
    {
        var team = Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00");
        Assert.Equal(6961974173478281512L, keys.SeasonTeam(Parent, team));
        Assert.NotEqual(keys.SeasonTeam(Parent, team), keys.SeasonTeam(team, Parent));
        Assert.Throws<ArgumentException>(() => keys.SeasonTeam(Guid.Empty, team));
        Assert.Throws<ArgumentException>(() => keys.SeasonTeam(Parent, Guid.Empty));
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(6961974173478281512L, keys.SeasonTeam(Parent, team));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void VersionOneVectorsAreStable()
    {
        // Independently computed from the specified framing with PowerShell SHA256/BitConverter.
        Assert.Equal(-2925672972397069994L, keys.FootballCoordination());
        Assert.Equal(-6555775033586225078L, keys.Identity(IdentityLockEntity.Competition, Identity));
        Assert.Equal(7232561902287743401L, keys.Identity(IdentityLockEntity.FootballMatch, new("提供者", "範囲", "⚽é")));
        Assert.Equal(-5539303635710558087L, keys.Sport("football"));
        Assert.Equal(-5675286302564563398L, keys.Competition(Parent, "PL"));
        Assert.Equal(1127588079050856767L, keys.Season(Parent, "2026"));
        Assert.Equal(keys.Identity(IdentityLockEntity.Competition, Identity), new AdvisoryLockKeys().Identity(IdentityLockEntity.Competition, Identity));
    }

    [Fact]
    public void LengthPrefixesMeasureUtf8BytesAndIncludeNamespace()
    {
        byte[]? captured = null;
        var capturingKeys = new AdvisoryLockKeys(input => { captured = input; return new byte[32]; });
        capturingKeys.Identity(IdentityLockEntity.FootballMatch, new("提供者", "範囲", "⚽é"));
        string[] components = [AdvisoryLockKeys.Namespace, "identity", "football-match", "提供者", "範囲", "⚽é"];
        var offset = 0;
        Assert.NotNull(captured);
        foreach (var component in components)
        {
            var expected = Encoding.UTF8.GetBytes(component);
            Assert.Equal((uint)expected.Length, BinaryPrimitives.ReadUInt32BigEndian(captured.AsSpan(offset, 4)));
            offset += 4;
            Assert.Equal(expected, captured.AsSpan(offset, expected.Length).ToArray());
            offset += expected.Length;
        }
        Assert.Equal(captured.Length, offset);
    }

    [Theory]
    [InlineData("ab", "c", "a", "bc")]
    [InlineData("a|b", "c", "a", "b|c")]
    [InlineData("a\0b", "c", "a", "b\0c")]
    public void ComponentBoundariesCannotBeConfused(string firstProvider, string firstScope, string secondProvider, string secondScope)
    {
        Assert.NotEqual(keys.Identity(IdentityLockEntity.Competition, new(firstProvider, firstScope, "id")),
            keys.Identity(IdentityLockEntity.Competition, new(secondProvider, secondScope, "id")));
    }

    [Fact]
    public void EveryIdentityComponentAndEntityTypeAffectsKey()
    {
        var original = keys.Identity(IdentityLockEntity.Competition, Identity);
        ProviderIdentity[] variants =
        [
            Identity with { ProviderKey = "provider" }, Identity with { ProviderKey = "Provider " },
            Identity with { IdentityScope = "scope " }, Identity with { IdentityScope = "Scope" },
            Identity with { ExternalId = "1" }, Identity with { ExternalId = "001 " },
            Identity with { ExternalId = "ABC" }, Identity with { ExternalId = "abc" }
        ];
        Assert.All(variants, value => Assert.NotEqual(original, keys.Identity(IdentityLockEntity.Competition, value)));
        Assert.Equal(4, Enum.GetValues<IdentityLockEntity>().Select(entity => keys.Identity(entity, Identity)).Distinct().Count());
    }

    [Fact]
    public void UnicodeFormsArePreservedAndInvalidSurrogatesAreRejected()
    {
        Assert.NotEqual(keys.Identity(IdentityLockEntity.Season, Identity with { ExternalId = "é" }),
            keys.Identity(IdentityLockEntity.Season, Identity with { ExternalId = "e\u0301" }));
        Assert.NotEqual(keys.Sport("é"), keys.Sport("e\u0301"));
        Assert.Throws<EncoderFallbackException>(() => keys.Identity(IdentityLockEntity.FootballTeam, Identity with { ExternalId = "\ud800" }));
    }

    [Fact]
    public void NaturalKeysMatchDomainTrimmingAndScopedParentsWithoutFoldingCase()
    {
        var sport = new Sport(Parent, "\u2003football\t", "Football");
        var competition = new Competition(Parent, sport, " PL ", "League");
        var season = new Season(Parent, competition, " 2026 ");
        Assert.Equal(keys.Sport(sport.Code), keys.Sport("\u2003football\t"));
        Assert.Equal(keys.Competition(sport.Id, competition.Code), keys.Competition(sport.Id, " PL "));
        Assert.Equal(keys.Season(competition.Id, season.Key), keys.Season(competition.Id, " 2026 "));
        Assert.NotEqual(keys.Sport("football"), keys.Sport("Football"));
        Assert.NotEqual(keys.Competition(Parent, "PL"), keys.Competition(Parent, "pl"));
        Assert.NotEqual(keys.Season(Parent, "YEAR"), keys.Season(Parent, "year"));
        Assert.NotEqual(keys.Competition(Parent, "PL"), keys.Competition(Guid.NewGuid(), "PL"));
        Assert.NotEqual(keys.Season(Parent, "2026"), keys.Season(Guid.NewGuid(), "2026"));
        Assert.NotEqual(keys.Competition(Parent, "2026"), keys.Season(Parent, "2026"));
    }

    [Fact]
    public void DerivationDoesNotDependOnCurrentCulture()
    {
        var original = CultureInfo.CurrentCulture;
        var expected = keys.Competition(Parent, "Iİ");
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(expected, keys.Competition(Parent, "Iİ"));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void EmptyNaturalKeysAndIdentityComponentsAreRejected(string? invalid)
    {
        Assert.ThrowsAny<ArgumentException>(() => keys.Sport(invalid!));
        Assert.ThrowsAny<ArgumentException>(() => keys.Competition(Parent, invalid!));
        Assert.ThrowsAny<ArgumentException>(() => keys.Season(Parent, invalid!));
        Assert.ThrowsAny<ArgumentException>(() => keys.Identity(IdentityLockEntity.Competition, Identity with { ProviderKey = invalid! }));
        Assert.ThrowsAny<ArgumentException>(() => keys.Identity(IdentityLockEntity.Competition, Identity with { IdentityScope = invalid! }));
        Assert.ThrowsAny<ArgumentException>(() => keys.Identity(IdentityLockEntity.Competition, Identity with { ExternalId = invalid! }));
    }

    [Fact]
    public void EmptyParentsUndefinedTypesAndNullIdentityAreRejected()
    {
        Assert.Throws<ArgumentException>(() => keys.Competition(Guid.Empty, "PL"));
        Assert.Throws<ArgumentException>(() => keys.Season(Guid.Empty, "2026"));
        Assert.Throws<ArgumentOutOfRangeException>(() => keys.Identity((IdentityLockEntity)999, Identity));
        Assert.Throws<ArgumentNullException>(() => keys.Identity(IdentityLockEntity.Season, null!));
    }

    [Fact]
    public void PlanSnapshotsDeduplicatesAndSortsSignedKeysWithCoordinationFirst()
    {
        var coordination = keys.FootballCoordination();
        var input = new List<long> { long.MaxValue, 0, -1, long.MinValue, -1, coordination, long.MaxValue };
        var plan = new FootballAdvisoryLockPlan(input);
        input.Clear();
        Assert.Equal(coordination, plan.CoordinationKey);
        Assert.Equal(new[] { long.MinValue, -1, 0, long.MaxValue }, plan.OrderedKeys);
        Assert.Throws<NotSupportedException>(() => ((IList<long>)plan.OrderedKeys).Add(10));
        Assert.Throws<ArgumentNullException>(() => new FootballAdvisoryLockPlan(null!));
    }

    [Fact]
    public void ForcedHashCollisionsSerializeDifferentIdentitiesInsteadOfInventingDistinctKeys()
    {
        var digest = new byte[32];
        BinaryPrimitives.WriteInt64BigEndian(digest, -7);
        var collidingKeys = new AdvisoryLockKeys(_ => digest);
        var first = collidingKeys.Identity(IdentityLockEntity.Competition, Identity);
        var second = collidingKeys.Identity(IdentityLockEntity.FootballMatch, Identity with { ExternalId = "different" });
        Assert.Equal(-7, first);
        Assert.Equal(first, second);
        Assert.Equal(new long[] { -7 }, new FootballAdvisoryLockPlan([first, second], 50).OrderedKeys);
        var coordinationCollision = new FootballAdvisoryLockPlan([first, second], collidingKeys.FootballCoordination());
        Assert.Equal(-7, coordinationCollision.CoordinationKey);
        Assert.Empty(coordinationCollision.OrderedKeys);
    }
}
