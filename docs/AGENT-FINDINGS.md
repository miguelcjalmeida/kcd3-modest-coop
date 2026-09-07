# Agent (C#) findings

## kcd.log tailing: two real bugs hit and fixed

**1. `FileInfo.Length` can lag behind the true size of a file another
process is actively appending to** (a known Windows metadata-caching
gotcha). `LogTailReader` originally called `new FileInfo(path).Length` once
per poll to check for growth; this occasionally reported a stale size even
though the game had already flushed new content, stalling detection
indefinitely. Fixed by opening the file as a `FileStream` and reading
`.Length` from the live handle instead, which reflects the real size.

**2. A line's content and its trailing `\n` can arrive as two separate
writes.** If a poll reads "...content" with no newline yet, that text is
correctly held as an incomplete "partial line" to be completed next poll.
But if nothing else gets logged afterward, there's no *next* line to ever
supply that newline - the content sits in the partial buffer forever and
`LineRead` never fires for it. Confirmed live: a lone item drop with no
further game log activity right after it was read (`position` advanced
correctly) but never dispatched.

Fixed with a grace-period flush: a non-empty partial line that hasn't
changed for 400ms is emitted anyway, on the assumption that the newline
was just flushed on a slight delay rather than the line being genuinely
incomplete. Verified live, repeatedly, after the fix: a single drop with no
other activity around it now reaches the peer within about a second.

## End-to-end verification method: a synthetic peer

Real 2-player testing needs a second machine, which wasn't available.
Instead, `agent/SyntheticPeer` is a small standalone tool that speaks the
real `ItemSwap.Net` wire protocol (same idea as the reference project's own
synthetic-peer testing) - `dotnet run -- <host:port> <name> <secret>`, then
either issue `drop <classGuid> <amount> <health>` / `claim <dropId>` once
via piped stdin, or leave it running with no stdin to just idle and listen
for real events.

This proved both directions of the full agent, with everything real except
the second player:
- **Synthetic peer → real agent → real game**: a fake `ItemDrop` sent to the
  running `ItemSwapAgent` (host) was injected via RemoteConsole and produced
  a real, named ground item in the actual game (`kcd.log`:
  `[ITEMSWAP] OnPeerDrop placed dropId=<id> ... ground=<real item name>`).
- **Real game → real agent → synthetic peer**: dropping a real item in-game
  was detected by the Lua-side timer (armed via the agent watching for
  `ITEMSWAP-LOADED` and sending the plain `itemswap_detect_on` command, per
  `PHASE1-FINDINGS.md`), parsed from `kcd.log` by the agent, and delivered
  over the wire to the synthetic peer with a matching `dropId`.

Note when using stdin-piped one-shot commands with `SyntheticPeer`: after
the piped input is exhausted, `Console.ReadLine()` returns `null`; the tool
treats that as "no more commands" and switches to idle-listening rather
than exiting, so it stays connected and observable after a scripted
one-shot action.

## Claim resolution: don't trust local tracking state, check reality

First implementation of `ItemSwap_OnClaimResolved` rolled back a lost claim
only when `t.state == "claimed_local"` (i.e., only if this client's own
750ms claim watcher had already detected the local pickup before the
resolution arrived). Live test with a synthetic competing claim exposed the
gap directly: the real player physically picked up an item, but the
resolution (network round trip + RC injection) arrived back at the game
*before* the next detection tick had polled and noticed - so `t.state` was
still `"ground"`, the rollback silently no-op'd, and the player kept an item
they'd actually lost the race for. No error, no log line, nothing to notice
by.

Fixed by having `ItemSwap_OnClaimResolved` check the actual current state
of the world instead of the tracked flag:
1. If the ground entity is still there when a loss resolves, remove it
   immediately (covers the common case - resolution beats the player to the
   pickup - with no inventory surgery at all).
2. If it's already gone, look for the item in the player's own inventory by
   class and delete it if found.

Verified live, twice: the buggy version left the player holding a fresh
tunic they'd lost the race for with no error, and the fixed version
correctly logged `claim <id> resolved: too slow, rolled back` and removed
exactly the freshly-picked-up copy while leaving the player's own
pre-existing item of the same class untouched.
