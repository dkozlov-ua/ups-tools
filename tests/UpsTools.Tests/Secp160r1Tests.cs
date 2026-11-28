using System.Numerics;
using System.Security.Cryptography;
using UpsTools.Core.EcoFlow;

namespace UpsTools.Tests;

/// <summary>
/// The managed secp160r1 (used where the platform lacks the curve, i.e. on Android) against Windows CNG, which talks to
/// real EcoFlow devices.
/// </summary>
public class Secp160r1Tests
{
    private static readonly ECCurve Curve = ECCurve.CreateFromFriendlyName("secP160r1");

    [Fact]
    public void The_generator_is_on_the_curve_and_of_order_n()
    {
        Assert.True(Secp160r1.OnCurve(Secp160r1.G.X, Secp160r1.G.Y));
        Assert.Null(Secp160r1.Multiply(Secp160r1.N, Secp160r1.G));
        Assert.NotNull(Secp160r1.Multiply(Secp160r1.N - 1, Secp160r1.G));
    }

    [Fact]
    public void Its_public_key_is_the_one_cng_makes_of_the_same_private_key()
    {
        using var cng = ECDiffieHellman.Create(Curve);
        var parameters = cng.ExportParameters(includePrivateParameters: true);
        var managed = new Secp160r1(new BigInteger(parameters.D!, true, true));

        Assert.Equal(EcoFlowCrypto.PublicPoint(parameters.Q.X!, parameters.Q.Y!), managed.PublicKey);
    }

    [Fact]
    public void It_agrees_on_the_same_secret_as_cng_either_way()
    {
        for (var i = 0; i < 8; i++)
        {
            using var cng = ECDiffieHellman.Create(Curve);
            var cngPublic = cng.ExportParameters(false).Q;
            var cngPoint = EcoFlowCrypto.PublicPoint(cngPublic.X!, cngPublic.Y!);
            var managed = new Secp160r1();

            using var managedAsCng = ECDiffieHellman.Create(new ECParameters
            {
                Curve = Curve,
                Q = new ECPoint { X = managed.PublicKey[..20], Y = managed.PublicKey[20..] },
            });
            var fromCng = cng.DeriveRawSecretAgreement(managedAsCng.PublicKey);
            var fromManaged = managed.DeriveSecret(cngPoint[..20], cngPoint[20..]);

            Assert.Equal(Convert.ToHexString(fromCng), Convert.ToHexString(fromManaged));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_key_exchange_gives_the_key_a_device_with_cng_derives(bool managed)
    {
        using var crypto = new EcoFlowCrypto(managed);
        var hello = crypto.StartKeyExchange();

        // The device's side, as FakeEcoFlowDevice does it.
        using var device = ECDiffieHellman.Create(Curve);
        using var client = ECDiffieHellman.Create(new ECParameters
        {
            Curve = Curve,
            Q = new ECPoint { X = hello[2..22], Y = hello[22..42] },
        });
        var shared = device.DeriveRawSecretAgreement(client.PublicKey);
        var q = device.ExportParameters(false).Q;
        crypto.HandlePublicKey([0x01, 0x00, 0x00, .. EcoFlowCrypto.PublicPoint(q.X!, q.Y!)]);

        using var aes = Aes.Create();
        aes.Key = shared[..16];
        var plain = "the temporary key works"u8.ToArray();
        Assert.Equal(aes.EncryptCbc(plain, MD5.HashData(shared), PaddingMode.PKCS7), crypto.Encrypt(plain));
    }

    [Fact]
    public void A_point_off_the_curve_is_refused()
    {
        var key = new Secp160r1().PublicKey;
        key[^1] ^= 1;
        Assert.Throws<CryptographicException>(() => new Secp160r1().DeriveSecret(key[..20], key[20..]));
    }
}
