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

## Real 2-player session: wrong item class sent (the pairing bug)

First real friend-to-friend test (2026-09-07) surfaced a serious bug: a
player dropped a coat and the other player received a hat; separately, a
dropped bread roll arrived as pants. Both were traced to the same root
cause in `ItemSwap_DetectTick`'s original design - it paired "some new
PickableItem appeared nearby" with "some inventory class decreased" as if
finding one of each in the same ~750ms tick proved they were the same
event. They frequently weren't:

- Food/consumables aren't tracked by `ItemSwap_InventoryCounts` at all (see
  the food/consumables limitation noted after the Phase 0 spike results),
  so a dropped food item was never itself "the decreased class" - the code
  would instead grab whatever unrelated class happened to have decreased
  around the same time and misattribute it.
- Even between two normal, fully-tracked equipment items, if anything else
  changed inventory count in the same tick (unequipping, item degrading,
  a second coincidental drop), the code had no way to tell which decrease
  belonged to which new item - it just consumed them in whatever arbitrary
  order `pairs()`/`GetEntitiesInSphere` happened to produce.

Confirmed via the actual joiner's own agent log: `local drop detected
(class=<hat's class>, ...)` - the wrong class was already what the
*sending* player's own Lua reported, before anything reached the network.
Confirmed on the receiving side too: the spawned ground item's name matched
exactly what its (wrong) class GUID represents, proving the spawn path
(`ItemSwap_SpawnItemAt`) was faithfully honest the whole time - the bug was
entirely upstream, in detection.

**Fix**: read each new ground item's *real* class directly instead of
assuming, and only fire if that specific class actually decreased.
Live research needed to find how - two dead ends first:
- `ItemManager.GetItem(entity.id)` returns a truthy object but with no
  usable `.class` (confirmed earlier for a "cabbage" world entity too).
- `entity.item.class` is nil - `.item` is a bound C++ wrapper whose only
  raw field is `__this`; real data comes through its methods, not
  properties (confirmed by dumping its metatable's `__index`: methods
  include `GetId`, `GetUIName`, `GetParams`, `GetStats`, etc., no
  `GetClass`).

What works, verified live: `entity.item:GetId()` returns the item's real
wuid, and `ItemManager.GetItem(thatWuid).class` then resolves correctly -
the same wuid-based lookup that already worked for inventory items, just
reached via the entity's `.item:GetId()` instead of the inventory table.

New `ItemSwap_GetGroundItemClass(entity)` helper wraps this. Detection now
iterates each new item and checks *its own* class against the count deltas,
rather than iterating count deltas and grabbing an arbitrary new item. A
class with no matching decrease (nil `prevCount` - e.g. untracked food) is
now correctly just skipped, with no misattribution - a side benefit that
also cleanly resolves the earlier-documented food/consumables gap instead
of letting it corrupt an unrelated event.
