using System.Buffers.Binary;
using System.Security.Cryptography;

namespace UpsTools.Core.Bluetti;

/// <summary>
/// Bluetti "V2" BLE channel encryption, ported from the community reverse-engineering in
/// https://github.com/Patrick762/bluetti-bt-lib (bluetooth/encryption.py, MIT).
///
/// Handshake (device-initiated once notifications are enabled):
/// 1. Device → "**" challenge with a 4-byte seed. Both sides derive an IV = MD5(reversed seed) and
///    a temporary AES-128 key = IV XOR a static key. We answer with IV[8..12].
/// 2. Device → (AES-CBC, temporary key/IV) its P-256 public key, signed with Bluetti's key K2.
/// 3. We → our ephemeral P-256 public key, signed with the app key L1, encrypted the same way.
/// 4. Device → "accepted". The ECDH shared secret becomes the AES-256 key for the rest of the session;
///    each later message carries a 4-byte seed whose MD5 is its IV.
///
/// The static keys were extracted from the official app by the community; they are the same for every unit.
/// </summary>
internal sealed class BluettiCrypto : IDisposable
{
    private const int AesBlock = 16;

    private static readonly byte[] StaticAesKey = Convert.FromHexString("459FC535808941F17091E0993EE3E93D");

    // App signing key "L1" (private scalar and its public point).
    internal static readonly byte[] L1D = Convert.FromHexString("4F19A16E3E87BDD9BD24D3E5495B88041511943CBC8B969ADE9641D0F56AF337");
    internal static readonly byte[] L1Q = Convert.FromHexString(
        "3f29e1b8b29d8422bbb0e0f09000cf2ee2931da13cdac8129f8c09deac07f519" +
        "b5ac5509ca3f3f59b234b7777a231ae595ca5aa1745fd7a62bc4d02037117dd1");

    // Device signing key "K2" (public point only).
    internal static readonly byte[] K2Q = Convert.FromHexString(
        "a73abf5d2232c8c1c72e68304343c272495e3a8fd6f30ea96de2f4b3ce60b251" +
        "ee21ac667cf8a71e18b46b664eaeffe3c489f24f695b6411db7e22ccc85a8594");

    public enum KexType : byte
    {
        Challenge = 1,
        ChallengeAccepted = 3,
        PeerPublicKey = 4,
        PublicKeyAccepted = 6,
    }

    private byte[]? _tempKey;
    private byte[]? _tempIv;
    private byte[]? _sessionKey;
    private ECDiffieHellman? _ephemeral;
    private ECDiffieHellman? _peer;

    public bool HasTemporaryKey => _tempKey is not null;
    public bool IsEstablished => _sessionKey is not null;

    // ---- framing of key-exchange messages: "**" | type | length | data | sum16(type..data) ----

    public static bool IsKexFrame(ReadOnlySpan<byte> frame) => frame.Length >= 6 && frame[0] == (byte)'*' && frame[1] == (byte)'*';

    public static KexType GetKexType(ReadOnlySpan<byte> frame) => (KexType)frame[2];

    private static ReadOnlySpan<byte> KexData(ReadOnlySpan<byte> frame) => frame[4..^2];

    internal static byte[] BuildKexFrame(ReadOnlySpan<byte> body)
    {
        var frame = new byte[body.Length + 4];
        frame[0] = frame[1] = (byte)'*';
        body.CopyTo(frame.AsSpan(2));
        var sum = 0;
        foreach (var b in body) sum += b;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(frame.Length - 2), (ushort)sum);
        return frame;
    }

    // ---- handshake steps ----

    /// <summary>Step 1: returns the plain-text frame to send back.</summary>
    public byte[] HandleChallenge(ReadOnlySpan<byte> frame)
    {
        var seed = KexData(frame);
        if (seed.Length != 4) throw new CryptographicException($"Unexpected challenge length {seed.Length}");

        Span<byte> reversed = stackalloc byte[4];
        seed.CopyTo(reversed);
        reversed.Reverse();

        _tempIv = MD5.HashData(reversed);
        _tempKey = new byte[16];
        for (var i = 0; i < 16; i++) _tempKey[i] = (byte)(_tempIv[i] ^ StaticAesKey[i]);
        _sessionKey = null;

        return BuildKexFrame([0x02, 0x04, .. _tempIv.AsSpan(8, 4)]);
    }

    /// <summary>Steps 2–3: verify the device's key and return our encrypted answer.</summary>
    public byte[] HandlePeerPublicKey(ReadOnlySpan<byte> decryptedFrame)
    {
        if (_tempIv is null || _tempKey is null) throw new InvalidOperationException("Challenge not handled yet");

        var data = KexData(decryptedFrame);
        if (data.Length != 128) throw new CryptographicException($"Unexpected peer key message length {data.Length}");
        var peerPoint = data[..64];
        var signature = data[64..];

        using (var k2 = ECDsa.Create(PublicParameters(K2Q)))
        {
            if (!k2.VerifyData([.. peerPoint, .. _tempIv], signature, HashAlgorithmName.SHA256))
                throw new CryptographicException("Device public key signature is invalid");
        }

        _peer?.Dispose();
        _peer = ECDiffieHellman.Create(PublicParameters(peerPoint));

        _ephemeral?.Dispose();
        _ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = _ephemeral.ExportParameters(false).Q;
        byte[] myPoint = [.. q.X!, .. q.Y!];

        using var l1 = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = L1D,
            Q = new ECPoint { X = L1Q[..32], Y = L1Q[32..] },
        });
        // .NET signs in IEEE P1363 (r || s) by default, the raw format the device expects.
        var mySignature = l1.SignData([.. myPoint, .. _tempIv], HashAlgorithmName.SHA256);

        var frame = BuildKexFrame([0x05, 0x80, .. myPoint, .. mySignature]);
        return EncryptFixedIv(frame, _tempKey, _tempIv);
    }

    /// <summary>Step 4: derive the session key.</summary>
    public void HandlePublicKeyAccepted(ReadOnlySpan<byte> decryptedFrame)
    {
        var data = KexData(decryptedFrame);
        if (data.Length != 1 || data[0] != 0) throw new CryptographicException("Device rejected our public key");
        if (_ephemeral is null || _peer is null) throw new InvalidOperationException("Peer key not handled yet");
        _sessionKey = _ephemeral.DeriveRawSecretAgreement(_peer.PublicKey);
    }

    public void Reset()
    {
        _tempKey = _tempIv = _sessionKey = null;
        _ephemeral?.Dispose();
        _peer?.Dispose();
        _ephemeral = _peer = null;
    }

    // ---- message encryption: len16 | [seed32] | AES-CBC(zero-padded) ----

    public byte[] Encrypt(ReadOnlySpan<byte> plain)
    {
        if (_sessionKey is null) throw new InvalidOperationException("Session key not established");
        return EncryptWithSeed(plain, _sessionKey, RandomNumberGenerator.GetBytes(4));
    }

    public byte[] Decrypt(ReadOnlySpan<byte> message)
    {
        if (_sessionKey is not null) return DecryptWithSeed(message, _sessionKey);
        if (_tempKey is null || _tempIv is null) throw new InvalidOperationException("No key yet");
        return DecryptFixedIv(message, _tempKey, _tempIv);
    }

    /// <summary>Total length of the encrypted message starting at the buffer, once its header has arrived.</summary>
    public int? ExpectedEncryptedLength(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 2) return null;
        var plainLength = BinaryPrimitives.ReadUInt16BigEndian(buffer);
        var header = _sessionKey is null ? 2 : 6;
        return header + RoundUp(plainLength);
    }

    internal static byte[] EncryptFixedIv(ReadOnlySpan<byte> plain, byte[] key, byte[] iv)
    {
        var cipher = AesCbc(key, iv, plain, encrypt: true);
        var message = new byte[2 + cipher.Length];
        BinaryPrimitives.WriteUInt16BigEndian(message, (ushort)plain.Length);
        cipher.CopyTo(message, 2);
        return message;
    }

    internal static byte[] EncryptWithSeed(ReadOnlySpan<byte> plain, byte[] key, byte[] seed)
    {
        var cipher = AesCbc(key, MD5.HashData(seed), plain, encrypt: true);
        var message = new byte[6 + cipher.Length];
        BinaryPrimitives.WriteUInt16BigEndian(message, (ushort)plain.Length);
        seed.CopyTo(message, 2);
        cipher.CopyTo(message, 6);
        return message;
    }

    internal static byte[] DecryptFixedIv(ReadOnlySpan<byte> message, byte[] key, byte[] iv)
    {
        var length = BinaryPrimitives.ReadUInt16BigEndian(message);
        return AesCbc(key, iv, message[2..], encrypt: false).AsSpan(0, length).ToArray();
    }

    internal static byte[] DecryptWithSeed(ReadOnlySpan<byte> message, byte[] key)
    {
        var length = BinaryPrimitives.ReadUInt16BigEndian(message);
        var iv = MD5.HashData(message.Slice(2, 4));
        return AesCbc(key, iv, message[6..], encrypt: false).AsSpan(0, length).ToArray();
    }

    private static byte[] AesCbc(byte[] key, byte[] iv, ReadOnlySpan<byte> data, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        if (encrypt)
        {
            var padded = new byte[RoundUp(data.Length)];
            data.CopyTo(padded);
            return aes.EncryptCbc(padded, iv, PaddingMode.None);
        }
        if (data.Length % AesBlock != 0) throw new CryptographicException("Ciphertext is not block aligned");
        return aes.DecryptCbc(data, iv, PaddingMode.None);
    }

    private static int RoundUp(int length) => (length + AesBlock - 1) / AesBlock * AesBlock;

    private static ECParameters PublicParameters(ReadOnlySpan<byte> point) => new()
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint { X = point[..32].ToArray(), Y = point[32..].ToArray() },
    };

    public void Dispose() => Reset();
}
