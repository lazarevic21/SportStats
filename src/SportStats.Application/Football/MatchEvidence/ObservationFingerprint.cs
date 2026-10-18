namespace SportStats.Application.Football.MatchEvidence;

/// <summary>Content equality under a named encoding; never authenticity, ordering or write authority.</summary>
public sealed class ObservationFingerprint : IEquatable<ObservationFingerprint>
{
    internal ObservationFingerprint(string lowerHex)
    {
        ArgumentNullException.ThrowIfNull(lowerHex);
        if (lowerHex.Length != 64 || lowerHex.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("A SHA-256 fingerprint requires 64 lowercase hexadecimal digits.", nameof(lowerHex));
        LowerHex = lowerHex;
    }

    public string Schema => "SS-MATCH-OBS/1";
    public string Algorithm => "SHA-256";
    public string LowerHex { get; }

    public bool Equals(ObservationFingerprint? other) => other is not null &&
        StringComparer.Ordinal.Equals(LowerHex, other.LowerHex);
    public override bool Equals(object? obj) => obj is ObservationFingerprint other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(LowerHex);
}
