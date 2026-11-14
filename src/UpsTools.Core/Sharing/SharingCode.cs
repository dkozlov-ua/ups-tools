using System.Security.Cryptography;
using System.Text;

namespace UpsTools.Core.Sharing;

/// <summary>
/// The secret another PC needs to watch this PC's UPSes: 16 characters (80 bits) of Crockford's base32, shown as
/// four groups of four, e.g. <c>7KQ2-M9XD-4FTB-R1WE</c>. Both ends derive the key of their link from it
/// (<see cref="SecureChannel"/>); it never goes over the network.
/// </summary>
public static class SharingCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int Length = 16;

    public static string New()
    {
        var chars = new char[Length];
        for (var i = 0; i < Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return Format(new string(chars))!;
    }

    /// <summary>
    /// The code as typed, made canonical: without spaces and dashes, in upper case, with O read as 0 and I or L as 1;
    /// null when it isn't a code.
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (text is null) return null;
        var code = new StringBuilder(Length);
        foreach (var c in text.ToUpperInvariant())
        {
            if (c is ' ' or '-') continue;
            var digit = c switch { 'O' => '0', 'I' or 'L' => '1', _ => c };
            if (!Alphabet.Contains(digit)) return null;
            code.Append(digit);
        }
        return code.Length == Length ? code.ToString() : null;
    }

    /// <summary>The code in groups of four, e.g. "7KQ2-M9XD-4FTB-R1WE"; null when it isn't a code.</summary>
    public static string? Format(string? text) =>
        Normalize(text) is { } code ? string.Join('-', Enumerable.Range(0, Length / 4).Select(i => code.Substring(i * 4, 4))) : null;

    /// <summary>The key of the announcements PCs with this code send each other (<see cref="Announcement"/>).</summary>
    public static byte[] DiscoveryKey(string code) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Key(code), 32, salt: [], info: "UpsTools discovery v1"u8.ToArray());

    /// <summary>
    /// Tells announcements of PCs with this code from those with another, without giving the code away: 8 bytes of a
    /// keyed hash, so trying codes against it is as hard as against the links.
    /// </summary>
    public static byte[] Fingerprint(string code) => HMACSHA256.HashData(Key(code), "UpsTools fingerprint"u8)[..8];

    /// <summary>The 256-bit key both ends derive from the code.</summary>
    public static byte[] Key(string code)
    {
        var normalized = Normalize(code) ?? throw new ArgumentException("Not a sharing code", nameof(code));
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.ASCII.GetBytes(normalized), 32,
            salt: "UpsTools sharing code"u8.ToArray(), info: []);
    }
}
