using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace UpsTools.Core.EcoFlow;

/// <summary>
/// Elliptic-curve Diffie-Hellman on secp160r1 (SEC 2), in managed code, for the EcoFlow key exchange where the platform
/// doesn't offer that curve (Android's providers have only the NIST curves). Windows uses CNG instead.
/// It isn't constant-time: it only ever handles one ephemeral key per Bluetooth session, which the device's own
/// 160-bit curve already limits far more.
/// </summary>
internal sealed class Secp160r1
{
    /// <summary>The length of a coordinate: 20 bytes, big-endian.</summary>
    public const int CoordinateLength = 20;

    internal static readonly BigInteger P = Hex("00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF7FFFFFFF");
    internal static readonly BigInteger A = P - 3;
    internal static readonly BigInteger B = Hex("001C97BEFC54BD7A8B65ACF89F81D4D4ADC565FA45");
    internal static readonly BigInteger N = Hex("0100000000000000000001F4C8F927AED3CA752257");
    internal static readonly (BigInteger X, BigInteger Y) G =
        (Hex("004A96B5688EF573284664698968C38BB913CBFC82"), Hex("0023A628553168947D59DCC912042351377AC5FB32"));

    private readonly BigInteger _d;

    /// <summary>A new key pair.</summary>
    public Secp160r1() : this(RandomScalar())
    {
    }

    /// <param name="d">The private key, 1 ≤ d &lt; n.</param>
    internal Secp160r1(BigInteger d)
    {
        if (d <= 0 || d >= N) throw new ArgumentOutOfRangeException(nameof(d));
        _d = d;
        var q = Multiply(d, G) ?? throw new CryptographicException("Bad secp160r1 private key");
        PublicKey = [.. Bytes(q.X, CoordinateLength), .. Bytes(q.Y, CoordinateLength)];
    }

    /// <summary>The public key as X‖Y, 20 bytes each.</summary>
    public byte[] PublicKey { get; }

    /// <summary>
    /// The shared secret with the peer's public key (X, Y; 20 bytes each): the shared point's X, 20 bytes, as CNG's
    /// raw secret agreement has it.
    /// </summary>
    public byte[] DeriveSecret(byte[] x, byte[] y)
    {
        var peer = (X: new BigInteger(x, true, true), Y: new BigInteger(y, true, true));
        if (!OnCurve(peer.X, peer.Y)) throw new CryptographicException("The peer's public key is not on secp160r1");
        var shared = Multiply(_d, peer) ?? throw new CryptographicException("The shared point is at infinity");
        return Bytes(shared.X, CoordinateLength);
    }

    internal static bool OnCurve(BigInteger x, BigInteger y)
    {
        if (x < 0 || y < 0 || x >= P || y >= P) return false;
        return Mod(y * y - (x * x * x + A * x + B)) == 0;
    }

    /// <summary>k·point, by double-and-add; null is the point at infinity.</summary>
    internal static (BigInteger X, BigInteger Y)? Multiply(BigInteger k, (BigInteger X, BigInteger Y) point)
    {
        (BigInteger X, BigInteger Y)? result = null;
        (BigInteger X, BigInteger Y)? addend = point;
        for (var bits = k; bits > 0; bits >>= 1)
        {
            if (!bits.IsEven) result = Add(result, addend);
            addend = Add(addend, addend);
        }
        return result;
    }

    internal static (BigInteger X, BigInteger Y)? Add((BigInteger X, BigInteger Y)? p, (BigInteger X, BigInteger Y)? q)
    {
        if (p is not { } a) return q;
        if (q is not { } b) return p;

        BigInteger slope;
        if (a.X == b.X)
        {
            if (Mod(a.Y + b.Y) == 0) return null; // a = −b (or a doubled with y = 0)
            slope = Mod((3 * a.X * a.X + A) * Inverse(2 * a.Y));
        }
        else
        {
            slope = Mod((b.Y - a.Y) * Inverse(b.X - a.X));
        }
        var x = Mod(slope * slope - a.X - b.X);
        return (x, Mod(slope * (a.X - x) - a.Y));
    }

    private static BigInteger Mod(BigInteger value)
    {
        var r = value % P;
        return r.Sign < 0 ? r + P : r;
    }

    private static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);

    private static BigInteger RandomScalar()
    {
        Span<byte> bytes = stackalloc byte[N.GetByteCount(isUnsigned: true)];
        while (true)
        {
            RandomNumberGenerator.Fill(bytes);
            var d = new BigInteger(bytes, true, true) % N;
            if (d > 0) return d;
        }
    }

    /// <summary>The value big-endian, left-padded with zeros to <paramref name="length"/> bytes.</summary>
    internal static byte[] Bytes(BigInteger value, int length)
    {
        var raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length > length) throw new CryptographicException("Value too long");
        var padded = new byte[length];
        raw.CopyTo(padded, length - raw.Length);
        return padded;
    }

    private static BigInteger Hex(string hex) => BigInteger.Parse(hex, NumberStyles.HexNumber);
}
