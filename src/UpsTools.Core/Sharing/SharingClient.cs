using System.Net;
using System.Net.Sockets;

namespace UpsTools.Core.Sharing;

/// <summary>A link to a PC that shares its UPSes (<see cref="SharingServer"/>): one request at a time.</summary>
public sealed class SharingClient : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly SecureChannel _channel;

    private SharingClient(SecureChannel channel, string pc)
    {
        _channel = channel;
        Pc = pc;
    }

    /// <summary>What the PC is called in messages, e.g. "DESKTOP-ABC".</summary>
    public string Pc { get; }

    /// <summary>The computer name this one gives, for the log of the PC that answers.</summary>
    public string From { get; set; } = Environment.MachineName;

    /// <summary>Connects to the PC at <paramref name="address"/> and checks that both ends have the same code.</summary>
    /// <param name="pc">What to call it in messages.</param>
    /// <exception cref="SharingException">It can't be reached, isn't sharing, or the code is wrong (<see cref="WrongSharingCodeException"/>).</exception>
    public static async Task<SharingClient> ConnectAsync(IPAddress address, int port, string code, string pc, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
        tcp.Client.DualMode = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                await tcp.ConnectAsync(address, port, timeout.Token);
                var channel = await SecureChannel.ConnectAsync(tcp.GetStream(), SharingCode.Key(code), timeout.Token);
                return new SharingClient(channel, pc);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SharingException($"{pc} didn't answer");
            }
            catch (SocketException ex)
            {
                throw new SharingException(ex.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => $"{pc} isn't sharing its UPSes any more",
                    SocketError.TimedOut => $"{pc} didn't answer",
                    _ => $"Can't reach {pc}: {ex.Message}",
                }, ex);
            }
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>What it shares.</summary>
    public async Task<SharedPc> ListAsync(CancellationToken cancellationToken)
    {
        var response = await AskAsync(new SharingRequest { Op = SharingRequest.List, From = From }, TimeSpan.FromSeconds(10), cancellationToken);
        return response.Pc ?? throw new SharingException("Unexpected answer");
    }

    /// <summary>Sends a request and waits up to <paramref name="timeout"/> for its answer.</summary>
    internal async Task<SharingResponse> AskAsync(SharingRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await _channel.SendAsync(SharingJson.Write(request), limit.Token);
            return SharingJson.Read<SharingResponse>(await _channel.ReceiveAsync(limit.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SharingException($"{Pc} stopped answering");
        }
        catch (IOException ex) when (ex is not SharingException)
        {
            throw new SharingException($"Lost the link to {Pc}: {ex.GetBaseException().Message}", ex);
        }
    }

    public void Dispose() => _channel.Dispose();
}
