using System.Net;
using System.Net.Sockets;

namespace ItemSwap.Net;

/// <summary>
/// Star topology, host-relayed: the host accepts up to MaxJoiners joiner
/// connections; joiners never connect to each other. "Broadcast" always
/// means "the host relays to every other connected player" - see the
/// plan's §1/§4b.
///
/// This class only speaks the wire protocol and tracks the player roster and
/// claim arbitration. It knows nothing about RemoteConsole, kcd.log, or the
/// game itself - that wiring is the future "skeleton agent"'s job. Testable
/// entirely with real loopback TCP connections between two or three
/// in-process instances, no game required.
///
/// MaxJoiners=9 (10 players total) is a soft cap, not a proven one: every
/// peer's position update becomes one serialized RemoteConsole call into the
/// host's game, throttled to a ~75ms floor between calls by
/// RemoteConsoleClient. At the mod's default 250ms broadcast interval, 9
/// peers alone need ~36 calls/sec just for position - well past the ~13/sec
/// a single RC connection can sustain, so markers may visibly lag behind
/// real positions well before the player count actually hits 10. Lowering
/// the position rate as player count grows, or batching multiple peers'
/// updates into one Lua call per tick, would be the next fix if that shows
/// up in practice.
/// </summary>
public sealed class PeerLink : IAsyncDisposable
{
    public const int MaxJoiners = 9;
    public const byte HostPlayerId = 0;

    private readonly bool _isHost;
    private readonly string _localName;
    private readonly byte[] _secretHash;
    private readonly CancellationTokenSource _cts = new();

    // Host-only.
    private TcpListener? _listener;
    private readonly Dictionary<byte, ConnectedPeer> _peers = new(); // playerId -> peer, host-side only
    private byte _nextPlayerId = 1;

    // Joiner-only.
    private ConnectedPeer? _hostConnection;

    private readonly object _claimLock = new();
    private readonly Dictionary<uint, byte> _resolvedClaims = new(); // dropId -> winner playerId

    public byte LocalPlayerId { get; private set; }
    public bool IsHost => _isHost;

    public event Action<byte, string>? PlayerJoined;
    public event Action<byte>? PlayerLeft;
    /// <summary>Another player's drop arrived - the local game should spawn it.</summary>
    public event Action<ItemDropMessage>? ItemDropReceived;
    /// <summary>A claim was resolved (by the host) - compare WinnerPlayerId to LocalPlayerId.</summary>
    public event Action<ItemClaimResolvedMessage>? ItemClaimResolved;
    /// <summary>A peer's position update arrived - the local game should move/create their presence marker.</summary>
    public event Action<PositionUpdateMessage>? PositionUpdateReceived;
    /// <summary>The host's weather changed - a joiner's local game should force-match it.</summary>
    public event Action<WeatherUpdateMessage>? WeatherUpdateReceived;
    /// <summary>A player (any player) just finished an in-game time skip - every other player's local game should force-match it.</summary>
    public event Action<TimeSkipMessage>? TimeSkipReceived;
    /// <summary>Some player just armed and wants everyone's current time - this player's agent should query its own Calendar.GetWorldTime() and reply with NotifyLocalTimeSkipAsync. Fires for host and joiner alike.</summary>
    public event Action<TimeSyncRequestMessage>? TimeSyncRequestReceived;

    private sealed class ConnectedPeer
    {
        public required byte PlayerId;
        public required string Name;
        public required TcpClient Client;
        public required NetworkStream Stream;
        public readonly SemaphoreSlim WriteLock = new(1, 1);
    }

    private PeerLink(bool isHost, string localName, string sharedSecret)
    {
        _isHost = isHost;
        _localName = localName;
        _secretHash = Protocol.HashSecret(sharedSecret);
    }

    // ===== Host =====

    public static async Task<PeerLink> StartHostAsync(int port, string localName, string sharedSecret, CancellationToken ct = default)
    {
        var link = new PeerLink(isHost: true, localName, sharedSecret) { LocalPlayerId = HostPlayerId };
        link._listener = new TcpListener(IPAddress.Any, port);
        link._listener.Start();
        _ = link.AcceptLoopAsync(link._cts.Token);
        await Task.CompletedTask;
        return link;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = HandleIncomingJoinerAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task HandleIncomingJoinerAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        try
        {
            var frame = await Protocol.ReadFrameAsync(stream, ct).ConfigureAwait(false);
            if (frame is not { Type: MessageType.Hello } helloFrame)
            {
                client.Dispose();
                return;
            }

            var hello = Protocol.DecodeHello(helloFrame.Payload);
            var secretOk = hello.SecretHash.AsSpan().SequenceEqual(_secretHash);
            var roomOk = _peers.Count < MaxJoiners;

            if (!secretOk || !roomOk)
            {
                var ack = Protocol.Encode(new HelloAckMessage(Protocol.ProtocolVersion, Ok: false, AssignedPlayerId: 0));
                await stream.WriteAsync(ack, ct).ConfigureAwait(false);
                client.Dispose();
                return;
            }

            byte assignedId;
            ConnectedPeer peer;
            List<ConnectedPeer> existingPeers;
            lock (_peers)
            {
                assignedId = _nextPlayerId++;
                peer = new ConnectedPeer { PlayerId = assignedId, Name = hello.Name, Client = client, Stream = stream };
                existingPeers = _peers.Values.ToList();
                _peers[assignedId] = peer;
            }

            var okAck = Protocol.Encode(new HelloAckMessage(Protocol.ProtocolVersion, Ok: true, AssignedPlayerId: assignedId));
            await stream.WriteAsync(okAck, ct).ConfigureAwait(false);

            // Bring the new peer up to date on who's already here (host + any other joiner),
            // then tell everyone else about the new peer.
            await SendAsync(peer, Protocol.Encode(new PlayerJoinedMessage(HostPlayerId, _localName))).ConfigureAwait(false);
            foreach (var existing in existingPeers)
                await SendAsync(peer, Protocol.Encode(new PlayerJoinedMessage(existing.PlayerId, existing.Name))).ConfigureAwait(false);

            await BroadcastAsync(Protocol.Encode(new PlayerJoinedMessage(assignedId, hello.Name)), excludePlayerId: assignedId).ConfigureAwait(false);
            PlayerJoined?.Invoke(assignedId, hello.Name);

            await ReadLoopAsync(peer, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A misbehaving/dropped joiner during handshake is not fatal to the host.
        }
    }

    private async Task ReadLoopAsync(ConnectedPeer peer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await Protocol.ReadFrameAsync(peer.Stream, ct).ConfigureAwait(false);
                if (frame is null) break; // clean disconnect

                switch (frame.Value.Type)
                {
                    case MessageType.ItemDrop:
                        {
                            var msg = Protocol.DecodeItemDrop(frame.Value.Payload);
                            ItemDropReceived?.Invoke(msg);
                            await BroadcastAsync(Protocol.Encode(msg), excludePlayerId: peer.PlayerId).ConfigureAwait(false);
                            break;
                        }
                    case MessageType.ItemClaim:
                        {
                            var msg = Protocol.DecodeItemClaim(frame.Value.Payload);
                            await ResolveAndBroadcastClaimAsync(msg.DropId, msg.FromPlayerId).ConfigureAwait(false);
                            break;
                        }
                    case MessageType.PositionUpdate:
                        {
                            var msg = Protocol.DecodePositionUpdate(frame.Value.Payload);
                            PositionUpdateReceived?.Invoke(msg);
                            await BroadcastAsync(Protocol.Encode(msg), excludePlayerId: peer.PlayerId).ConfigureAwait(false);
                            break;
                        }
                    case MessageType.TimeSkip:
                        {
                            var msg = Protocol.DecodeTimeSkip(frame.Value.Payload);
                            TimeSkipReceived?.Invoke(msg);
                            await BroadcastAsync(Protocol.Encode(msg), excludePlayerId: peer.PlayerId).ConfigureAwait(false);
                            break;
                        }
                    case MessageType.TimeSyncRequest:
                        {
                            // Relayed to everyone else, same as ItemDrop -
                            // any connected player might be the one with a
                            // trustworthy clock (e.g. the asker's own save
                            // just reloaded and is now behind), not just the
                            // host. The host's own game answers too (fired
                            // here), each recipient replies with its own
                            // current time as a normal TimeSkipMessage
                            // broadcast - handled entirely by Program.cs.
                            var msg = Protocol.DecodeTimeSyncRequest(frame.Value.Payload);
                            TimeSyncRequestReceived?.Invoke(msg);
                            await BroadcastAsync(Protocol.Encode(msg), excludePlayerId: peer.PlayerId).ConfigureAwait(false);
                            break;
                        }
                    case MessageType.Heartbeat:
                        break;
                    case MessageType.Disconnect:
                        return;
                }
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        finally
        {
            lock (_peers) { _peers.Remove(peer.PlayerId); }
            peer.Client.Dispose();
            await BroadcastAsync(Protocol.Encode(new PlayerLeftMessage(peer.PlayerId))).ConfigureAwait(false);
            PlayerLeft?.Invoke(peer.PlayerId);
        }
    }

    private async Task ResolveAndBroadcastClaimAsync(uint dropId, byte claimantPlayerId)
    {
        byte winner;
        lock (_claimLock)
        {
            if (!_resolvedClaims.TryGetValue(dropId, out winner))
            {
                winner = claimantPlayerId;
                _resolvedClaims[dropId] = winner;
            }
        }

        var msg = new ItemClaimResolvedMessage(dropId, winner);
        ItemClaimResolved?.Invoke(msg); // the host is a player too - it may have a pending local claim on this dropId
        await BroadcastAsync(Protocol.Encode(msg)).ConfigureAwait(false);
    }

    private async Task BroadcastAsync(byte[] frameBytes, byte? excludePlayerId = null)
    {
        List<ConnectedPeer> targets;
        lock (_peers)
        {
            targets = excludePlayerId is byte ex
                ? _peers.Values.Where(p => p.PlayerId != ex).ToList()
                : _peers.Values.ToList();
        }
        foreach (var peer in targets)
            await SendAsync(peer, frameBytes).ConfigureAwait(false);
    }

    private static async Task SendAsync(ConnectedPeer peer, byte[] frameBytes)
    {
        await peer.WriteLock.WaitAsync().ConfigureAwait(false);
        try { await peer.Stream.WriteAsync(frameBytes).ConfigureAwait(false); }
        catch { /* the read loop will notice the dead connection and clean up */ }
        finally { peer.WriteLock.Release(); }
    }

    // ===== Joiner =====

    public static async Task<PeerLink> JoinAsync(string hostAddress, int port, string localName, string sharedSecret, CancellationToken ct = default)
    {
        var link = new PeerLink(isHost: false, localName, sharedSecret);
        var client = new TcpClient();
        await client.ConnectAsync(hostAddress, port, ct).ConfigureAwait(false);
        var stream = client.GetStream();

        var hello = Protocol.Encode(new HelloMessage(Protocol.ProtocolVersion, localName, link._secretHash));
        await stream.WriteAsync(hello, ct).ConfigureAwait(false);

        var frame = await Protocol.ReadFrameAsync(stream, ct).ConfigureAwait(false);
        if (frame is not { Type: MessageType.HelloAck } ackFrame)
            throw new InvalidOperationException("Host did not respond with HelloAck.");

        var ack = Protocol.DecodeHelloAck(ackFrame.Payload);
        if (!ack.Ok)
        {
            client.Dispose();
            throw new InvalidOperationException("Host rejected the connection (bad shared secret, or the game is full).");
        }

        link.LocalPlayerId = ack.AssignedPlayerId;
        link._hostConnection = new ConnectedPeer { PlayerId = HostPlayerId, Name = "host", Client = client, Stream = stream };
        _ = link.JoinerReadLoopAsync(link._cts.Token);
        return link;
    }

    private async Task JoinerReadLoopAsync(CancellationToken ct)
    {
        var host = _hostConnection!;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await Protocol.ReadFrameAsync(host.Stream, ct).ConfigureAwait(false);
                if (frame is null) break;

                switch (frame.Value.Type)
                {
                    case MessageType.PlayerJoined:
                        {
                            var msg = Protocol.DecodePlayerJoined(frame.Value.Payload);
                            PlayerJoined?.Invoke(msg.PlayerId, msg.Name);
                            break;
                        }
                    case MessageType.PlayerLeft:
                        {
                            var msg = Protocol.DecodePlayerLeft(frame.Value.Payload);
                            PlayerLeft?.Invoke(msg.PlayerId);
                            break;
                        }
                    case MessageType.ItemDrop:
                        ItemDropReceived?.Invoke(Protocol.DecodeItemDrop(frame.Value.Payload));
                        break;
                    case MessageType.ItemClaimResolved:
                        ItemClaimResolved?.Invoke(Protocol.DecodeItemClaimResolved(frame.Value.Payload));
                        break;
                    case MessageType.PositionUpdate:
                        PositionUpdateReceived?.Invoke(Protocol.DecodePositionUpdate(frame.Value.Payload));
                        break;
                    case MessageType.WeatherUpdate:
                        WeatherUpdateReceived?.Invoke(Protocol.DecodeWeatherUpdate(frame.Value.Payload));
                        break;
                    case MessageType.TimeSkip:
                        TimeSkipReceived?.Invoke(Protocol.DecodeTimeSkip(frame.Value.Payload));
                        break;
                    case MessageType.TimeSyncRequest:
                        // Relayed here by the host from another joiner (or
                        // the host's own arm) - this joiner's game should
                        // answer too, same as the host does.
                        TimeSyncRequestReceived?.Invoke(Protocol.DecodeTimeSyncRequest(frame.Value.Payload));
                        break;
                    case MessageType.Heartbeat:
                        break;
                    case MessageType.Disconnect:
                        return;
                }
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }
    }

    // ===== Shared local-event API (used identically whether host or joiner) =====

    /// <summary>
    /// Call when THIS player's own game detects a local drop. `dropId` is
    /// supplied by the caller (the game's own Lua mints it, since it needs
    /// the id immediately to track its own ground copy for the claim
    /// watcher) rather than minted here, so every player who ends up with a
    /// copy of the same conceptual item converges on the identical id.
    /// </summary>
    public async Task NotifyLocalDropAsync(uint dropId, Guid itemClass, ushort amount, float health, float x, float y, float z)
    {
        var msg = new ItemDropMessage(dropId, LocalPlayerId, itemClass, amount, health, x, y, z);

        if (_isHost)
            await BroadcastAsync(Protocol.Encode(msg)).ConfigureAwait(false);
        else
            await SendAsync(_hostConnection!, Protocol.Encode(msg)).ConfigureAwait(false);
    }

    /// <summary>Call periodically with THIS player's own current position, so others can show a presence marker for them.</summary>
    public async Task NotifyLocalPositionAsync(float x, float y, float z, bool isCrouching = false, float currentHp = 0f, float maxHp = 0f, bool inCombat = false, bool inDanger = false, bool inTense = false, bool inDialog = false, bool inRiding = false, bool inPickpocketing = false, bool inUnconscious = false, bool inDead = false, bool inWanted = false, bool inArmed = false, bool inCarryingCorpse = false, bool inGambling = false, bool inAlchemy = false, bool inSharpening = false, bool inReading = false, bool inTranscribing = false, bool inSmithing = false, bool isSitting = false, bool isLaying = false, bool inHungry = false, bool inExhausted = false, bool inOutOfBreath = false, bool inLockpicking = false)
    {
        var msg = new PositionUpdateMessage(LocalPlayerId, x, y, z, isCrouching, currentHp, maxHp, inCombat, inDanger, inTense, inDialog, inRiding, inPickpocketing, inUnconscious, inDead, inWanted, inArmed, inCarryingCorpse, inGambling, inAlchemy, inSharpening, inReading, inTranscribing, inSmithing, isSitting, isLaying, inHungry, inExhausted, inOutOfBreath, inLockpicking);

        if (_isHost)
            await BroadcastAsync(Protocol.Encode(msg)).ConfigureAwait(false);
        else
            await SendAsync(_hostConnection!, Protocol.Encode(msg)).ConfigureAwait(false);
    }

    /// <summary>
    /// Host-only: call periodically with the host's own current rain
    /// intensity so joiners can force-match it. No-ops for a joiner - there
    /// is exactly one weather source of truth per session, the host, and a
    /// joiner has nothing meaningful to broadcast here.
    /// </summary>
    public async Task NotifyWeatherAsync(float rainIntensity)
    {
        if (!_isHost) return;
        await BroadcastAsync(Protocol.Encode(new WeatherUpdateMessage(rainIntensity))).ConfigureAwait(false);
    }

    /// <summary>
    /// Call when THIS player's own game just finished an in-game time skip
    /// (detected as a big Calendar.GetWorldTime() delta settling back to
    /// baseline). Relayed like NotifyLocalDropAsync - any player can be the
    /// origin, the host both relays it and applies it to its own game.
    /// </summary>
    public async Task NotifyLocalTimeSkipAsync(double newWorldTime)
    {
        var msg = new TimeSkipMessage(LocalPlayerId, newWorldTime);

        if (_isHost)
            await BroadcastAsync(Protocol.Encode(msg)).ConfigureAwait(false);
        else
            await SendAsync(_hostConnection!, Protocol.Encode(msg)).ConfigureAwait(false);
    }

    /// <summary>
    /// Call once whenever the local mod arms, with this player's own
    /// current world time, to two-way sync with everyone else connected
    /// rather than waiting for someone's next real skip. Symmetric by
    /// design (works for the host too) - see TimeSyncRequestMessage for why
    /// carrying myWorldTime here means neither side needs to arbitrate who
    /// "wins": every recipient just tries applying it (a safe no-op if
    /// they're already ahead) and separately answers with its own time.
    /// </summary>
    public async Task NotifyTimeSyncRequestAsync(double myWorldTime)
    {
        var msg = new TimeSyncRequestMessage(LocalPlayerId, myWorldTime);
        if (_isHost)
            await BroadcastAsync(Protocol.Encode(msg)).ConfigureAwait(false);
        else
            await SendAsync(_hostConnection!, Protocol.Encode(msg)).ConfigureAwait(false);
    }

    /// <summary>Call when THIS player physically picks up a tracked drop (local or a peer's).</summary>
    public async Task NotifyLocalClaimAsync(uint dropId)
    {
        if (_isHost)
        {
            await ResolveAndBroadcastClaimAsync(dropId, LocalPlayerId).ConfigureAwait(false);
        }
        else
        {
            var msg = new ItemClaimMessage(dropId, LocalPlayerId);
            await SendAsync(_hostConnection!, Protocol.Encode(msg)).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        List<ConnectedPeer> peers;
        lock (_peers) { peers = _peers.Values.ToList(); }
        foreach (var p in peers) p.Client.Dispose();
        _hostConnection?.Client.Dispose();
        _cts.Dispose();
        await Task.CompletedTask;
    }
}
