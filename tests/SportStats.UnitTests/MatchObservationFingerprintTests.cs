using System.Globalization;
using System.Text;
using SportStats.Application.Football.Ingestion;
using SportStats.Application.Football.MatchEvidence;
using SportStats.Application.Football.MatchSynchronization;
using SportStats.Domain.Football;
using Xunit;

namespace SportStats.UnitTests;

public sealed class MatchObservationFingerprintTests
{
    private static readonly DateTime Utc = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    private static ProviderIdentity Id(string value) => new("p", "s", value);
    private static MatchProviderObservation Observation() => new(
        Id("m"), new("p", "/matches"),
        new(Id("season"), 2026, "2026", new(2026, 8, 1), new(2027, 6, 1)),
        Id("home"), Id("away"), default, default, default, null, default, default, default, Utc);

    private static MatchProviderObservation Vector(string name) => name switch
    {
        "missing" => Observation(),
        "nulls" => Observation() with {
            KickoffUtc = MatchObservationField<DateTime>.Null, Matchday = MatchObservationField<int>.Null,
            HomeGoals = MatchObservationField<int>.Null, AwayGoals = MatchObservationField<int>.Null,
            ProviderLastUpdatedUtc = MatchObservationField<DateTime>.Null },
        "values" => Observation() with {
            KickoffUtc = MatchObservationField<DateTime>.FromValue(Utc.AddTicks(1)),
            Matchday = MatchObservationField<int>.FromValue(1),
            Status = MatchObservationField<FootballMatchStatus>.FromValue(FootballMatchStatus.Live),
            HomeGoals = MatchObservationField<int>.FromValue(0), AwayGoals = MatchObservationField<int>.FromValue(2),
            ProviderLastUpdatedUtc = MatchObservationField<DateTime>.FromValue(Utc), RawProviderStatus = "IN_PLAY" },
        _ => throw new ArgumentException(nameof(name))
    };

    // Fixed vectors independently transcribed from the SS-MATCH-OBS/1 wire grammar;
    // hashes calculated outside the production encoder, not inferred from its output.
    public static IEnumerable<object[]> GoldenVectors()
    {
        yield return ["missing", "53532d4d415443482d4f42532f310000000001700000000173000000016d0000000170000000017300000006736561736f6e000000043230323600000004323032360000000a323032362d30382d30310000000a323032372d30362d30310000000170000000017300000004686f6d650000000170000000017300000004617761790000000170000000082f6d61746368657300000000000000", "6eff50d7464f47588fb0fa72b3b72d1d6802c641858eb0c53742347b2322cd0e"];
        yield return ["nulls", "53532d4d415443482d4f42532f310000000001700000000173000000016d0000000170000000017300000006736561736f6e000000043230323600000004323032360000000a323032362d30382d30310000000a323032372d30362d30310000000170000000017300000004686f6d650000000170000000017300000004617761790000000170000000082f6d61746368657301010001010100", "389857d64cef7254c45e19b70a0e180911cf3d09c5ddde7fdca8aecc8f79d255"];
        yield return ["values", "53532d4d415443482d4f42532f310000000001700000000173000000016d0000000170000000017300000006736561736f6e000000043230323600000004323032360000000a323032362d30382d30310000000a323032372d30362d30310000000170000000017300000004686f6d650000000170000000017300000004617761790000000170000000082f6d617463686573020000001c323032362d31302d31385431323a30303a30302e303030303030315a02000000013102000000044c697665020000000130020000000132020000001c323032362d31302d31385431323a30303a30302e303030303030305a0100000007494e5f504c4159", "fd0a70e3b413857dc008cb3d1e5e2a131c74b7aa5aeb6ebb98a32b2c861d27c2"];
    }

    [Theory]
    [MemberData(nameof(GoldenVectors))]
    public void CanonicalBytesAndSha256MatchIndependentGoldenVectors(string name, string hex, string hash)
    {
        var observation = Vector(name);
        Assert.Equal(Convert.FromHexString(hex), MatchObservationFingerprintSerializer.Serialize(observation));
        var fingerprint = MatchObservationFingerprintSerializer.Compute(observation);
        Assert.Equal("SS-MATCH-OBS/1", fingerprint.Schema);
        Assert.Equal("SHA-256", fingerprint.Algorithm);
        Assert.Equal(hash, fingerprint.LowerHex);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    [InlineData("tr-TR")]
    public void CultureDoesNotChangeDatesIntegersOrStatus(string culture)
    {
        var prior = CultureInfo.CurrentCulture;
        var priorUi = CultureInfo.CurrentUICulture;
        try
        {
            var expected = MatchObservationFingerprintSerializer.Serialize(Vector("values"));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(expected, MatchObservationFingerprintSerializer.Serialize(Vector("values")));
        }
        finally { CultureInfo.CurrentCulture = prior; CultureInfo.CurrentUICulture = priorUi; }
    }

    [Fact]
    public void ReturnedBytesCannotMutateSubsequentSerialization()
    {
        var first = MatchObservationFingerprintSerializer.Serialize(Vector("values"));
        first[0] ^= 0xff;
        Assert.Equal((byte)'S', MatchObservationFingerprintSerializer.Serialize(Vector("values"))[0]);
    }

    [Fact]
    public void FingerprintHasFixedSchemaAndNoPublicEnvelopeConstructionOrMutation()
    {
        var type = typeof(ObservationFingerprint);
        Assert.Empty(type.GetConstructors());
        Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
        var first = MatchObservationFingerprintSerializer.Compute(Observation());
        var second = MatchObservationFingerprintSerializer.Compute(Observation());
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, MatchObservationFingerprintSerializer.Compute(Vector("values")));
        Assert.False(first.Equals(null));
        Assert.False(first.Equals(first.LowerHex));
    }

    [Fact]
    public void UnverifiedWrapperCannotSupplyEvidenceToExistingPolicy()
    {
        var observation = Vector("values");
        var wrapper = new UnverifiedObservationEvidence(observation);
        Assert.Equal(observation, wrapper.Observation);
        Assert.Equal(MatchObservationFingerprintSerializer.Compute(observation), wrapper.Fingerprint);
        Assert.All(typeof(UnverifiedObservationEvidence).GetProperties(), p => Assert.Null(p.SetMethod));
        Assert.DoesNotContain(typeof(MatchSynchronizationPolicyEngine).GetMethods().SelectMany(m => m.GetParameters()),
            p => p.ParameterType == typeof(UnverifiedObservationEvidence) || p.ParameterType == typeof(ObservationFingerprint));
        Assert.False(new MatchSynchronizationEvidence().HasVerifiedAuthority);
        Assert.Throws<ArgumentNullException>(() => new UnverifiedObservationEvidence(null!));
        Assert.ThrowsAny<ArgumentException>(() => new UnverifiedObservationEvidence(observation with {
            HomeGoals = MatchObservationField<int>.FromValue(-1) }));
    }

    [Fact]
    public void RetrievalTimeIsExcludedButProviderClaimsAndTickPrecisionAreIncluded()
    {
        var observation = Vector("values");
        Assert.Equal(Hash(observation), Hash(observation with { RetrievedAtUtc = Utc.AddYears(1) }));
        Assert.NotEqual(Hash(observation), Hash(observation with { ProviderLastUpdatedUtc = MatchObservationField<DateTime>.FromValue(Utc.AddTicks(1)) }));
        Assert.NotEqual(Hash(observation), Hash(observation with { RawProviderStatus = "PAUSED" }));
        Assert.NotEqual(Hash(observation), Hash(observation with { KickoffUtc = MatchObservationField<DateTime>.FromValue(Utc) }));
    }

    [Fact]
    public void UnicodeUsesByteLengthsAndPreservesOrdinalContentIncludingNul()
    {
        var baseline = Observation();
        var unicode = baseline with { RawProviderStatus = "é😀\0" };
        var bytes = MatchObservationFingerprintSerializer.Serialize(unicode);
        // N Value + S byte count: 2 + 4 + 1 = 7 UTF-8 bytes, not four UTF-16 chars.
        Assert.Equal(new byte[] { 1, 0, 0, 0, 7, 0xc3, 0xa9, 0xf0, 0x9f, 0x98, 0x80, 0 }, bytes[^12..]);
        Assert.NotEqual(Hash(baseline), Hash(baseline with { RawProviderStatus = "" }));
        Assert.NotEqual(Hash(unicode), Hash(baseline with { RawProviderStatus = "e\u0301😀\0" }));
        Assert.NotEqual(Hash(baseline with { RawProviderStatus = "Live" }), Hash(baseline with { RawProviderStatus = "live" }));
    }

    [Fact]
    public void NullableFieldsDistinguishMissingNullAndZero()
    {
        var o = Observation();
        var hashes = new[] { Hash(o), Hash(o with { HomeGoals = MatchObservationField<int>.Null }),
            Hash(o with { HomeGoals = MatchObservationField<int>.FromValue(0) }) };
        Assert.Equal(3, hashes.Distinct().Count());
        Assert.NotEqual(Hash(o with { KickoffUtc = MatchObservationField<DateTime>.Null }), Hash(o));
        Assert.NotEqual(Hash(o with { Matchday = MatchObservationField<int>.Null }), Hash(o));
        Assert.NotEqual(Hash(o with { AwayGoals = MatchObservationField<int>.Null }), Hash(o));
        Assert.NotEqual(Hash(o with { ProviderLastUpdatedUtc = MatchObservationField<DateTime>.Null }), Hash(o));
    }

    [Theory]
    [InlineData(FootballMatchStatus.Scheduled, "Scheduled")]
    [InlineData(FootballMatchStatus.Live, "Live")]
    [InlineData(FootballMatchStatus.Finished, "Finished")]
    [InlineData(FootballMatchStatus.Postponed, "Postponed")]
    [InlineData(FootballMatchStatus.Cancelled, "Cancelled")]
    [InlineData(FootballMatchStatus.Suspended, "Suspended")]
    public void StatusUsesStableTokenRatherThanEnumOrdinal(FootballMatchStatus status, string token)
    {
        var bytes = MatchObservationFingerprintSerializer.Serialize(Observation() with {
            Status = MatchObservationField<FootballMatchStatus>.FromValue(status) });
        var prefixLength = MatchObservationFingerprintSerializer.Serialize(Observation()).Length - 5;
        var expected = new byte[] { 2, 0, 0, 0, (byte)token.Length }.Concat(Encoding.ASCII.GetBytes(token)).ToArray();
        Assert.Equal(expected, bytes.Skip(prefixLength).Take(expected.Length));
    }

    [Fact]
    public void ExactIdentitySourceSeasonAndParticipantOrderAffectFingerprint()
    {
        var o = Observation();
        var variants = new[] {
            o with { Identity = o.Identity with { ExternalId = "other" } },
            o with { Identity = o.Identity with { IdentityScope = "other" } },
            o with { Source = o.Source with { Endpoint = "/other" } },
            o with { Season = o.Season with { Identity = Id("other-season") } },
            o with { Season = o.Season with { Key = "2026/27" } },
            o with { Season = o.Season with { EndDate = new(2027, 6, 2) } },
            o with { HomeTeam = o.AwayTeam, AwayTeam = o.HomeTeam } };
        Assert.All(variants, variant => Assert.NotEqual(Hash(o), Hash(variant)));
    }

    public static IEnumerable<object[]> InvalidObservations()
    {
        var o = Observation();
        yield return [o with { Status = MatchObservationField<FootballMatchStatus>.Null }];
        yield return [o with { Status = MatchObservationField<FootballMatchStatus>.FromValue(FootballMatchStatus.Unknown) }];
        yield return [o with { Status = MatchObservationField<FootballMatchStatus>.FromValue((FootballMatchStatus)999) }];
        yield return [o with { KickoffUtc = MatchObservationField<DateTime>.FromValue(DateTime.SpecifyKind(Utc, DateTimeKind.Local)) }];
        yield return [o with { ProviderLastUpdatedUtc = MatchObservationField<DateTime>.FromValue(DateTime.SpecifyKind(Utc, DateTimeKind.Unspecified)) }];
        yield return [o with { RetrievedAtUtc = DateTime.SpecifyKind(Utc, DateTimeKind.Unspecified) }];
        yield return [o with { Matchday = MatchObservationField<int>.FromValue(0) }];
        yield return [o with { HomeGoals = MatchObservationField<int>.FromValue(-1) }];
        yield return [o with { AwayGoals = MatchObservationField<int>.FromValue(-1) }];
        yield return [o with { Identity = Id("") }];
        yield return [o with { Source = new("wrong", "/matches") }];
        yield return [o with { HomeTeam = o.AwayTeam }];
        yield return [o with { HomeTeam = o.HomeTeam with { ProviderKey = "wrong" } }];
        yield return [o with { Season = o.Season with { QueryYear = 0 } }];
        yield return [o with { Season = o.Season with { QueryYear = 10000 } }];
        yield return [o with { Season = o.Season with { QueryYear = 2025 } }];
        yield return [o with { Season = o.Season with { Identity = o.Season.Identity with { ProviderKey = "wrong" } } }];
        yield return [o with { Season = o.Season with { StartDate = new(2028, 1, 1) } }];
        yield return [o with { RawProviderStatus = "\ud800" }];
        yield return [o with { Identity = null! }];
        yield return [o with { Source = null! }];
        yield return [o with { Season = null! }];
        yield return [o with { HomeTeam = null! }];
        yield return [o with { AwayTeam = null! }];
    }

    [Theory]
    [MemberData(nameof(InvalidObservations))]
    public void InvalidObservationCannotReceiveAcceptedBytesOrFingerprint(MatchProviderObservation observation)
    {
        Assert.ThrowsAny<ArgumentException>(() => MatchObservationFingerprintSerializer.Serialize(observation));
        Assert.ThrowsAny<ArgumentException>(() => MatchObservationFingerprintSerializer.Compute(observation));
    }

    [Fact]
    public void NullObservationFailsBeforeEncoding()
    {
        Assert.Throws<ArgumentNullException>(() => MatchObservationFingerprintSerializer.Serialize(null!));
        Assert.Throws<ArgumentNullException>(() => MatchObservationFingerprintSerializer.Compute(null!));
    }

    [Fact]
    public void CanonicalPayloadLimitAcceptsExactBoundAndRejectsOverflowWithoutTruncating()
    {
        const int limit = 16 * 1024 * 1024;
        var observation = Observation();
        // Null N occupies one byte; present N adds a four-byte UTF-8 length.
        var textLength = limit - MatchObservationFingerprintSerializer.Serialize(observation).Length - 4;
        var exact = observation with { RawProviderStatus = new string('a', textLength) };
        Assert.Equal(limit, MatchObservationFingerprintSerializer.Serialize(exact).Length);
        Assert.ThrowsAny<ArgumentException>(() => MatchObservationFingerprintSerializer.Serialize(
            observation with { RawProviderStatus = new string('a', textLength + 1) }));
    }

    [Fact]
    public void TrailingPresenceTagsCountTowardTotalPayloadBound()
    {
        const int limit = 16 * 1024 * 1024;
        var observation = Observation();
        var endpointLength = limit - MatchObservationFingerprintSerializer.Serialize(observation).Length
            + Encoding.UTF8.GetByteCount(observation.Source.Endpoint);
        Assert.Equal(limit, MatchObservationFingerprintSerializer.Serialize(observation with {
            Source = new("p", new string('a', endpointLength)) }).Length);
        Assert.ThrowsAny<ArgumentException>(() => MatchObservationFingerprintSerializer.Serialize(observation with {
            Source = new("p", new string('a', endpointLength + 1)) }));
    }

    private static string Hash(MatchProviderObservation observation) =>
        MatchObservationFingerprintSerializer.Compute(observation).LowerHex;

    [Fact]
    public void EachMetadataValueAndCoherentProviderChangeAffectsContent()
    {
        var o = Vector("values");
        var variants = new[] {
            o with { Matchday = MatchObservationField<int>.FromValue(2) },
            o with { HomeGoals = MatchObservationField<int>.FromValue(1) },
            o with { AwayGoals = MatchObservationField<int>.FromValue(3) },
            o with { Status = MatchObservationField<FootballMatchStatus>.FromValue(FootballMatchStatus.Finished) },
            o with {
                Identity = o.Identity with { ProviderKey = "another-provider" },
                Season = o.Season with { Identity = o.Season.Identity with { ProviderKey = "another-provider" } },
                HomeTeam = o.HomeTeam with { ProviderKey = "another-provider" },
                AwayTeam = o.AwayTeam with { ProviderKey = "another-provider" },
                Source = o.Source with { ProviderKey = "another-provider" } } };
        Assert.All(variants, variant => Assert.NotEqual(Hash(o), Hash(variant)));
    }

    [Fact]
    public void EquivalentUtcSpellingsAndRepeatedCallsKeepTheSameBytes()
    {
        var o = Vector("values");
        var utc = DateTime.ParseExact("2026-10-18T12:00:00.0000000Z",
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var equivalent = o with { ProviderLastUpdatedUtc = MatchObservationField<DateTime>.FromValue(utc) };
        var expected = MatchObservationFingerprintSerializer.Serialize(o);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(expected, MatchObservationFingerprintSerializer.Serialize(equivalent));
            Assert.Equal(Hash(o), Hash(equivalent));
        }
    }

    [Fact]
    public void RejectedTextDoesNotAppearInExceptions()
    {
        const string marker = "sensitive-attacker-controlled-text";
        var o = Observation();
        foreach (var invalid in new[] {
            o with { Identity = o.Identity with { ExternalId = marker + "\ud800" } },
            o with { Source = o.Source with { Endpoint = marker + "\udfff" } },
            o with { RawProviderStatus = marker + "\ud800" } })
        {
            var error = Assert.ThrowsAny<ArgumentException>(() => MatchObservationFingerprintSerializer.Serialize(invalid));
            Assert.DoesNotContain(marker, error.ToString());
            Assert.Null(error.InnerException);
        }
    }
}

