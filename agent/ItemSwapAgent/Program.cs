using System.Globalization;
using ItemSwap.Net;
using ItemSwapAgent;

var config = InteractiveSetup.Run(Config.Load());

// InteractiveSetup guarantees a valid Role when it actually prompted; this
// only matters on the non-interactive (redirected stdin) path, where it
// returns the loaded config file untouched.
if (config.Role is not ("host" or "join"))
{
    Console.WriteLine("[config] Role must be 'host' or 'join'.");
    return 1;
}

Console.WriteLine($"[agent] Starting as {config.Role}, name='{config.PlayerName}'");

PeerLink peerLink;
if (config.Role == "host")
{
    peerLink = await PeerLink.StartHostAsync(config.ListenPort, config.PlayerName, config.SharedSecret);
    Console.WriteLine($"[agent] Hosting on port {config.ListenPort}. Share your reachable address with up to 2 friends.");
    Console.WriteLine("[agent] Never forward port 4600 (RemoteConsole) - only this agent's port needs to cross the network. See README.md.");
}
else
{
    var idx = config.PeerAddress.LastIndexOf(':');
    if (idx <= 0 || !int.TryParse(config.PeerAddress[(idx + 1)..], out var hostPort))
    {
        Console.WriteLine("[config] PeerAddress must be set to 'host:port' for role=join.");
        return 1;
    }
    var hostAddr = config.PeerAddress[..idx];
    Console.WriteLine($"[agent] Connecting to {hostAddr}:{hostPort} ...");
    peerLink = await PeerLink.JoinAsync(hostAddr, hostPort, config.PlayerName, config.SharedSecret);
    Console.WriteLine($"[agent] Connected. Assigned player id {peerLink.LocalPlayerId}.");
}

await using var rc = new RemoteConsoleClient(config.RemoteConsoleHost, config.RemoteConsolePort);

// Tracked purely so presence-marker labels can show a real name instead of
// a bare player id - PositionUpdateMessage itself only carries id+coords.
var playerNames = new Dictionary<byte, string>();

peerLink.PlayerJoined += (id, name) =>
{
    playerNames[id] = name;
    Console.WriteLine($"[agent] player joined: id={id} name={name}");
};
peerLink.PlayerLeft += async id =>
{
    playerNames.Remove(id);
    Console.WriteLine($"[agent] player left: id={id}");
    try { await rc.SendLuaAsync($"ItemSwap_OnPeerLeft({id})"); }
    catch (Exception ex) { Console.WriteLine($"[agent] failed to clear presence marker for player {id}: {ex.Message}"); }
};

peerLink.ItemDropReceived += async msg =>
{
    Console.WriteLine($"[agent] peer drop received: dropId={msg.DropId} from={msg.FromPlayerId} class={msg.ItemClass} amount={msg.Amount}");
    try
    {
        var health = msg.Health.ToString(CultureInfo.InvariantCulture);
        var x = msg.X.ToString(CultureInfo.InvariantCulture);
        var y = msg.Y.ToString(CultureInfo.InvariantCulture);
        var z = msg.Z.ToString(CultureInfo.InvariantCulture);
        await rc.SendLuaAsync($"ItemSwap_OnPeerDrop('{msg.DropId}', '{msg.ItemClass}', {msg.Amount}, {health}, {x}, {y}, {z})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[agent] failed to inject peer drop into the game: {ex.Message}");
    }
};

peerLink.PositionUpdateReceived += async msg =>
{
    // Deliberately no per-update console line here (this fires at the
    // detect tick's own rate, ~1.3Hz per connected peer - matches the
    // project's long-standing goal of keeping console/log output minimal,
    // same reasoning as never streaming position continuously in the
    // reference project's style).
    try
    {
        var x = msg.X.ToString(CultureInfo.InvariantCulture);
        var y = msg.Y.ToString(CultureInfo.InvariantCulture);
        var z = msg.Z.ToString(CultureInfo.InvariantCulture);
        var name = playerNames.TryGetValue(msg.PlayerId, out var n) ? n : $"Player {msg.PlayerId}";
        // Basic escaping: a peer's display name is free text they typed into
        // their own agent config, not a validated identifier like a GUID -
        // this only guards against an accidental/malicious quote breaking the
        // Lua call outright, not a full sandboxing (RC already assumes a
        // trusted, shared-secret-gated peer per the README's threat model).
        var escapedName = name.Replace("\\", "\\\\").Replace("'", "\\'");
        await rc.SendLuaAsync($"ItemSwap_OnPeerPosition({msg.PlayerId}, {x}, {y}, {z}, '{escapedName}')");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[agent] failed to update presence marker for player {msg.PlayerId}: {ex.Message}");
    }
};

peerLink.ItemClaimResolved += async msg =>
{
    // The Lua side doesn't know its own network player id (that's assigned
    // during the PeerLink handshake, entirely a C#-side concept) - so this
    // agent, not Lua, decides win/lose and passes just that outcome down.
    var won = msg.WinnerPlayerId == peerLink.LocalPlayerId;
    Console.WriteLine($"[agent] claim resolved: dropId={msg.DropId} winner={msg.WinnerPlayerId} (mine={won})");
    try
    {
        await rc.SendLuaAsync($"ItemSwap_OnClaimResolved('{msg.DropId}', {(won ? 1 : 0)})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[agent] failed to send claim resolution: {ex.Message}");
    }
};

await using var logTail = new LogTailReader(config.KcdLogPath);
logTail.LineRead += async line =>
{
    if (line.Contains("ITEMSWAP-LOADED", StringComparison.Ordinal))
    {
        // The mod cannot reliably arm its own detector timer on load -
        // Script.SetTimer only works via a plain console command, never
        // from the Startup script's own top-level execution (confirmed
        // live, docs/PHASE1-FINDINGS.md). This is not optional: skip it and
        // the detector silently does nothing for the whole session.
        Console.WriteLine("[agent] game (re)loaded the mod - arming the drop detector");
        try { await rc.SendCommandAsync("itemswap_detect_on"); }
        catch (Exception ex) { Console.WriteLine($"[agent] failed to arm detector: {ex.Message}"); }
        return;
    }

    const string tag = "[ITEMSWAP-EVT]";
    var tagIndex = line.IndexOf(tag, StringComparison.Ordinal);
    if (tagIndex < 0) return;

    var parts = line[(tagIndex + tag.Length)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) return;

    try
    {
        switch (parts[0])
        {
            // drop <dropId> <cls> <amount> <health> <x> <y> <z>
            // dropId is minted by Lua (it needs the id immediately to track
            // its own ground copy for the claim watcher), so it's used as-is
            // here rather than this agent minting its own.
            case "drop" when parts.Length >= 8
                && uint.TryParse(parts[1], out var dropId)
                && Guid.TryParse(parts[2], out var cls)
                && int.TryParse(parts[3], out var amount)
                && float.TryParse(parts[4], CultureInfo.InvariantCulture, out var health)
                && float.TryParse(parts[5], CultureInfo.InvariantCulture, out var x)
                && float.TryParse(parts[6], CultureInfo.InvariantCulture, out var y)
                && float.TryParse(parts[7], CultureInfo.InvariantCulture, out var z):
                {
                    await peerLink.NotifyLocalDropAsync(dropId, cls, (ushort)amount, health, x, y, z);
                    Console.WriteLine($"[agent] local drop detected (class={cls}, amount={amount}) -> sent to peers as dropId={dropId}");
                    break;
                }

            // claim <dropId>
            case "claim" when parts.Length >= 2 && uint.TryParse(parts[1], out var claimDropId):
                Console.WriteLine($"[agent] local claim on dropId={claimDropId}");
                await peerLink.NotifyLocalClaimAsync(claimDropId);
                break;

            // pos <x> <y> <z> - piggybacks the same detect tick, ~1.3Hz.
            // No console line here either, same reasoning as the receive side.
            case "pos" when parts.Length >= 4
                && float.TryParse(parts[1], CultureInfo.InvariantCulture, out var px)
                && float.TryParse(parts[2], CultureInfo.InvariantCulture, out var py)
                && float.TryParse(parts[3], CultureInfo.InvariantCulture, out var pz):
                await peerLink.NotifyLocalPositionAsync(px, py, pz);
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[agent] failed to handle log line '{line}': {ex.Message}");
    }
};
logTail.Start();

Console.WriteLine("[agent] Running. Press Ctrl+C to exit.");

var shutdown = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.TrySetResult(); };
await shutdown.Task;

await peerLink.DisposeAsync();
return 0;
