using System.Net;
using System.Net.Sockets;
using ItemSwap.Net;
using Xunit;

namespace ItemSwap.Net.Tests;

public class PeerLinkTests
{
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<T> WaitForAsync<T>(TaskCompletionSource<T> tcs, string what, int timeoutMs = 3000)
    {
        var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (winner != tcs.Task) throw new TimeoutException($"Timed out waiting for: {what}");
        return await tcs.Task;
    }

    [Fact]
    public async Task Joiner_gets_assigned_player_id_1_and_can_see_host_in_roster()
    {
        var port = GetFreePort();
        await using var host = await PeerLink.StartHostAsync(port, "Host", "secret");

        var joinedTcs = new TaskCompletionSource<(byte, string)>();
        host.PlayerJoined += (id, name) => joinedTcs.TrySetResult((id, name));

        await using var joiner = await PeerLink.JoinAsync("127.0.0.1", port, "Joiner", "secret");

        Assert.Equal(0, host.LocalPlayerId);
        Assert.Equal(1, joiner.LocalPlayerId);

        var (joinedId, joinedName) = await WaitForAsync(joinedTcs, "host to observe joiner's PlayerJoined");
        Assert.Equal(1, joinedId);
        Assert.Equal("Joiner", joinedName);
    }

    [Fact]
    public async Task Wrong_shared_secret_is_rejected()
    {
        var port = GetFreePort();
        await using var host = await PeerLink.StartHostAsync(port, "Host", "correct-secret");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PeerLink.JoinAsync("127.0.0.1", port, "Mallory", "wrong-secret"));
    }

    [Fact]
    public async Task A_third_joiner_is_rejected_once_two_are_already_connected()
    {
        var port = GetFreePort();
        await using var host = await PeerLink.StartHostAsync(port, "Host", "secret");
        await using var joinerA = await PeerLink.JoinAsync("127.0.0.1", port, "A", "secret");
        await using var joinerB = await PeerLink.JoinAsync("127.0.0.1", port, "B", "secret");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PeerLink.JoinAsync("127.0.0.1", port, "C", "secret"));
    }

    [Fact]
    public async Task Drop_from_one_joiner_reaches_the_host_and_the_other_joiner_but_not_the_sender()
    {
        var port = GetFreePort();
        await using var host = await PeerLink.StartHostAsync(port, "Host", "secret");
        await using var joinerA = await PeerLink.JoinAsync("127.0.0.1", port, "A", "secret");
        await using var joinerB = await PeerLink.JoinAsync("127.0.0.1", port, "B", "secret");

        // Let the roster settle (PlayerJoined broadcasts) before the real test.
        await Task.Delay(200);

        var hostGotIt = new TaskCompletionSource<ItemDropMessage>();
        var bGotIt = new TaskCompletionSource<ItemDropMessage>();
        var aGotItToo = false;

        host.ItemDropReceived += m => hostGotIt.TrySetResult(m);
        joinerB.ItemDropReceived += m => bGotIt.TrySetResult(m);
        joinerA.ItemDropReceived += _ => aGotItToo = true;

        var cls = Guid.NewGuid();
        const uint dropId = 12345;
        await joinerA.NotifyLocalDropAsync(dropId, cls, amount: 3, health: 0.75f, x: 1, y: 2, z: 3);

        var atHost = await WaitForAsync(hostGotIt, "host to receive the drop");
        var atB = await WaitForAsync(bGotIt, "joiner B to receive the drop");

        Assert.Equal(dropId, atHost.DropId);
        Assert.Equal(1, atHost.FromPlayerId); // joinerA's assigned id
        Assert.Equal(cls, atHost.ItemClass);
        Assert.Equal(3, atHost.Amount);
        Assert.Equal(0.75f, atHost.Health);

        Assert.Equal(dropId, atB.DropId);
        Assert.Equal(1, atB.FromPlayerId);

        await Task.Delay(200); // give a wrongly-routed echo a chance to arrive, if there was a bug
        Assert.False(aGotItToo, "the sender should never receive its own drop back");
    }

    [Fact]
    public async Task Drop_from_the_host_reaches_both_joiners()
    {
        var port = GetFreePort();
        await using var host = await PeerLink.StartHostAsync(port, "Host", "secret");
        await using var joinerA = await PeerLink.JoinAsync("127.0.0.1", port, "A", "secret");
        await using var joinerB = await PeerLink.JoinAsync("127.0.0.1", port, "B", "secret");
        await Task.Delay(200);

        var aGotIt = new TaskCompletionSource<ItemDropMessage>();
        var bGotIt = new TaskCompletionSource<ItemDropMessage>();
        joinerA.ItemDropReceived += m => aGotIt.TrySetResult(m);
        joinerB.ItemDropReceived += m => bGotIt.TrySetResult(m);

        var cls = Guid.NewGuid();
        const uint dropId = 54321;
        await host.NotifyLocalDropAsync(dropId, cls, amount: 1, health: 1.0f, x: 0, y: 0, z: 0);

        var atA = await WaitForAsync(aGotIt, "joiner A to receive the host's drop");
        var atB = await WaitForAsync(bGotIt, "joiner B to receive the host's drop");
        Assert.Equal(dropId, atA.DropId);
        Assert.Equal(dropId, atB.DropId);
        Assert.Equal(PeerLink.HostPlayerId, atA.FromPlayerId);
    }

    [Fact]
    public async Task Concurrent_claims_on_the_same_drop_resolve_to_exactly_one_winner_seen_by_everyone()
    {
        var port = GetFreePort();
        await using var host = await PeerLink.StartHostAsync(port, "Host", "secret");
        await using var joinerA = await PeerLink.JoinAsync("127.0.0.1", port, "A", "secret");
        await using var joinerB = await PeerLink.JoinAsync("127.0.0.1", port, "B", "secret");
        await Task.Delay(200);

        const uint dropId = 999;
        await host.NotifyLocalDropAsync(dropId, Guid.NewGuid(), 1, 1.0f, 0, 0, 0);
        await Task.Delay(200); // let the drop propagate before the race

        var hostResolved = new TaskCompletionSource<byte>();
        var aResolved = new TaskCompletionSource<byte>();
        var bResolved = new TaskCompletionSource<byte>();
        host.ItemClaimResolved += m => hostResolved.TrySetResult(m.WinnerPlayerId);
        joinerA.ItemClaimResolved += m => aResolved.TrySetResult(m.WinnerPlayerId);
        joinerB.ItemClaimResolved += m => bResolved.TrySetResult(m.WinnerPlayerId);

        // A and B both try to claim it "at the same time".
        var claimA = joinerA.NotifyLocalClaimAsync(dropId);
        var claimB = joinerB.NotifyLocalClaimAsync(dropId);
        await Task.WhenAll(claimA, claimB);

        var winnerAtHost = await WaitForAsync(hostResolved, "host to resolve the claim");
        var winnerAtA = await WaitForAsync(aResolved, "joiner A to learn the resolution");
        var winnerAtB = await WaitForAsync(bResolved, "joiner B to learn the resolution");

        Assert.Equal(winnerAtHost, winnerAtA);
        Assert.Equal(winnerAtHost, winnerAtB);
        Assert.True(winnerAtHost is 1 or 2, "the winner should be one of the two claimants");
    }

    [Fact]
    public async Task Joiner_disconnecting_notifies_the_host_and_the_other_joiner()
    {
        var port = GetFreePort();
        await using var host = await PeerLink.StartHostAsync(port, "Host", "secret");
        var joinerA = await PeerLink.JoinAsync("127.0.0.1", port, "A", "secret");
        await using var joinerB = await PeerLink.JoinAsync("127.0.0.1", port, "B", "secret");
        await Task.Delay(200);

        var hostSawLeave = new TaskCompletionSource<byte>();
        var bSawLeave = new TaskCompletionSource<byte>();
        host.PlayerLeft += id => hostSawLeave.TrySetResult(id);
        joinerB.PlayerLeft += id => bSawLeave.TrySetResult(id);

        await joinerA.DisposeAsync();

        var leftAtHost = await WaitForAsync(hostSawLeave, "host to notice joiner A left");
        var leftAtB = await WaitForAsync(bSawLeave, "joiner B to notice joiner A left");
        Assert.Equal(1, leftAtHost);
        Assert.Equal(1, leftAtB);
    }
}
