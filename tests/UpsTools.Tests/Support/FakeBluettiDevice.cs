using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using UpsTools.Core.Bluetti;

namespace UpsTools.Tests.Support;

/// <summary>
/// The device side of the Bluetti protocol: the "V2" encryption handshake (with a test key in place of
/// Bluetti's K2) and Modbus reads of <see cref="Registers"/>, answered in notifications of at most
/// <see cref="Mtu"/> bytes. Its properties inject the faults seen on real devices.
/// </summary>
internal sealed class FakeBluettiDevice
{
    /// <summary>Stands in for Bluetti's device signing key K2.</summary>
    private static readonly ECDsa Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>The public half of <see cref="Signer"/>: what the connection must trust.</summary>
    public static byte[] SigningKey { get; } = PublicPoint(Signer.ExportParameters(false));

    private readonly ConcurrentQueue<string> _log = new();

    /// <summary>Registers by address; ones not listed read as 0.</summary>
    public Dictionary<ushort, ushort> Registers { get; } = [];

    /// <summary>Starts the encryption handshake when notifications are turned on.</summary>
    public bool Encrypted { get; set; } = true;

    /// <summary>Largest notification (20 bytes on a minimal BLE link), except for plain handshake frames.</summary>
    public int Mtu { get; set; } = 20;

    /// <summary>This many subscriptions bring no challenge (Windows still held the previous link).</summary>
    public int ChallengesToSkip { get; set; }

    /// <summary>This many handshakes stall: the device never accepts our key.</summary>
    public int HandshakesToStall { get; set; }

    /// <summary>This many connection attempts fail.</summary>
    public int ConnectFailures { get; set; }

    /// <summary>Signs its key with another key than the one the connection trusts.</summary>
    public bool BadSignature { get; set; }

    /// <summary>Rejects our public key.</summary>
    public bool RejectKey { get; set; }

    /// <summary>Writes fail.</summary>
    public bool WritesFail { get; set; }

    /// <summary>Answers a read of (start, count) with this plain frame instead; null keeps it silent.</summary>
    public Func<ushort, ushort, byte[]?>? Answer { get; set; }

    public int Connections { get; private set; }
    public List<Link> Links { get; } = [];
    public Link? Current => Links.LastOrDefault();

    /// <summary>The reads it answered: (start, count, whether the request was encrypted).</summary>
    public ConcurrentQueue<(ushort Start, ushort Count, bool Encrypted)> Reads { get; } = new();

    public IReadOnlyCollection<string> Log => _log;

    public BluettiLinkFactory Factory => _ =>
    {
        Connections++;
        if (ConnectFailures > 0)
        {
            ConnectFailures--;
            throw new IOException("Bluetooth device not found");
        }
        var link = new Link(this);
        Links.Add(link);
        return Task.FromResult<IBluettiLink>(link);
    };

    /// <summary>Loads register blocks as read from a device (big-endian, 2 bytes per register).</summary>
    public void Load(IReadOnlyDictionary<RegisterBlock, byte[]> blocks)
    {
        foreach (var (block, data) in blocks)
            for (var i = 0; i < block.Count; i++)
                Registers[(ushort)(block.Start + i)] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i * 2));
    }

    public static byte[] ReadResponse(byte[] payload)
    {
        byte[] frame = [0x01, 0x03, (byte)payload.Length, .. payload, 0, 0];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(^2), Modbus.Crc16(frame.AsSpan(0, frame.Length - 2)));
        return frame;
    }

    private static byte[] PublicPoint(ECParameters p) => [.. p.Q.X!, .. p.Q.Y!];

    public sealed class Link(FakeBluettiDevice device) : IBluettiLink
    {
        private byte[]? _tempKey;
        private byte[]? _tempIv;
        private byte[]? _sessionKey;
        private ECDiffieHellman? _ecdh;

        public event Action<byte[]>? Received;
        public event Action? Disconnected;

        public bool Subscribed { get; private set; }
        public int Subscriptions { get; private set; }
        public bool Disposed { get; private set; }
        public bool SessionEstablished => _sessionKey is not null;

        public Task SetNotificationsAsync(bool on, CancellationToken ct)
        {
            if (Disposed) throw new ObjectDisposedException(nameof(Link));
            device._log.Enqueue(on ? "subscribe" : "unsubscribe");
            Subscribed = on;
            if (!on) return Task.CompletedTask;

            Subscriptions++;
            if (!device.Encrypted) return Task.CompletedTask;
            if (device.ChallengesToSkip > 0)
            {
                device.ChallengesToSkip--;
                return Task.CompletedTask;
            }
            SendChallenge();
            return Task.CompletedTask;
        }

        public Task WriteAsync(byte[] data, CancellationToken ct)
        {
            if (Disposed) throw new ObjectDisposedException(nameof(Link));
            if (device.WritesFail) throw new IOException("Bluetooth write failed (Unreachable)");
            Handle(data);
            return Task.CompletedTask;
        }

        /// <summary>The device drops the link.</summary>
        public void Disconnect() => Disconnected?.Invoke();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _ecdh?.Dispose();
            return ValueTask.CompletedTask;
        }

        private void SendChallenge()
        {
            var seed = RandomNumberGenerator.GetBytes(4);
            var reversed = seed.Reverse().ToArray();
            _tempIv = MD5.HashData(reversed);
            _tempKey = new byte[16];
            for (var i = 0; i < 16; i++) _tempKey[i] = (byte)(_tempIv[i] ^ BluettiCrypto.StaticAesKey[i]);
            _sessionKey = null;
            device._log.Enqueue("challenge");
            SendWhole(BluettiCrypto.BuildKexFrame([0x01, 0x04, .. seed]));
        }

        private void Handle(byte[] data)
        {
            if (BluettiCrypto.IsKexFrame(data))
            {
                // Our answer to the challenge: IV[8..12].
                if (BluettiCrypto.GetKexType(data) != (BluettiCrypto.KexType)2 || _tempIv is null) return;
                if (!data.AsSpan(4, 4).SequenceEqual(_tempIv.AsSpan(8, 4)))
                {
                    device._log.Enqueue("wrong challenge answer");
                    return;
                }
                SendWhole(BluettiCrypto.BuildKexFrame([0x03, 0x01, 0x00]));
                SendPublicKey();
                return;
            }

            if (_tempKey is not null && _sessionKey is null)
            {
                OnPeerKey(BluettiCrypto.DecryptFixedIv(data, _tempKey, _tempIv!));
                return;
            }

            var request = _sessionKey is null ? data : BluettiCrypto.DecryptWithSeed(data, _sessionKey);
            var start = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(2));
            var count = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(4));
            if (BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(6)) != Modbus.Crc16(request.AsSpan(0, 6)))
            {
                device._log.Enqueue("bad request CRC");
                return;
            }
            device.Reads.Enqueue((start, count, _sessionKey is not null));

            byte[]? frame;
            if (device.Answer is { } answer)
            {
                frame = answer(start, count);
            }
            else
            {
                var payload = new byte[count * 2];
                for (var i = 0; i < count; i++)
                    BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(i * 2),
                        device.Registers.GetValueOrDefault((ushort)(start + i)));
                frame = ReadResponse(payload);
            }
            if (frame is null) return;
            Send(_sessionKey is null ? frame : BluettiCrypto.EncryptWithSeed(frame, _sessionKey, RandomNumberGenerator.GetBytes(4)));
        }

        private void SendPublicKey()
        {
            _ecdh?.Dispose();
            _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var point = PublicPoint(_ecdh.ExportParameters(false));
            using var other = device.BadSignature ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
            var signature = (other ?? Signer).SignData([.. point, .. _tempIv!], HashAlgorithmName.SHA256);
            var frame = BluettiCrypto.BuildKexFrame([0x04, 0x80, .. point, .. signature]);
            Send(BluettiCrypto.EncryptFixedIv(frame, _tempKey!, _tempIv!));
        }

        private void OnPeerKey(byte[] frame)
        {
            if (BluettiCrypto.GetKexType(frame) != (BluettiCrypto.KexType)5) return;
            var data = frame.AsSpan(4, frame.Length - 6);
            var point = data[..64].ToArray();
            var signature = data[64..].ToArray();

            using var l1 = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = BluettiCrypto.L1Q[..32], Y = BluettiCrypto.L1Q[32..] },
            });
            var valid = l1.VerifyData((byte[])[.. point, .. _tempIv!], signature, HashAlgorithmName.SHA256);
            device._log.Enqueue(valid ? "app key valid" : "app key INVALID");

            if (device.HandshakesToStall > 0)
            {
                device.HandshakesToStall--;
                device._log.Enqueue("stalled");
                return;
            }

            byte status = valid && !device.RejectKey ? (byte)0 : (byte)1;
            var accepted = BluettiCrypto.BuildKexFrame([0x06, 0x01, status]);
            Send(BluettiCrypto.EncryptFixedIv(accepted, _tempKey!, _tempIv!));
            if (status != 0) return;

            using var peer = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = point[..32], Y = point[32..] },
            });
            _sessionKey = _ecdh!.DeriveRawSecretAgreement(peer.PublicKey);
            device._log.Enqueue("session");
        }

        /// <summary>Plain handshake frames are 7-10 bytes: always one notification on a real link.</summary>
        private void SendWhole(byte[] frame) => Received?.Invoke(frame);

        private void Send(byte[] message)
        {
            for (var at = 0; at < message.Length; at += device.Mtu)
                Received?.Invoke(message.AsSpan(at, Math.Min(device.Mtu, message.Length - at)).ToArray());
        }
    }
}
