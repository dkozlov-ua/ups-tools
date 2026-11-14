using System.Buffers.Binary;
using System.Security.Cryptography;

namespace UpsTools.Core.Sharing;

/// <summary>Something went wrong with a link to or from another PC.</summary>
public class SharingException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>The two ends of a link don't have the same sharing code.</summary>
public sealed class WrongSharingCodeException() : SharingException("The sharing code is wrong");

/// <summary>
/// Messages over a TCP stream, encrypted and authenticated with a key both ends derive from the sharing code
/// (<see cref="SharingCode.Key"/>), so that only a PC that has the code can read the UPS or pass off a reading as the
/// UPS's.
/// </summary>
/// <remarks>
/// Each end first sends a frame with 32 random bytes; the keys of the link (one per direction) are derived from the
/// code's key and both ends' random bytes, so a recorded link can't be played back. Every later frame is sealed with
/// AES-GCM under its direction's key, with the frame's number as the nonce: a frame that was changed, dropped,
/// reordered or replayed fails to open. A frame is a 4-byte big-endian length, a type byte and the payload.
/// </remarks>
public sealed class SecureChannel : IDisposable
{
    public const int MaxFrame = 256 * 1024;
    private const int RandomLength = 32;
    private const int TagLength = 16;
    private static readonly byte[] Magic = "UPST\x01"u8.ToArray();

    private const byte HelloFrame = 0;
    private const byte SealedFrame = 1;
    private const byte RefusedFrame = 2;

    private readonly Stream _stream;
    private readonly AesGcm _send;
    private readonly AesGcm _receive;
    private ulong _sent;
    private ulong _received;

    private SecureChannel(Stream stream, byte[] keys, bool client)
    {
        _stream = stream;
        _send = new AesGcm(client ? keys.AsSpan(0, 32) : keys.AsSpan(32, 32), TagLength);
        _receive = new AesGcm(client ? keys.AsSpan(32, 32) : keys.AsSpan(0, 32), TagLength);
    }

    /// <summary>Starts a link as the PC that asks (the one watching).</summary>
    public static async Task<SecureChannel> ConnectAsync(Stream stream, byte[] key, CancellationToken cancellationToken)
    {
        var mine = RandomNumberGenerator.GetBytes(RandomLength);
        await WriteFrameAsync(stream, HelloFrame, [.. Magic, .. mine], cancellationToken);
        var theirs = await ReadHelloAsync(stream, cancellationToken);
        return new SecureChannel(stream, Derive(key, mine, theirs), client: true);
    }

    /// <summary>Starts a link as the PC that answers (the one sharing).</summary>
    public static async Task<SecureChannel> AcceptAsync(Stream stream, byte[] key, CancellationToken cancellationToken)
    {
        var theirs = await ReadHelloAsync(stream, cancellationToken);
        var mine = RandomNumberGenerator.GetBytes(RandomLength);
        await WriteFrameAsync(stream, HelloFrame, [.. Magic, .. mine], cancellationToken);
        return new SecureChannel(stream, Derive(key, theirs, mine), client: false);
    }

    private static byte[] Derive(byte[] key, byte[] client, byte[] server) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 64, salt: [.. client, .. server], info: "UpsTools sharing v1"u8.ToArray());

    private static async Task<byte[]> ReadHelloAsync(Stream stream, CancellationToken cancellationToken)
    {
        var (type, payload) = await ReadFrameAsync(stream, cancellationToken);
        if (type != HelloFrame || payload.Length != Magic.Length + RandomLength || !payload.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new SharingException("It isn't UPS Tools that answers there, or it is a version that doesn't share this way");
        return payload[Magic.Length..];
    }

    public async Task SendAsync(byte[] message, CancellationToken cancellationToken)
    {
        var frame = new byte[message.Length + TagLength];
        _send.Encrypt(Nonce(_sent++), message, frame.AsSpan(0, message.Length), frame.AsSpan(message.Length));
        await WriteFrameAsync(_stream, SealedFrame, frame, cancellationToken);
    }

    /// <exception cref="WrongSharingCodeException">The other end has another code.</exception>
    public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        var (type, frame) = await ReadFrameAsync(_stream, cancellationToken);
        if (type == RefusedFrame) throw new WrongSharingCodeException();
        if (type != SealedFrame || frame.Length < TagLength) throw new SharingException("Unexpected data on the link");
        var message = new byte[frame.Length - TagLength];
        try
        {
            _receive.Decrypt(Nonce(_received++), frame.AsSpan(0, message.Length), frame.AsSpan(message.Length), message);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new WrongSharingCodeException();
        }
        return message;
    }

    /// <summary>Tells the other end, in the clear, that its first message didn't open: its code is wrong.</summary>
    public Task RefuseAsync(CancellationToken cancellationToken) => WriteFrameAsync(_stream, RefusedFrame, [], cancellationToken);

    private static byte[] Nonce(ulong counter)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    private static async Task WriteFrameAsync(Stream stream, byte type, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length + 1 > MaxFrame) throw new SharingException("Message too long");
        var frame = new byte[4 + 1 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length + 1);
        frame[4] = type;
        payload.CopyTo(frame, 5);
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<(byte Type, byte[] Payload)> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 1 or > MaxFrame) throw new SharingException("Unexpected data on the link");
        var body = new byte[length];
        await ReadExactlyAsync(stream, body, cancellationToken);
        return (body[0], body[1..]);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken);
        }
        catch (EndOfStreamException ex)
        {
            throw new SharingException("The other PC closed the link", ex);
        }
    }

    public void Dispose()
    {
        _send.Dispose();
        _receive.Dispose();
        _stream.Dispose();
    }
}
