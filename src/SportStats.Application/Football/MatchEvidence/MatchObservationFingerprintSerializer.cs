using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SportStats.Application.Football.Ingestion;
using SportStats.Application.Football.MatchMetadata;
using SportStats.Domain.Football;

namespace SportStats.Application.Football.MatchEvidence;

/// <summary>
/// Pure SS-MATCH-OBS/1 encoding of structurally valid observations. Does not resolve canonical
/// identities, authenticate capture, verify provider claims or authorize synchronization.
/// </summary>
public static class MatchObservationFingerprintSerializer
{
    public const int MaxCanonicalBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ObservationFingerprint Compute(MatchProviderObservation observation)
        => new(Convert.ToHexStringLower(SHA256.HashData(Serialize(observation))));

    /// <summary>Returns a new caller-owned buffer. Invalid input is never repaired or truncated.</summary>
    public static byte[] Serialize(MatchProviderObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        Validate(observation);
        long length = 0;
        MemoryStream? bytes = null;
        Encode(); // Count and validate every string before allocating the output buffer.
        if (length > MaxCanonicalBytes) throw Invalid("SizeLimit");
        using var output = new MemoryStream((int)length);
        bytes = output;
        length = 0;
        Encode();
        return output.ToArray();

        void Encode()
        {
            length += "SS-MATCH-OBS/1\0"u8.Length;
            bytes?.Write("SS-MATCH-OBS/1\0"u8);
            Identity(observation.Identity);
            Identity(observation.Season.Identity);
            Integer(observation.Season.QueryYear);
            Text(observation.Season.Key);
            Text(observation.Season.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Text(observation.Season.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Identity(observation.HomeTeam);
            Identity(observation.AwayTeam);
            Text(observation.Source.ProviderKey);
            Text(observation.Source.Endpoint);
            Presence(observation.KickoffUtc, Timestamp);
            Presence(observation.Matchday, Integer);
            Presence(observation.Status, status => Text(StatusToken(status)));
            Presence(observation.HomeGoals, Integer);
            Presence(observation.AwayGoals, Integer);
            Presence(observation.ProviderLastUpdatedUtc, Timestamp);
            Byte(observation.RawProviderStatus is null ? (byte)0 : (byte)1);
            if (observation.RawProviderStatus is { } raw) Text(raw);
        }

        void Byte(byte value)
        {
            if (++length > MaxCanonicalBytes) throw Invalid("SizeLimit");
            bytes?.WriteByte(value);
        }

        void Identity(ProviderIdentity identity)
        {
            Text(identity.ProviderKey);
            Text(identity.IdentityScope);
            Text(identity.ExternalId);
        }

        void Text(string value)
        {
            int textLength;
            try { textLength = StrictUtf8.GetByteCount(value); }
            catch (EncoderFallbackException) { throw Invalid("TextEncoding"); }
            length += 4L + textLength;
            if (length > MaxCanonicalBytes)
                throw Invalid("SizeLimit");
            if (bytes is null) return;
            Span<byte> prefix = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)textLength);
            bytes.Write(prefix);
            bytes.Write(StrictUtf8.GetBytes(value));
        }

        void Integer(int value) => Text(value.ToString(CultureInfo.InvariantCulture));
        void Timestamp(DateTime value) => Text(value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
        void Presence<T>(MatchObservationField<T> field, Action<T> encode) where T : struct
        {
            Byte(field.Presence switch
            {
                MatchObservationFieldPresence.Missing => 0,
                MatchObservationFieldPresence.Null => 1,
                MatchObservationFieldPresence.Value => 2,
                _ => throw Invalid("Presence")
            });
            if (field.Presence == MatchObservationFieldPresence.Value) encode(field.Value!.Value);
        }
    }

    private static void Validate(MatchProviderObservation value)
    {
        if (MatchMetadataChangeEvaluator.Validate(value) is { } field) throw Invalid(field);
        if (value.Season.StartDate.Year != value.Season.QueryYear) throw Invalid("Season");
        if (value.HomeTeam == value.AwayTeam)
            throw Invalid("Participants");
        if (value.Season.Identity.ProviderKey != value.Identity.ProviderKey ||
            value.HomeTeam.ProviderKey != value.Identity.ProviderKey || value.AwayTeam.ProviderKey != value.Identity.ProviderKey)
            throw Invalid("ProviderConsistency");
        CheckPresence(value.KickoffUtc);
        CheckPresence(value.Matchday);
        CheckPresence(value.Status);
        CheckPresence(value.HomeGoals);
        CheckPresence(value.AwayGoals);
        CheckPresence(value.ProviderLastUpdatedUtc);
        if (value.Status.Value is { } status) _ = StatusToken(status);
    }

    private static void CheckPresence<T>(MatchObservationField<T> field) where T : struct
    {
        if (!Enum.IsDefined(field.Presence) ||
            (field.Presence == MatchObservationFieldPresence.Value) != field.Value.HasValue)
            throw Invalid("Presence");
    }

    private static string StatusToken(FootballMatchStatus status) => status switch
    {
        FootballMatchStatus.Scheduled => "Scheduled",
        FootballMatchStatus.Live => "Live",
        FootballMatchStatus.Finished => "Finished",
        FootballMatchStatus.Postponed => "Postponed",
        FootballMatchStatus.Cancelled => "Cancelled",
        FootballMatchStatus.Suspended => "Suspended",
        _ => throw Invalid("Status")
    };

    private static ArgumentException Invalid(string field)
        => new($"Invalid observation fingerprint input: {field}.", "observation");
}
