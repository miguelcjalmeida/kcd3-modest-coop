# KCD2 Item Swap

A minimal Kingdom Come: Deliverance II co-op mod for a small group of
trusted friends (up to 3 players). It does exactly one thing: drop an item
in your world, it shows up for the others, first pickup wins. No ghosts, no
NPC/quest/combat sync, no visible presence of other players (that's a
separate, later milestone — see the plan).

Runs on the **retail** game launched with `-devmode` — no separate "Modding
Tools" install required.

## ⚠️ Security — read before playing over the internet

`-devmode` opens KCD2's RemoteConsole on `TCP 0.0.0.0:4600`, **unauthenticated**,
which can execute arbitrary Lua inside the game process. This mod's agent
only ever talks to it on `127.0.0.1` (loopback), but:

- **Never port-forward port 4600. Ever.** Only the host agent's own port
  (see config) needs to be reachable by the other players — over a VPN
  overlay (Tailscale, ZeroTier) is strongly recommended over raw port
  forwarding.
- Add a Windows Firewall rule blocking inbound TCP 4600 from non-loopback
  sources, on every machine.
- This mod is designed for a small group of people you trust, on a private
  connection. It is not hardened for exposure to the open internet.

## Status

Early development — see `docs/SPIKE-RESULTS.md` for the current state of
Phase 0 verification, and the plan this repo was built from for the full
design and roadmap.

## Layout

- `mod/` — the Lua mod pak source (`mod.manifest` + `Data/Scripts/Startup/itemswap.lua`)
- `agent/` — the standalone C# companion process each player runs alongside the game
- `tools/` — build/install scripts and throwaway Phase-0 verification scripts

## Building/installing the mod pak (dev loop)

```powershell
tools\Build-And-Install-Mod.ps1
```

Installs into `<retail KCD2 install>\Mods\itemswap\`. Re-run after editing
`mod/Data/Scripts/Startup/itemswap.lua` and relaunch the game to pick up
changes.

## Launching the game with `-devmode`

Two ways to do this — pick whichever fits how you normally play:

**Option A — every time, manually (what this project's own testing used):**
```powershell
& "C:\Program Files (x86)\Steam\steamapps\common\KingdomComeDeliverance2\Bin\Win64MasterMasterSteamPGO\KingdomCome.exe" -devmode
```

**Option B — set it once as a Steam launch option, then just click Play:**
1. In Steam, right-click **Kingdom Come: Deliverance II** → **Properties**.
2. Under **General**, find **Launch Options** and enter:
   ```
   -devmode
   ```
3. Close the dialog. From now on, Steam's own Play button always launches
   with `-devmode` — no manual exe launch needed.

Either way, this is still the normal retail app (`Kingdom Come: Deliverance
II` in your library) — not the separate "Modding Tools" entry. You'll know
it worked if the main menu looks completely normal (no debug entries); the
mod only activates once you load a save.

## Installing the mod

Run this once, and again any time `mod/Data/Scripts/Startup/itemswap.lua`
changes:

```powershell
tools\Build-And-Install-Mod.ps1
```

This packs `mod/Data` into `itemswap.pak` and copies it, with
`mod.manifest`, into `<retail KCD2 install>\Mods\itemswap\`. It defaults to
the standard Steam install path; pass `-RetailInstall "<path>"` if yours is
different.

**A friend on another machine needs their own copy.** There's no installer
yet, so the simplest path today: copy this whole repo folder to their PC
(or just `mod\` and `tools\Build-And-Install-Mod.ps1`), then have them run
the same script there. It writes into *their* KCD2 `Mods\` folder using
whatever path their own Steam install actually uses.

The game must be closed while installing/updating the pak — it's locked
while running, and the script will fail with a file-in-use error otherwise.

## Playing with a friend

One person **hosts**; the other **joins** (up to 3 people total — a host
plus up to 2 joiners). Both sides need: the game launched with `-devmode`
(above), the mod installed (above), and their own copy of the
`ItemSwapAgent` built and running.

**Building the agent** (each person, once — or share the built
`agent\ItemSwapAgent\bin\Debug\net9.0\` folder directly instead of
rebuilding):
```powershell
cd agent\ItemSwapAgent
dotnet build
```

**Every session, in this order:**
1. Launch the game with `-devmode` and load a save.
2. Run the agent from a real terminal (not piped/redirected - it needs to
   ask you questions):
   ```powershell
   cd agent\ItemSwapAgent
   dotnet run
   ```

**Answer the prompts.** Each one shows `[a default]` in brackets - press
Enter to keep it, or type something else:

```
=== ItemSwap setup - press Enter on any question to keep the [default] ===
Host or join? (host/join) [host]:
Your name [YourName]:
Shared secret (must match everyone else's exactly) [change-me-please]:
Port to listen on (share this + your address with your friends) [7777]:
```

(A **joiner** gets asked for the host's address instead of a listen port:
`Host address to join (ip:port, from whoever is hosting)`.)

Whatever you enter is saved to `itemswap-agent.json` next to the exe, so
next time's prompts default to what you used last - hitting Enter through
all of them repeats the previous session exactly.

**The host** shares two things with whoever's joining: their **reachable
address** (their LAN IP if you're on the same Wi-Fi/network, or a VPN
overlay IP - see the security section below for why never a raw public
IP/port forward), and the **exact same shared secret** they typed. Everyone
must enter the identical secret and, for joiners, the identical
`host-address:port` - or the connection is rejected.

Running non-interactively (stdin piped/redirected) skips the prompts
entirely and just uses `itemswap-agent.json` as already written - useful
for scripting later, not needed for normal play.

Once both sides have `dotnet run` going again with a real config, you
should see `player joined` on the host's console and a `Connected. Assigned
player id N` line on the joiner's. From then on: dropping an item in either
game makes it appear near the other player(s) automatically — no further
commands needed. If two people grab the same dropped item, whoever's pickup
reaches the host first keeps it; the other side gets it silently removed.
