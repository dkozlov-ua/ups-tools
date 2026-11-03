using System.Security.Cryptography;
using UpsTools.Core.Bluetti;

namespace UpsTools.Tests;

/// <summary>Vectors generated with the Python reference implementation (bluetti-bt-lib 0.2.0b10).</summary>
public class BluettiCryptoTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    private static readonly byte[] TempIv = Hex("56b80d4da723052405a25513d238e70a");
    private static readonly byte[] TempKey = Hex("1327c87827aa44d57533b58aecdb0e37");
    private static readonly byte[] FixedPlain = Hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f2021222324");
    private static readonly byte[] FixedCipher = Hex("0025afd9c4bd9ff7e13d117a9b48bfa7421ef895d4c0aa3346790084d22c33f00a30130e6b9a3706567af3ca8356c2d898a6");

    [Fact]
    public void Challenge_response_matches_reference()
    {
        using var crypto = new BluettiCrypto();
        var challenge = Hex("2a2a0104a1b2c3d402ef");

        Assert.True(BluettiCrypto.IsKexFrame(challenge));
        Assert.Equal(BluettiCrypto.KexType.Challenge, BluettiCrypto.GetKexType(challenge));
        Assert.Equal(Hex("2a2a020405a255130115"), crypto.HandleChallenge(challenge));
        Assert.True(crypto.HasTemporaryKey);
        Assert.False(crypto.IsEstablished);
    }

    [Fact]
    public void Temporary_key_from_challenge_decrypts_reference_message()
    {
        using var crypto = new BluettiCrypto();
        crypto.HandleChallenge(Hex("2a2a0104a1b2c3d402ef"));

        Assert.Equal(FixedCipher.Length, crypto.ExpectedEncryptedLength(FixedCipher));
        Assert.Equal(FixedPlain, crypto.Decrypt(FixedCipher));
    }

    [Fact]
    public void Fixed_iv_encryption_matches_reference()
    {
        Assert.Equal(FixedCipher, BluettiCrypto.EncryptFixedIv(FixedPlain, TempKey, TempIv));
    }

    [Fact]
    public void Seeded_encryption_matches_reference_and_round_trips()
    {
        var key = Hex("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");
        var plain = Hex("01030066001225d8");
        var expected = Hex("0008deadbeef9c78321863f5801b5e5ac219f0895941");

        var cipher = BluettiCrypto.EncryptWithSeed(plain, key, Hex("deadbeef"));

        Assert.Equal(expected, cipher);
        Assert.Equal(plain, BluettiCrypto.DecryptWithSeed(cipher, key));
    }

    [Fact]
    public void App_signing_key_pair_is_consistent()
    {
        var curve = ECCurve.NamedCurves.nistP256;
        using var signer = ECDsa.Create(new ECParameters
        {
            Curve = curve,
            D = BluettiCrypto.L1D,
            Q = new ECPoint { X = BluettiCrypto.L1Q[..32], Y = BluettiCrypto.L1Q[32..] },
        });
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = curve,
            Q = new ECPoint { X = BluettiCrypto.L1Q[..32], Y = BluettiCrypto.L1Q[32..] },
        });
        var data = RandomNumberGenerator.GetBytes(80);

        var signature = signer.SignData(data, HashAlgorithmName.SHA256);

        Assert.Equal(64, signature.Length);
        Assert.True(verifier.VerifyData(data, signature, HashAlgorithmName.SHA256));
    }

    [Fact]
    public void Kex_frame_checksum_is_big_endian_byte_sum()
    {
        var frame = BluettiCrypto.BuildKexFrame([0x01, 0x04, 0xa1, 0xb2, 0xc3, 0xd4]);
        Assert.Equal(Hex("2a2a0104a1b2c3d402ef"), frame);
    }

    [Fact]
    public void Peer_key_with_bad_signature_is_rejected()
    {
        using var crypto = new BluettiCrypto();
        crypto.HandleChallenge(Hex("2a2a0104a1b2c3d402ef"));

        using var someKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = someKey.ExportParameters(false).Q;
        var frame = BluettiCrypto.BuildKexFrame([0x04, 0x80, .. q.X!, .. q.Y!, .. new byte[64]]);

        Assert.ThrowsAny<CryptographicException>(() => crypto.HandlePeerPublicKey(frame));
    }
}
