using System.Net.Sockets;
using System.Text;

namespace ItemSwapAgent;

/// <summary>
/// Talks to KCD2's RemoteConsole (retail + -devmode, port 4600). Wire
/// format, confirmed live (see docs/SPIKE-RESULTS.md and
/// docs/PHASE1-FINDINGS.md): &lt;ascii-digit type&gt;&lt;utf8 payload&gt;&lt;0x00&gt;,
/// type '5' = ConsoleCommand.
///
/// Two distinct sending modes, and the difference matters:
///   - SendLuaAsync: '#'-prefixed Lua eval. Fine for one-shot calls
///     (spawn an item, probe inventory, apply a peer's drop).
///   - SendCommandAsync: a bare registered console command, no '#'. This is
///     the ONLY form that successfully starts a Script.SetTimer chain -
///     confirmed live, repeatedly (docs/PHASE1-FINDINGS.md). Use this for
///     itemswap_detect_on and nothing else needs it today.
///
/// Always loopback-only (127.0.0.1) - see README.md's security section.
/// Outbound state from the game comes back via kcd.log, not over this
/// connection (confirmed: no reply frames arrive for LogAlways output even
/// at high log verbosity), so this client does not attempt to read replies.
/// </summary>
public sealed class RemoteConsoleClient : IAsyncDisposable
{
    private const byte ConsoleCommandType = (byte)'5';

    private readonly string _host;
    private readonly int _port;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public RemoteConsoleClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    /// <summary>Sends a one-shot '#'-prefixed Lua expression/statement.</summary>
    public Task SendLuaAsync(string luaCode, CancellationToken ct = default) =>
        SendRawAsync("#" + luaCode, ct);

    /// <summary>Sends a bare registered console command (no '#'). Required for anything that starts a Script.SetTimer chain.</summary>
    public Task SendCommandAsync(string command, CancellationToken ct = default) =>
        SendRawAsync(command, ct);

    // Confirmed live: sending several commands back-to-back with zero gap
    // (e.g. a burst of peer drops arriving together) silently lost most of
    // them - no exception on the C# side, no error in kcd.log, they just
    // never executed. A small delay between sends on this connection fixed
    // it in testing. _lock already serializes sends to one at a time; this
    // just paces them instead of firing as fast as the loop can go.
    private static readonly TimeSpan MinGapBetweenSends = TimeSpan.FromMilliseconds(75);
    private DateTime _lastSendUtc = DateTime.MinValue;

    private async Task SendRawAsync(string payload, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sinceLast = DateTime.UtcNow - _lastSendUtc;
            if (sinceLast < MinGapBetweenSends)
                await Task.Delay(MinGapBetweenSends - sinceLast, ct).ConfigureAwait(false);

            var body = Encoding.UTF8.GetBytes(payload);
            var frame = new byte[1 + body.Length + 1];
            frame[0] = ConsoleCommandType;
            body.CopyTo(frame, 1);
            frame[^1] = 0;

            // A fresh connection per send, not a long-lived one. Confirmed
            // live: a persistent connection kept reporting
            // TcpClient.Connected == true and every write kept "succeeding"
            // (no exception, no error anywhere) long after the game's RC
            // listener had actually stopped receiving them - Connected only
            // reflects whether the *last* I/O on the socket failed, it never
            // actively probes the remote end, so a connection that dies
            // silently (the game hiccups, a brief network blip, anything
            // that doesn't produce an immediate local error) is invisible to
            // it forever. RC's own reply-less, one-frame-per-call design
            // means there's nothing a persistent connection actually buys
            // here; reconnecting every call costs a negligible loopback TCP
            // handshake next to the 75ms throttle already in place, and
            // removes this entire failure class outright.
            using var client = new TcpClient();
            await client.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
            await using var stream = client.GetStream();
            await stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            // Confirmed live: closing the connection immediately after the
            // write returns can silently discard the frame on the game's end
            // (a local WriteAsync completing only means the data reached
            // this machine's send buffer, not that the game's RC listener
            // actually read it). Tried a graceful half-close
            // (Socket.Shutdown(SocketShutdown.Send)) to avoid a timing guess
            // entirely, on the theory that the OS only sends the FIN once
            // prior writes are queued for delivery - live-tested, and it
            // does not work here: a 20-call burst produced zero successful
            // deliveries. A short delay, by contrast, was tested the same
            // way and delivered all 20 with none dropped, converging to the
            // correct final position with no backlog. 20ms was chosen by
            // testing, not guessing: 15x shorter than the 300ms this
            // replaced (which throttled real sessions into a growing,
            // eventually minutes-long backlog), while still empirically
            // reliable in a rapid-fire stress test.
            await Task.Delay(20, ct).ConfigureAwait(false);

            _lastSendUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[rc] send failed: {ex.Message}");
            throw;
        }
        finally { _lock.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }
}
