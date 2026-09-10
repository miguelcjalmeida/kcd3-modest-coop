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

// Host-only: the last rain intensity actually broadcast, so a real change
// (not float noise or a duplicate reading) is what triggers a WeatherUpdate -
// this mod's Lua side already reads its own rain at most once every
// extraStateIntervalSec, but that value rides along on every 250ms "pos"
// line regardless, so this is what keeps the network side from re-sending
// the identical value 8x for nothing.
float? lastBroadcastRain = null;

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
        var inGambling = msg.InGambling ? "true" : "false";
        var inAlchemy = msg.InAlchemy ? "true" : "false";
        var inSharpening = msg.InSharpening ? "true" : "false";
        var inReading = msg.InReading ? "true" : "false";
        var inTranscribing = msg.InTranscribing ? "true" : "false";
        var inSmithing = msg.InSmithing ? "true" : "false";
        var isSitting = msg.IsSitting ? "true" : "false";
        var isLaying = msg.IsLaying ? "true" : "false";
        var inHungry = msg.InHungry ? "true" : "false";
        var inExhausted = msg.InExhausted ? "true" : "false";
        var inOutOfBreath = msg.InOutOfBreath ? "true" : "false";
        var inLockpicking = msg.InLockpicking ? "true" : "false";
        await rc.SendLuaAsync($"ItemSwap_OnPeerPosition({msg.PlayerId}, {x}, {y}, {z}, '{escapedName}', {crouching}, {curHp}, {maxHp}, {inCombat}, {inDanger}, {inTense}, {inDialog}, {inRiding}, {inPickpocketing}, {inUnconscious}, {inDead}, {inWanted}, {inArmed}, {inCarryingCorpse}, {inGambling}, {inAlchemy}, {inSharpening}, {inReading}, {inTranscribing}, {inSmithing}, {isSitting}, {isLaying}, {inHungry}, {inExhausted}, {inOutOfBreath}, {inLockpicking})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[agent] failed to update presence marker for player {msg.PlayerId}: {ex.Message}");
    }
};

peerLink.WeatherUpdateReceived += async msg =>
{
    // Only a joiner ever receives this - NotifyWeatherAsync no-ops for the
    // host, so the host's own game never gets its natural weather
    // overridden by itself.
    var rain = msg.RainIntensity.ToString(CultureInfo.InvariantCulture);
    Console.WriteLine($"[agent] host weather update: rainIntensity={rain}");
    try { await rc.SendLuaAsync($"ItemSwap_OnPeerWeather({rain})"); }
    catch (Exception ex) { Console.WriteLine($"[agent] failed to apply weather update: {ex.Message}"); }
};

peerLink.TimeSkipReceived += async msg =>
{
    // Unlike weather, any player can be the origin - the host both applies
    // this to its own game (fired here too when relaying) and forwards it
    // to everyone else.
    var name = playerNames.TryGetValue(msg.FromPlayerId, out var n) ? n : $"Player {msg.FromPlayerId}";
    var newWorldTime = msg.NewWorldTime.ToString(CultureInfo.InvariantCulture);
    Console.WriteLine($"[agent] time skip from {name}: newWorldTime={newWorldTime}");
    try { await rc.SendLuaAsync($"ItemSwap_OnPeerTimeSkip({msg.FromPlayerId}, '{name.Replace("'", "\\'")}', {newWorldTime})"); }
    catch (Exception ex) { Console.WriteLine($"[agent] failed to apply time skip: {ex.Message}"); }
};

peerLink.TimeSyncRequestReceived += async msg =>
{
    // Fires for host and joiner alike (PeerLink relays this to every
    // connected player, like ItemDrop). Two things happen: (1) this
    // player's own game applies the asker's embedded time via
    // ItemSwap_OnPeerTimeSyncRequest, NOT the wholesale
    // ItemSwap_OnPeerTimeSkip - this agent didn't just arm, it's an
    // already-connected peer merely being told the asker's time, so its
    // own perceived time of day must never change, only its underlying
    // day-count may catch up (see that Lua function's own doc comment for
    // why); (2) this agent queries its own game for its current time to
    // answer with - that reply comes back via the [ITEMSWAP-TIMESYNC]
    // log-tail branch above, not synchronously here, since RC has no
    // inbound value-return channel.
    var fromName = playerNames.TryGetValue(msg.FromPlayerId, out var n) ? n : $"Player {msg.FromPlayerId}";
    var fromWorldTime = msg.FromWorldTime.ToString(CultureInfo.InvariantCulture);
    Console.WriteLine($"[agent] time sync requested by {fromName}: theirWorldTime={fromWorldTime}");
    try { await rc.SendLuaAsync($"ItemSwap_OnPeerTimeSyncRequest({msg.FromPlayerId}, '{fromName.Replace("'", "\\'")}', {fromWorldTime})"); }
    catch (Exception ex) { Console.WriteLine($"[agent] failed to apply asker's time: {ex.Message}"); }
    try { await rc.SendLuaAsync("System.LogAlways('[ITEMSWAP-TIMESYNC] ' .. tostring(Calendar.GetWorldTime()))"); }
    catch (Exception ex) { Console.WriteLine($"[agent] failed to query time for sync reply: {ex.Message}"); }
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

    if (line.Contains("Loading saved game", StringComparison.Ordinal))
    {
        // A native engine line (not one this mod emits), confirmed live by
        // the user: dying and auto-reloading the last save does NOT print
        // ITEMSWAP-LOADED (the whole Startup script does not re-execute -
        // this is a lighter-weight reload than a real level/script load),
        // yet the position broadcast silently stopped forever right at
        // this exact line in their log. The mod's own Lua state survives
        // that reload, but its Script.SetTimer chains apparently don't -
        // detectRunning/animRunning/etc. stay stuck true from before,
        // pointing at chains that are actually dead, which is exactly what
        // itemswap_start's own off-then-wait-then-on sequence is for: Off()
        // unconditionally clears each flag regardless of its stale value,
        // so On() is guaranteed to schedule a genuinely fresh chain rather
        // than trusting state left over from before the reload.
        //
        // Deliberately "Loading saved game" without "last": confirmed live
        // that manually loading a DIFFERENT specific save from the main
        // menu prints only this line, with no "Loading last saved game"
        // prefix at all - matching on "last" would miss that case entirely.
        // No risk of double-firing alongside the ITEMSWAP-LOADED branch
        // above either: confirmed live that line appears during mod init,
        // well before any save-loading line, at a completely different
        // point in the log.
        Console.WriteLine("[agent] detected a save load - rearming the mod " +
            "(if this fails, type 'itemswap_start' in the game's console)");
        try { await rc.SendCommandAsync("itemswap_start"); }
        catch (Exception ex) { Console.WriteLine($"[agent] failed to auto-arm after reload: {ex.Message}"); }
        return;
    }

    const string armCheckTag = "[ITEMSWAP-ARMCHECK]";
    var armCheckIndex = line.IndexOf(armCheckTag, StringComparison.Ordinal);
    if (armCheckIndex >= 0)
    {
        // Answer to the watchdog's periodic question below - deliberately
        // answered via the log-tail channel, not a direct RC response
        // (console-command output never comes back over RC itself, only
        // to kcd.log - the same constraint every other outbound signal in
        // this mod already works around).
        var armed = line[(armCheckIndex + armCheckTag.Length)..].Trim();
        if (!string.Equals(armed, "true", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[agent] watchdog: mod not armed - rearming");
            try { await rc.SendCommandAsync("itemswap_start"); }
            catch (Exception ex) { Console.WriteLine($"[agent] watchdog rearm failed: {ex.Message}"); }
        }
        return;
    }

    const string timeSyncTag = "[ITEMSWAP-TIMESYNC]";
    var timeSyncIndex = line.IndexOf(timeSyncTag, StringComparison.Ordinal);
    if (timeSyncIndex >= 0)
    {
        // This line gets emitted in answer to our own TimeSyncRequestReceived
        // handler above, which now fires whenever ANY player (host or
        // joiner) arms and asks - see PeerLink's TimeSyncRequest relay.
        // NotifyLocalTimeSkipAsync sends this agent's answer out correctly
        // either way: broadcasts to every joiner if this agent is host, or
        // sends to the host (who relays) if this agent is a joiner.
        var raw = line[(timeSyncIndex + timeSyncTag.Length)..].Trim();
        if (double.TryParse(raw, CultureInfo.InvariantCulture, out var myWorldTime))
        {
            Console.WriteLine($"[agent] answering time sync request: myWorldTime={myWorldTime}");
            try { await peerLink.NotifyLocalTimeSkipAsync(myWorldTime); }
            catch (Exception ex) { Console.WriteLine($"[agent] failed to broadcast time sync reply: {ex.Message}"); }
        }
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

            // timeskip <newWorldTime> - Lua detected the local game's
            // Calendar.GetWorldTime() ramp-then-settle pattern that means
            // the in-game "skip time" dial just finished.
            case "timeskip" when parts.Length >= 2 && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var newWorldTime):
                Console.WriteLine($"[agent] local time skip detected: newWorldTime={newWorldTime}");
                await peerLink.NotifyLocalTimeSkipAsync(newWorldTime);
                break;

            // timesyncrequest <myWorldTime> - Lua just armed and wants a
            // two-way time sync with everyone connected (symmetric - works
            // the same whether this agent is host or joiner). Carries this
            // player's own current world time so recipients can catch up
            // to it too, not just answer with their own.
            case "timesyncrequest" when parts.Length >= 2 && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var myWorldTime):
                Console.WriteLine($"[agent] requesting time sync from everyone connected: myWorldTime={myWorldTime}");
                await peerLink.NotifyTimeSyncRequestAsync(myWorldTime);
                break;

            // pos <x> <y> <z> <crouching> <curHp> <maxHp> <inCombat> <inDanger> -
            // piggybacks the same detect tick. <crouching>/<inCombat>/<inDanger>/
            // <inTense>/<inDialog>/<inRiding>/<inPickpocketing>/<inUnconscious>/
            // <inDead>/<inWanted>/<inArmed>/<inCarryingCorpse>/<inGambling>/
            // <inAlchemy>/<inSharpening>/<inReading>/<inTranscribing>/
            // <inSmithing>/<isSitting>/<isLaying>/<inHungry>/<inExhausted>/
            // <inOutOfBreath>/<inLockpicking> are "1"/"0"; each trailing field
            // is optional and defaults to "off" rather than failing the whole
            // match, so an older mod build missing the newer fields still
            // works. The final field, <rainIntensity>, is a float (0-1) -
            // the only real weather value the game exposes a reader for.
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
                var pInGambling = parts.Length >= 19 && parts[18] == "1";
                var pInAlchemy = parts.Length >= 20 && parts[19] == "1";
                var pInSharpening = parts.Length >= 21 && parts[20] == "1";
                var pInReading = parts.Length >= 22 && parts[21] == "1";
                var pInTranscribing = parts.Length >= 23 && parts[22] == "1";
                var pInSmithing = parts.Length >= 24 && parts[23] == "1";
                var pIsSitting = parts.Length >= 25 && parts[24] == "1";
                var pIsLaying = parts.Length >= 26 && parts[25] == "1";
                var pInHungry = parts.Length >= 27 && parts[26] == "1";
                var pInExhausted = parts.Length >= 28 && parts[27] == "1";
                var pInOutOfBreath = parts.Length >= 29 && parts[28] == "1";
                var pInLockpicking = parts.Length >= 30 && parts[29] == "1";
                await peerLink.NotifyLocalPositionAsync(px, py, pz, pCrouching, pCurHp, pMaxHp, pInCombat, pInDanger, pInTense, pInDialog, pInRiding, pInPickpocketing, pInUnconscious, pInDead, pInWanted, pInArmed, pInCarryingCorpse, pInGambling, pInAlchemy, pInSharpening, pInReading, pInTranscribing, pInSmithing, pIsSitting, pIsLaying, pInHungry, pInExhausted, pInOutOfBreath, pInLockpicking);

                // Milestone 9: weather sync, host-only. NotifyWeatherAsync
                // itself already no-ops for a joiner, but the change-detection
                // here is what stops the host from re-broadcasting the same
                // reading on every single "pos" line before Lua's own
                // extraStateIntervalSec throttle produces a new one.
                if (config.Role == "host"
                    && parts.Length >= 31
                    && float.TryParse(parts[30], CultureInfo.InvariantCulture, out var rain)
                    && (lastBroadcastRain is null || Math.Abs(lastBroadcastRain.Value - rain) >= 0.02f))
                {
                    lastBroadcastRain = rain;
                    try { await peerLink.NotifyWeatherAsync(rain); }
                    catch (Exception ex) { Console.WriteLine($"[agent] failed to broadcast weather: {ex.Message}"); }
                }
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[agent] failed to handle log line '{line}': {ex.Message}");
    }
};
logTail.Start();

var shutdown = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.TrySetResult(); };

// Guaranteed backstop, independent of every specific "something might have
// gone wrong" trigger above (death-reload, loading a save, the boot-time
// auto-arm losing its race with RC starting up) - confirmed live that the
// boot-time race is real, not just theoretical. Runs unconditionally every
// 10s regardless of whether the mod already looks armed; asking is cheap,
// and re-running itemswap_start when it's already armed is a harmless
// no-op (each *_On() function already guards against double-arming).
// The answer arrives asynchronously via the ARMCHECK branch above, not a
// direct response to this call - same reasoning as everything else here.
_ = Task.Run(async () =>
{
    while (!shutdown.Task.IsCompleted)
    {
        try
        {
            await rc.SendLuaAsync(
                "local ok, armed = pcall(function() return ItemSwap.detectRunning end) " +
                "System.LogAlways('[ITEMSWAP-ARMCHECK] ' .. tostring(ok and armed == true))");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[agent] watchdog check failed: {ex.Message}");
        }
        await Task.Delay(TimeSpan.FromSeconds(10));
    }
});

Console.WriteLine("[agent] Running. Press Ctrl+C to exit.");

await shutdown.Task;

await peerLink.DisposeAsync();
return 0;
