# Phase 0 spike results

All four checks done, live, against retail KCD2 (`MasterMasterSteamPGO`,
version 1.5.6) launched with `-devmode`. **All four pass** (with a corrected
design for #4 - see below). Phase 1 can proceed.

## 1. Retail + `-devmode` shows no debug menu entries — PASSED

Launched `Bin\Win64MasterMasterSteamPGO\KingdomCome.exe -devmode`, took a
screenshot of the main menu: `Continuar / Novo jogo / Carregar jogo /
Configurações / Ajuda / DLCs e extras / Créditos / Sair`. No "(depuração)"
debug entries anywhere — matches normal retail, unlike the Modding Tools
build's cluttered menu screenshotted at the start of this project.

## 2. RemoteConsole (`:4600`) reachable, executes `#`-Lua — PASSED

- `netstat -ano | findstr 4600` → `TCP 0.0.0.0:4600 LISTENING` on the game's
  PID, confirmed on two separate launches.
- Sent `5#System.LogAlways('SPIKE-OK')\0` over a raw TCP socket to
  `127.0.0.1:4600` → `SPIKE-OK` landed in `kcd.log`.
- Also got a banner-type reply frame back immediately (`1 6map trosecko`),
  confirming the connection/handshake side of the protocol too.

## 3. `Mods\itemswap\` pak loads on retail + `-devmode` — PASSED

`kcd.log` on load:
```
[Mod] 'mods/itemswap' has no version restrictions in manifest
[Mod] 'mods/itemswap' is not limited to any game version, it will be enabled
[Mod] Opening paks in mods/itemswap/data/*.pak
Pak 'mods\itemswap\data\itemswap.pak' is opened, root: 'data\'
ITEMSWAP-LOADED
Loading lua init script for mod itemswap ...
```
No manual enable step needed - it auto-enables. Confirmed on two separate
mod pak versions (rebuilt once mid-session).

## 4. Item placement — FAILED as originally planned, PASSED with a corrected design

**Original plan (player as placer): FAILED.** Calling
`player.inventory:CreateItem(cls, health, amount)` then
`player.human:PlaceItem(created, anchorId, false)` did **not** drop anything
into the world. Confirmed two ways:
- Re-probing the player's own inventory afterward showed a **second copy**
  of the same item class had been added directly to their inventory (a new
  wuid appeared).
- A full entity scan within 15m of the player found no new `PickableItem`
  anywhere.

Root cause found along the way: `inventory:CreateItem(...)` returns a
**boolean success flag, not an item handle**. Passing that boolean straight
into `PlaceItem` (as the first attempt did) meant `PlaceItem` never had a
real item reference to work with.

**Corrected, verified design: a hidden, momentary NPC placer.**
1. `System.SpawnEntity({class="NPC", name=..., position=spawnPos})` - has
   real `.inventory`/`.human` components (`hasInv=true hasHuman=true`,
   confirmed live).
2. `placer:Hide(true)` immediately - confirmed this doesn't break the calls
   below, and means the placer is never visible for even a frame.
3. `placer.inventory:CreateItem(cls, health, amount)`.
4. Read the real wuid back via `for _, w in pairs(placer.inventory:GetInventoryTable()) do wuid = w end`
   (the placer is freshly spawned and empty, so there's exactly one entry).
5. `placer.human:PlaceItem(wuid, placer.id, false)`.
6. Scan nearby for a new `PickableItem` not present before step 1 - **found
   it synchronously**, same call, no deferred tick needed (e.g.
   `Shoes04_m01_E000298`, `BootsAnkle03_m01_D000350`,
   `TunicLong01_m04_C000348` across several live runs).
7. `System.RemoveEntity(placer.id)`.

This is implemented in `mod/Data/Scripts/Startup/itemswap.lua` as the real
`ItemSwap_TestSpawn`, re-verified end to end via the actual `itemswap_test_spawn`
console path (not just ad-hoc RC snippets) after rebuilding the pak.

No visible side effects observed in-game (screenshotted before/after) - no
flicker, no NPC pop-in, no change to the player's own hands/gear/health bar.

**Consequence for the plan**: no separate throwaway `PickableItem` anchor
entity is needed at all (§3a/§4a in the plan can drop that step) - the
hidden NPC placer serves as its own position anchor, and placement resolves
synchronously rather than needing the reference project's two-phase
deferred-tick finalize dance. This makes Milestone 1's spawn logic simpler
than planned, not more complex, despite not reusing a ghost/NPC puppet
concept at all (the placer never persists past a single Lua call).

## Verdict

- [x] All four verified → **proceed to Phase 1**
- [x] Corrected design adopted for #4, documented above and in the plan
- [ ] No blocking issues found
