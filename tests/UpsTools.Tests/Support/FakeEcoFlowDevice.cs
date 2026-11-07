using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using UpsTools.Core.Ble;
using UpsTools.Core.EcoFlow;

namespace UpsTools.Tests.Support;

/// <summary>
/// The device side of the EcoFlow protocol: the secp160r1 key exchange, the session key, the user ID check, and
/// uploads of its state, sent in notifications of at most <see cref="Mtu"/> bytes. Its properties inject faults.
/// </summary>
internal sealed class FakeEcoFlowDevice
{
    public const string Serial = "R631ZE1AWH5K2080";
    public const string UserId = "1234567890123456789";

    private readonly ConcurrentQueue<string> _log = new();

    /// <summary>Largest notification.</summary>
    public int Mtu { get; set; } = 20;

    /// <summary>What it answers to our user ID: 0 accepts it.</summary>
    public byte AuthResult { get; set; }

    /// <summary>Sends its first upload in place of the answer to our user ID, as some devices do.</summary>
    public bool UploadInsteadOfAuthReply { get; set; }

    /// <summary>Sends each handshake reply this many times, as some devices do.</summary>
    public int HandshakeCopies { get; set; } = 1;

    /// <summary>Doesn't answer the key exchange.</summary>
    public bool Mute { get; set; }

    /// <summary>Asks for the time once authenticated, as River 3 devices do.</summary>
    public bool AsksForTime { get; set; }

    /// <summary>This many connection attempts fail.</summary>
    public int ConnectFailures { get; set; }

    /// <summary>The uploads sent once authenticated: DisplayPropertyUpload payloads.</summary>
    public List<byte[]> Uploads { get; } = [];

    public int Connections { get; private set; }
    public List<Link> Links { get; } = [];
    public Link? Current => Links.LastOrDefault();

    /// <summary>The packets received once the session key was in place.</summary>
    public ConcurrentQueue<EcoFlowPacket> Packets { get; } = new();

    public IReadOnlyCollection<string> Log => _log;

    public BleLinkFactory Factory => _ =>
    {
        Connections++;
        if (ConnectFailures > 0)
        {
            ConnectFailures--;
            throw new IOException("Bluetooth device not found");
        }
        var link = new Link(this);
        Links.Add(link);
        return Task.FromResult<IBleLink>(link);
    };

    public sealed class Link(FakeEcoFlowDevice device) : IBleLink
    {
        private readonly EcoFlowFrameReader _frames = new();
        private byte[]? _key;
        private byte[]? _iv;
        private bool _sessionKey;
        private byte _seq = 0x75;

        public event Action<byte[]>? Received;
        public event Action? Disconnected;

        public bool Subscribed { get; private set; }
        public bool Disposed { get; private set; }
        public bool Authenticated { get; private set; }

        public Task SetNotificationsAsync(bool on, CancellationToken ct)
        {
            if (Disposed) throw new ObjectDisposedException(nameof(Link));
            device._log.Enqueue(on ? "subscribe" : "unsubscribe");
            Subscribed = on;
            return Task.CompletedTask;
        }

        public Task WriteAsync(byte[] data, CancellationToken ct)
        {
            if (Disposed) throw new ObjectDisposedException(nameof(Link));
            foreach (var frame in _frames.Add(data)) Handle(frame);
            return Task.CompletedTask;
        }

        /// <summary>The device drops the link.</summary>
        public void Disconnect() => Disconnected?.Invoke();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        /// <summary>Sends one upload now.</summary>
        public void Upload(byte[] payload) => SendPacket(Pd(payload));

        /// <summary>Sends a packet as the device does: version 0x13, the payload XORed with the sequence number.</summary>
        public void SendPacket(EcoFlowPacket packet)
        {
            var seq = _seq++;
            var xored = packet.Payload.Select(b => (byte)(b ^ seq)).ToArray();
            var plain = (packet with { Payload = xored, Version = 0x13, Seq = seq | 0x09e03700u }).ToBytes();
            Send(new EcoFlowFrame(EcoFlowFrame.Protocol, Encrypt(plain)).ToBytes());
        }

        private static EcoFlowPacket Pd(byte[] payload) => new(0x02, 0x21, 0xFE, 0x15, payload);

        private void Handle(EcoFlowFrame frame)
        {
            if (frame.Type == EcoFlowFrame.Command)
            {
                if (device.Mute) return;
                switch (frame.Payload)
                {
                    case [0x01, 0x00, ..] when frame.Payload.Length == 42:
                        ExchangeKeys(frame.Payload[2..]);
                        break;
                    case [0x02]:
                        SendKeyInfo();
                        break;
                    default:
                        device._log.Enqueue($"unexpected command {Convert.ToHexString(frame.Payload)}");
                        break;
                }
                return;
            }

            if (!_sessionKey)
            {
                device._log.Enqueue("packet before the session key");
                return;
            }

            var packet = EcoFlowPacket.Parse(Decrypt(frame.Payload));
            device.Packets.Enqueue(packet);
            switch (packet)
            {
                case { Dst: 0x35, CmdSet: 0x35, CmdId: 0x89 }:
                    SendPacket(new EcoFlowPacket(0x35, 0x21, 0x35, 0x89, [0x00, 0x01]));
                    break;
                case { Dst: 0x35, CmdSet: 0x35, CmdId: 0x86 }:
                    CheckUser(packet.Payload);
                    break;
            }
        }

        private void ExchangeKeys(byte[] ours)
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.CreateFromFriendlyName("secP160r1"));
            using var peer = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.CreateFromFriendlyName("secP160r1"),
                Q = new ECPoint { X = ours[..20], Y = ours[20..] },
            });
            var shared = ecdh.DeriveRawSecretAgreement(peer.PublicKey);
            _key = shared[..16];
            _iv = MD5.HashData(shared);
            _sessionKey = false;

            var q = ecdh.ExportParameters(false).Q;
            SendHandshake([0x01, 0x00, 0x00, .. EcoFlowCrypto.PublicPoint(q.X!, q.Y!)]);
        }

        private void SendKeyInfo()
        {
            var random = RandomNumberGenerator.GetBytes(16);
            byte[] seed = [(byte)RandomNumberGenerator.GetInt32(0, 16), (byte)RandomNumberGenerator.GetInt32(1, 255)];
            SendHandshake([0x02, .. Encrypt([.. random, .. seed])]);
            _key = EcoFlowCrypto.SessionKey(seed, random);
            _sessionKey = true;
        }

        private void CheckUser(byte[] token)
        {
            var expected = Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(UserId + Serial)));
            var accepted = Encoding.ASCII.GetString(token) == expected && device.AuthResult == 0;
            device._log.Enqueue(accepted ? "user accepted" : "user rejected");
            Authenticated = accepted;

            if (accepted && device.UploadInsteadOfAuthReply && device.Uploads.Count > 0)
            {
                Upload(device.Uploads[0]);
                return;
            }

            SendPacket(new EcoFlowPacket(0x35, 0x21, 0x35, 0x86, [accepted ? (byte)0 : device.AuthResult is 0 ? (byte)6 : device.AuthResult]));
            if (!accepted) return;
            if (device.AsksForTime) SendPacket(new EcoFlowPacket(0x35, 0x21, 0x01, 0x52, []));
            foreach (var upload in device.Uploads) Upload(upload);
        }

        private void SendHandshake(byte[] payload)
        {
            for (var i = 0; i < device.HandshakeCopies; i++) Send(new EcoFlowFrame(EcoFlowFrame.Command, payload).ToBytes());
        }

        private void Send(byte[] data)
        {
            foreach (var chunk in data.Chunk(device.Mtu)) Received?.Invoke(chunk);
        }

        private byte[] Encrypt(byte[] plain)
        {
            using var aes = Aes.Create();
            aes.Key = _key!;
            return aes.EncryptCbc(plain, _iv!, PaddingMode.PKCS7);
        }

        private byte[] Decrypt(byte[] encrypted)
        {
            using var aes = Aes.Create();
            aes.Key = _key!;
            return aes.DecryptCbc(encrypted, _iv!, PaddingMode.PKCS7);
        }
    }
}
