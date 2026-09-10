using System.Globalization;
using System.Reflection;
using ItemSwap.Net;

// A fake second (or third) player, for testing the real ItemSwapAgent + real
// game end-to-end without needing a second machine or a second copy of
// KCD2 - same idea as the reference project's own synthetic-peer testing.
// This talks the real wire protocol; ItemSwapAgent cannot tell it apart
// from a real player's agent.

Console.WriteLine($"[peer] SyntheticPeer v{Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"}");

if (args.Length < 3)
{
    Console.WriteLine("Usage: SyntheticPeer <hostAddr:port> <name> <sharedSecret>");
    Console.WriteLine("Then type commands:");
    Console.WriteLine("  drop <itemClassGuid> <amount> <health> [x] [y] [z]   - simulate this fake player dropping an item (position optional)");
    Console.WriteLine("  claim <dropId>                            - simulate this fake player picking up a tracked drop");
    Console.WriteLine("  pos <x> <y> <z> [crouching] [curHp] [maxHp] [inCombat] [inDanger] [inTense] [inDialog] [inRiding] [inPickpocketing] [inUnconscious] [inDead] [inWanted] [inArmed] [inCarryingCorpse] [inGambling] [inAlchemy] [inSharpening] [inReading] [inTranscribing] [inSmithing] [isSitting] [isLaying] [inHungry] [inExhausted] [inOutOfBreath] [inLockpicking]  - simulate this fake player's position (bool fields: 1/true; HP optional, defaults to unknown)");
    Console.WriteLine("  quit");
    return 1;
}

var idx = args[0].LastIndexOf(':');
var hostAddr = args[0][..idx];
var hostPort = int.Parse(args[0][(idx + 1)..]);
var name = args[1];
var secret = args[2];

Console.WriteLine($"[peer] connecting to {hostAddr}:{hostPort} as '{name}' ...");
await using var link = await PeerLink.JoinAsync(hostAddr, hostPort, name, secret);
Console.WriteLine($"[peer] connected, assigned player id {link.LocalPlayerId}");

link.PlayerJoined += (id, n) => Console.WriteLine($"[peer] player joined: id={id} name={n}");
link.PlayerLeft += id => Console.WriteLine($"[peer] player left: id={id}");
link.ItemDropReceived += m => Console.WriteLine(
    $"[peer] received ItemDrop: dropId={m.DropId} from={m.FromPlayerId} class={m.ItemClass} amount={m.Amount} health={m.Health}");
link.ItemClaimResolved += m => Console.WriteLine(
    $"[peer] claim resolved: dropId={m.DropId} winner={m.WinnerPlayerId} (mine={m.WinnerPlayerId == link.LocalPlayerId})");
link.PositionUpdateReceived += m => Console.WriteLine(
    $"[peer] position update: playerId={m.PlayerId} pos={m.X:F2},{m.Y:F2},{m.Z:F2}");

Console.WriteLine("[peer] ready. Commands: drop <classGuid> <amount> <health> | claim <dropId> | quit");
while (true)
{
    var line = Console.ReadLine();
    if (line is null)
    {
        // No (more) stdin to read - e.g. piped input ran out, or stdin
        // isn't interactive at all in a background run. Don't exit: keep
        // the connection alive and just listen for incoming events until
        // killed (Ctrl+C in a real terminal).
        Console.WriteLine("[peer] no more input - idling and listening only. Ctrl+C / kill to stop.");
        await Task.Delay(Timeout.Infinite);
        continue;
    }
    if (line.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) continue;

    try
    {
        if (parts[0] == "drop" && parts.Length >= 4)
        {
            var cls = Guid.Parse(parts[1]);
            var amount = ushort.Parse(parts[2]);
            var health = float.Parse(parts[3], CultureInfo.InvariantCulture);
            // Optional world position - defaults to 0,0,0 (which the mod
            // treats as "no real position supplied" and falls back to
            // placing near the receiving player instead).
            var x = parts.Length >= 7 ? float.Parse(parts[4], CultureInfo.InvariantCulture) : 0f;
            var y = parts.Length >= 7 ? float.Parse(parts[5], CultureInfo.InvariantCulture) : 0f;
            var z = parts.Length >= 7 ? float.Parse(parts[6], CultureInfo.InvariantCulture) : 0f;
            var dropId = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
            await link.NotifyLocalDropAsync(dropId, cls, amount, health, x, y, z);
            Console.WriteLine($"[peer] sent drop, dropId={dropId}");
        }
        else if (parts[0] == "claim" && parts.Length >= 2)
        {
            var dropId = uint.Parse(parts[1]);
            await link.NotifyLocalClaimAsync(dropId);
            Console.WriteLine($"[peer] sent claim for dropId={dropId}");
        }
        else if (parts[0] == "pos" && parts.Length >= 4)
        {
            var x = float.Parse(parts[1], CultureInfo.InvariantCulture);
            var y = float.Parse(parts[2], CultureInfo.InvariantCulture);
            var z = float.Parse(parts[3], CultureInfo.InvariantCulture);
            var crouching = parts.Length >= 5 && (parts[4] == "1" || parts[4].Equals("true", StringComparison.OrdinalIgnoreCase));
            var curHp = parts.Length >= 6 ? float.Parse(parts[5], CultureInfo.InvariantCulture) : 0f;
            var maxHp = parts.Length >= 7 ? float.Parse(parts[6], CultureInfo.InvariantCulture) : 0f;
            var inCombat = parts.Length >= 8 && (parts[7] == "1" || parts[7].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inDanger = parts.Length >= 9 && (parts[8] == "1" || parts[8].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inTense = parts.Length >= 10 && (parts[9] == "1" || parts[9].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inDialog = parts.Length >= 11 && (parts[10] == "1" || parts[10].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inRiding = parts.Length >= 12 && (parts[11] == "1" || parts[11].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inPickpocketing = parts.Length >= 13 && (parts[12] == "1" || parts[12].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inUnconscious = parts.Length >= 14 && (parts[13] == "1" || parts[13].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inDead = parts.Length >= 15 && (parts[14] == "1" || parts[14].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inWanted = parts.Length >= 16 && (parts[15] == "1" || parts[15].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inArmed = parts.Length >= 17 && (parts[16] == "1" || parts[16].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inCarryingCorpse = parts.Length >= 18 && (parts[17] == "1" || parts[17].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inGambling = parts.Length >= 19 && (parts[18] == "1" || parts[18].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inAlchemy = parts.Length >= 20 && (parts[19] == "1" || parts[19].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inSharpening = parts.Length >= 21 && (parts[20] == "1" || parts[20].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inReading = parts.Length >= 22 && (parts[21] == "1" || parts[21].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inTranscribing = parts.Length >= 23 && (parts[22] == "1" || parts[22].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inSmithing = parts.Length >= 24 && (parts[23] == "1" || parts[23].Equals("true", StringComparison.OrdinalIgnoreCase));
            var isSitting = parts.Length >= 25 && (parts[24] == "1" || parts[24].Equals("true", StringComparison.OrdinalIgnoreCase));
            var isLaying = parts.Length >= 26 && (parts[25] == "1" || parts[25].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inHungry = parts.Length >= 27 && (parts[26] == "1" || parts[26].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inExhausted = parts.Length >= 28 && (parts[27] == "1" || parts[27].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inOutOfBreath = parts.Length >= 29 && (parts[28] == "1" || parts[28].Equals("true", StringComparison.OrdinalIgnoreCase));
            var inLockpicking = parts.Length >= 30 && (parts[29] == "1" || parts[29].Equals("true", StringComparison.OrdinalIgnoreCase));
            await link.NotifyLocalPositionAsync(x, y, z, crouching, curHp, maxHp, inCombat, inDanger, inTense, inDialog, inRiding, inPickpocketing, inUnconscious, inDead, inWanted, inArmed, inCarryingCorpse, inGambling, inAlchemy, inSharpening, inReading, inTranscribing, inSmithing, isSitting, isLaying, inHungry, inExhausted, inOutOfBreath, inLockpicking);
            Console.WriteLine($"[peer] sent position {x},{y},{z} crouching={crouching} hp={curHp}/{maxHp} inCombat={inCombat} inDanger={inDanger} inTense={inTense} inDialog={inDialog} inRiding={inRiding} inPickpocketing={inPickpocketing} inUnconscious={inUnconscious} inDead={inDead} inWanted={inWanted} inArmed={inArmed} inCarryingCorpse={inCarryingCorpse} inGambling={inGambling} inAlchemy={inAlchemy} inSharpening={inSharpening} inReading={inReading} inTranscribing={inTranscribing} inSmithing={inSmithing} isSitting={isSitting} isLaying={isLaying} inHungry={inHungry} inExhausted={inExhausted} inOutOfBreath={inOutOfBreath} inLockpicking={inLockpicking}");
        }
        else
        {
            Console.WriteLine("[peer] unrecognized command");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[peer] error: {ex.Message}");
    }
}

return 0;
