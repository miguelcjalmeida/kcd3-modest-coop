using System.Globalization;
using System.Reflection;
using ItemSwap.Net;
using ItemSwapAgent;

Console.WriteLine($"[agent] ItemSwapAgent v{Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"}");

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
    Console.WriteLine($"[agent] Hosting on port {config.ListenPort}. Share your reachable address with up to {ItemSwap.Net.PeerLink.MaxJoiners} friends.");
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
    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
        "[agent] position update received: playerId={0} pos={1:F2},{2:F2},{3:F2}",
        msg.PlayerId, msg.X, msg.Y, msg.Z));
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
        var crouching = msg.IsCrouching ? "true" : "false";
        var curHp = msg.CurrentHp.ToString(CultureInfo.InvariantCulture);
        var maxHp = msg.MaxHp.ToString(CultureInfo.InvariantCulture);
        var inCombat = msg.InCombat ? "true" : "false";
        var inDanger = msg.InDanger ? "true" : "false";
        var inTense = msg.InTense ? "true" : "false";
        var inDialog = msg.InDialog ? "true" : "false";
        var inRiding = msg.InRiding ? "true" : "false";
        var inPickpocketing = msg.InPickpocketing ? "true" : "false";
        var inUnconscious = msg.InUnconscious ? "true" : "false";
        var inDead = msg.InDead ? "true" : "false";
        var inWanted = msg.InWanted ? "true" : "false";
        var inArmed = msg.InArmed ? "true" : "false";
        var inCarryingCorpse = msg.InCarryingCorpse ? "true" : "false";
        await rc.SendLuaAsync($"ItemSwap_OnPeerPosition({msg.PlayerId}, {x}, {y}, {z}, '{escapedName}', {crouching}, {curHp}, {maxHp}, {inCombat}, {inDanger}, {inTense}, {inDialog}, {inRiding}, {inPickpocketing}, {inUnconscious}, {inDead}, {inWanted}, {inArmed}, {inCarryingCorpse})");
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
        //
        // In practice this auto-arm attempt often loses the race with RC
        // actually being ready this early in the game's startup - confirmed
        // live, repeatedly. It's still attempted (free when it works), but
        // isn't the only way to get going: `itemswap_start` in the in-game
        // console does the same thing on demand and is the reliable
        // fallback whenever this fires too early.
        Console.WriteLine("[agent] game (re)loaded the mod - arming the drop detector and marker animation " +
            "(if this fails, type 'itemswap_start' in the game's console once it's fully loaded)");
        try { await rc.SendCommandAsync("itemswap_start"); }
        catch (Exception ex) { Console.WriteLine($"[agent] failed to auto-arm: {ex.Message}"); }
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

            // pos <x> <y> <z> <crouching> <curHp> <maxHp> <inCombat> <inDanger> -
            // piggybacks the same detect tick. <crouching>/<inCombat>/<inDanger>/
            // <inTense>/<inDialog>/<inRiding>/<inPickpocketing>/<inUnconscious>/
            // <inDead>/<inWanted>/<inArmed>/<inCarryingCorpse> are "1"/"0";
            // each trailing field is optional and defaults to "off" rather
            // than failing the whole match, so an older mod build missing
            // the newer fields still works.
            // No console line here either, same reasoning as the receive side.
            case "pos" when parts.Length >= 4
                && float.TryParse(parts[1], CultureInfo.InvariantCulture, out var px)
                && float.TryParse(parts[2], CultureInfo.InvariantCulture, out var py)
                && float.TryParse(parts[3], CultureInfo.InvariantCulture, out var pz):
                var pCrouching = parts.Length >= 5 && parts[4] == "1";
                var pCurHp = parts.Length >= 6 && float.TryParse(parts[5], CultureInfo.InvariantCulture, out var chp) ? chp : 0f;
                var pMaxHp = parts.Length >= 7 && float.TryParse(parts[6], CultureInfo.InvariantCulture, out var mhp) ? mhp : 0f;
                var pInCombat = parts.Length >= 8 && parts[7] == "1";
                var pInDanger = parts.Length >= 9 && parts[8] == "1";
                var pInTense = parts.Length >= 10 && parts[9] == "1";
                var pInDialog = parts.Length >= 11 && parts[10] == "1";
                var pInRiding = parts.Length >= 12 && parts[11] == "1";
                var pInPickpocketing = parts.Length >= 13 && parts[12] == "1";
                var pInUnconscious = parts.Length >= 14 && parts[13] == "1";
                var pInDead = parts.Length >= 15 && parts[14] == "1";
                var pInWanted = parts.Length >= 16 && parts[15] == "1";
                var pInArmed = parts.Length >= 17 && parts[16] == "1";
                var pInCarryingCorpse = parts.Length >= 18 && parts[17] == "1";
                await peerLink.NotifyLocalPositionAsync(px, py, pz, pCrouching, pCurHp, pMaxHp, pInCombat, pInDanger, pInTense, pInDialog, pInRiding, pInPickpocketing, pInUnconscious, pInDead, pInWanted, pInArmed, pInCarryingCorpse);
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
