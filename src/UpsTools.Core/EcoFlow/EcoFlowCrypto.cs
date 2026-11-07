using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace UpsTools.Core.EcoFlow;

/// <summary>
/// The encryption of an EcoFlow Bluetooth session ("encrypt type 7"):
/// <list type="number">
/// <item>ECDH on secp160r1: we send our public key, the device answers with its own; the first 16 bytes of the shared
/// secret are a temporary AES key, and its MD5 the IV for the whole session.</item>
/// <item>The device sends a random value and a seed, encrypted with the temporary key; the session key is the MD5 of
/// 16 bytes picked from <see cref="EcoFlowKeyData"/> by the seed, followed by the random value.</item>
/// <item>Every packet then travels AES-128-CBC encrypted with the session key, PKCS#7 padded.</item>
/// </list>
/// Ported from ha-ef-ble (Apache-2.0).
/// </summary>
internal sealed class EcoFlowCrypto : IDisposable
{
    /// <summary>secp160r1 coordinates are 20 bytes, big-endian.</summary>
    private const int CoordinateLength = 20;

    private static readonly ECCurve Curve = ECCurve.CreateFromFriendlyName("secP160r1");

    // secp160r1: y² = x³ + ax + b (mod p), a = p − 3
    private static readonly BigInteger P = BigInteger.Parse("00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF7FFFFFFF", NumberStyles.HexNumber);
    private static readonly BigInteger B = BigInteger.Parse("001C97BEFC54BD7A8B65ACF89F81D4D4ADC565FA45", NumberStyles.HexNumber);

    private ECDiffieHellman? _ephemeral;
    private byte[]? _key;
    private byte[]? _iv;

    /// <summary>The session key is in place: packets can be sent and read.</summary>
    public bool HasSessionKey { get; private set; }

    /// <summary>Starts a handshake: the payload of the first command frame, 0x01 0x00 and our public key (X‖Y).</summary>
    public byte[] StartKeyExchange()
    {
        Reset();
        _ephemeral = ECDiffieHellman.Create(Curve);
        var q = _ephemeral.ExportParameters(false).Q;
        return [0x01, 0x00, .. PublicPoint(q.X!, q.Y!)];
    }

    /// <summary>Takes the device's public key reply (0x01, status, curve, X‖Y) and sets the temporary key.</summary>
    public void HandlePublicKey(byte[] reply)
    {
        if (_ephemeral is null) throw new InvalidOperationException("No key exchange in progress");
        if (reply.Length < 3 + 2 * CoordinateLength || reply[0] != 0x01)
            throw new IOException($"Unexpected public key reply: {Convert.ToHexString(reply)}");
        if (reply[2] != 0) throw new IOException($"The UPS uses an unsupported key type ({reply[2]})");

        var x = reply[3..(3 + CoordinateLength)];
        var y = reply[(3 + CoordinateLength)..(3 + 2 * CoordinateLength)];
        if (!OnCurve(x, y)) throw new IOException("The UPS's public key is not on the curve");
        using var peer = ECDiffieHellman.Create(new ECParameters { Curve = Curve, Q = new ECPoint { X = x, Y = y } });
        var shared = _ephemeral.DeriveRawSecretAgreement(peer.PublicKey);
        _key = shared[..16];
        _iv = MD5.HashData(shared);
    }

    /// <summary>Takes the device's key info reply (0x02, then the encrypted random value and seed) and sets the session key.</summary>
    public void HandleKeyInfo(byte[] reply)
    {
        if (_key is null) throw new InvalidOperationException("No temporary key yet");
        if (reply.Length < 33 || reply[0] != 0x02) throw new IOException($"Unexpected key info reply: {Convert.ToHexString(reply)}");

        var info = Decrypt(reply[1..]);
        if (info.Length < 18) throw new IOException($"Key info too short ({info.Length} bytes)");
        _key = SessionKey(info.AsSpan(16, 2), info.AsSpan(0, 16));
        HasSessionKey = true;
    }

    /// <summary>MD5 of the 16 bytes of <see cref="EcoFlowKeyData"/> the seed points at, followed by the random value.</summary>
    internal static byte[] SessionKey(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> random)
    {
        var position = seed[0] * 0x10 + ((seed[1] - 1) & 0xFF) * 0x100;
        var table = EcoFlowKeyData.Table;
        if (position + 16 > table.Length) throw new IOException($"Key seed {Convert.ToHexString(seed)} is out of range");
        return MD5.HashData([.. table.AsSpan(position, 16), .. random[..16]]);
    }

    public byte[] Encrypt(byte[] plain)
    {
        using var aes = CreateAes();
        return aes.EncryptCbc(plain, _iv!, PaddingMode.PKCS7);
    }

    /// <summary>Decrypts whole blocks (trailing bytes are dropped, as the devices do) and removes valid PKCS#7 padding.</summary>
    public byte[] Decrypt(ReadOnlySpan<byte> encrypted)
    {
        var aligned = encrypted.Length - encrypted.Length % 16;
        if (aligned == 0) return encrypted.ToArray();
        using var aes = CreateAes();
        var plain = aes.DecryptCbc(encrypted[..aligned], _iv!, PaddingMode.None);
        var pad = plain[^1];
        return pad is >= 1 and <= 16 && plain.AsSpan(plain.Length - pad).IndexOfAnyExcept(pad) < 0 ? plain[..^pad] : plain;
    }

    private Aes CreateAes()
    {
        if (_key is null || _iv is null) throw new InvalidOperationException("No key yet");
        var aes = Aes.Create();
        aes.Key = _key;
        return aes;
    }

    /// <summary>
    /// Our public key as 20-byte X‖Y. Windows exports secp160r1 coordinates as 21 bytes (the length of the curve's
    /// order), the value first and the padding last; the window that is on the curve is the point.
    /// </summary>
    internal static byte[] PublicPoint(byte[] x, byte[] y)
    {
        if (x.Length == CoordinateLength && y.Length == CoordinateLength) return [.. x, .. y];
        foreach (var start in new[] { 0, x.Length - CoordinateLength })
        {
            var (px, py) = (x[start..(start + CoordinateLength)], y[start..(start + CoordinateLength)]);
            if (OnCurve(px, py)) return [.. px, .. py];
        }
        throw new CryptographicException("Could not read our own secp160r1 public key");
    }

    internal static bool OnCurve(byte[] x, byte[] y)
    {
        var (bx, by) = (new BigInteger(x, true, true), new BigInteger(y, true, true));
        if (bx >= P || by >= P) return false;
        var right = (BigInteger.ModPow(bx, 3, P) + (P - 3) * bx + B) % P;
        return BigInteger.ModPow(by, 2, P) == right;
    }

    public void Reset()
    {
        _ephemeral?.Dispose();
        _ephemeral = null;
        _key = null;
        _iv = null;
        HasSessionKey = false;
    }

    public void Dispose() => Reset();
}

/// <summary>The table EcoFlow derives session keys from (from ha-ef-ble, which took it from EcoFlow's app).</summary>
internal static class EcoFlowKeyData
{
    private static readonly Lazy<byte[]> Data = new(() =>
    {
        using var stream = typeof(EcoFlowKeyData).Assembly.GetManifestResourceStream("UpsTools.Core.EcoFlow.EcoFlowKeyData.bin")
                           ?? throw new InvalidOperationException("The EcoFlow key table is missing");
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        return data;
    });

    public static byte[] Table => Data.Value;
}
