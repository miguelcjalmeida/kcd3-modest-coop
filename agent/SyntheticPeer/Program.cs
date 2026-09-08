using System.Globalization;
using ItemSwap.Net;

// A fake second (or third) player, for testing the real ItemSwapAgent + real
// game end-to-end without needing a second machine or a second copy of
// KCD2 - same idea as the reference project's own synthetic-peer testing.
// This talks the real wire protocol; ItemSwapAgent cannot tell it apart
// from a real player's agent.

if (args.Length < 3)
{
    Console.WriteLine("Usage: SyntheticPeer <hostAddr:port> <name> <sharedSecret>");
    Console.WriteLine("Then type commands:");
    Console.WriteLine("  drop <itemClassGuid> <amount> <health> [x] [y] [z]   - simulate this fake player dropping an item (position optional)");
    Console.WriteLine("  claim <dropId>                            - simulate this fake player picking up a tracked drop");
    Console.WriteLine("  pos <x> <y> <z> [crouching]               - simulate this fake player's position (crouching: 1/true, optional)");
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
            await link.NotifyLocalPositionAsync(x, y, z, crouching);
            Console.WriteLine($"[peer] sent position {x},{y},{z} crouching={crouching}");
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
