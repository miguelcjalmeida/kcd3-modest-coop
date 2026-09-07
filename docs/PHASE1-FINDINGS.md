# Phase 1 findings — the timer bug and its fix

## Symptom

The automatic drop detector (`ItemSwap_DetectOn` / `ItemSwap_DetectTick`,
meant to run every 750ms via a self-rescheduling `Script.SetTimer` chain)
never fired on its own. Manually invoking `ItemSwap_DetectTick()` directly
(via RC, `#ItemSwap_DetectTick()`) always worked correctly and detected real
drops (confirmed multiple times, live, with a real "Dado viciado" item).
`ItemSwap.detectRunning` reported `true` throughout, which was misleading —
the flag was set, but the actual timer chain was dead.

## Root cause

`Script.SetTimer(ms, callback)` only works from a genuine **runtime**
context. It does **not** work:
- Called at the top level of a Startup/init script (confirmed by
  `docs/kcd2_lua_api.md` line 37 from the reference project this project
  takes techniques from: *"runtime only, NOT from startup/init scripts"*).
- Called via a raw `#`-prefixed RC Lua-eval command (confirmed live: even a
  trivial `Script.SetTimer(1000, function() System.LogAlways(...) end)`
  sent this way never fired).

It **does** work when reached via a plain registered console command (RC's
`ConsoleCommand` frame type, sent as just the bare command name, no `#`
prefix — the same path a player typing directly into the local `~` console
uses). Confirmed twice, live: `itemswap_detect_off` then `itemswap_detect_on`
sent as bare commands produced real, automatic `[ITEMSWAP-EVT]` lines with
zero further manual intervention.

This matches the reference project's own design, which was easy to miss
until this bit us directly: `KCD2MP_StartEmitter()` (their equivalent
timer-starting function) is **never** auto-called at the bottom of
`kdcmp.lua`. It is only ever reached via its `mp_emit_on` `AddCCommand`,
triggered externally by their C# agent sending that command name. They
never rely on the script self-arming at load time either.

## Consequence for this project's design

**The mod cannot reliably self-arm its own timer on level/save load.**
`ItemSwap_DetectOn()` is deliberately left un-called at the bottom of
`itemswap.lua` now (a commented-out attempt was tried and reverted — see
the comment there for the full explanation).

This means the **C# agent** (Phase 2/3) has a required responsibility, not
an optional nicety: it must detect each fresh level/save load — the
simplest signal is tailing `kcd.log` for `ITEMSWAP-LOADED`, which fires
every time the Startup script re-executes (confirmed: the whole script
re-runs from scratch on every level/save load, resetting all `ItemSwap.*`
state) — and then send the plain command `itemswap_detect_on` over RC
(bare command name, not `#ItemSwap_DetectOn()`) every single time. Missing
this means the detector silently does nothing for that session, with
`detectRunning` still falsely reporting `true` if anything upstream ever
queries it.

Broader rule to carry into Phase 2/3's `RemoteConsoleClient` design: **any
call that needs to start a `Script.SetTimer` chain must be sent as a plain
console command, never as `#`-eval.** One-shot/immediate calls (spawning an
item, probing inventory, reading state) work fine either way — this only
matters for anything that schedules a recurring callback.

## Verified sequence (for re-testing)

1. Load a save.
2. Confirm `ITEMSWAP-LOADED` in `kcd.log`.
3. Send `itemswap_detect_off` then `itemswap_detect_on` as **bare console
   commands** over RC (not `#`-prefixed).
4. Drop any non-food, non-quest item from the inventory screen.
5. Within ~1-2s, `[ITEMSWAP-EVT] drop <class> <amount> <health> <x> <y> <z>`
   appears in `kcd.log` with no further manual action.

## Known separate limitation (not this bug)

Food/consumable items (tested with two apples) never appear in
`player.inventory:GetInventoryTable()`, so a drop of food is invisible to
the current class-count-diff detector — KCD2 appears to track consumables
through a separate inventory subsystem. Not addressed yet; out of scope for
proving the core mechanism works.
