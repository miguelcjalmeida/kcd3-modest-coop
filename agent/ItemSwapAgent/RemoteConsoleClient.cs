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
    private TcpClient? _client;
    private NetworkStream? _stream;

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

            await EnsureConnectedAsync(ct).ConfigureAwait(false);

            var body = Encoding.UTF8.GetBytes(payload);
            var frame = new byte[1 + body.Length + 1];
            frame[0] = ConsoleCommandType;
            body.CopyTo(frame, 1);
            frame[^1] = 0;

            await _stream!.WriteAsync(frame, ct).ConfigureAwait(false);
            _lastSendUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // The game may have restarted (new process = new RC listener); drop the
            // stale connection so the next call reconnects instead of failing forever.
            Console.WriteLine($"[rc] send failed ({ex.Message}), will reconnect next call");
            DropConnection();
            throw;
        }
        finally { _lock.Release(); }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client is { Connected: true }) return;

        DropConnection();
        _client = new TcpClient();
        await _client.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
        _stream = _client.GetStream();
        Console.WriteLine($"[rc] connected to {_host}:{_port}");
    }

    private void DropConnection()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    public async ValueTask DisposeAsync()
    {
        DropConnection();
        _lock.Dispose();
        await Task.CompletedTask;
    }
}
