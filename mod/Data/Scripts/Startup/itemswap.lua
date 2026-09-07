-- ItemSwap - Phase 0 spike script
--
-- Purpose: verify, on the RETAIL build launched with -devmode, the riskiest
-- assumptions in the plan before any networking or real drop/pickup logic
-- is written:
--   (a) this pak actually loads (ITEMSWAP-LOADED must appear in kcd.log) - PASSED
--   (b) some entity's inventory/human components can mint and place a world
--       item, with no ghost/NPC persisting in this design.
--
-- (b) was spiked live via RC and the FIRST attempt (using the LOCAL PLAYER's
-- own player.inventory/player.human) FAILED: CreateItem just added a second
-- copy of the item into the player's own inventory; PlaceItem never produced
-- a ground entity. Confirmed by re-probing inventory afterward (an extra
-- wuid of the same class appeared) and by scanning all entities within 15m
-- (no new PickableItem anywhere).
--
-- The WORKING design, verified live (kcd.log [DIAG6]/[DIAG7]): spawn a
-- plain `class="NPC"` entity via System.SpawnEntity (has real .inventory/
-- .human components), Hide(true) it immediately so it's never visible for
-- even a frame, mint the item into ITS inventory, then look up the minted
-- item's real wuid via GetInventoryTable() (CreateItem's return value is a
-- boolean success flag, NOT an item handle - passing it straight into
-- PlaceItem is what made the first attempt do nothing useful) before calling
-- PlaceItem. This produced a real, named ground PickableItem synchronously -
-- no deferred-tick finalize step was needed in testing, unlike the ghost-
-- based two-phase approach this project intentionally does not reuse.
-- The placer entity is deleted immediately after. It exists for a single
-- Lua call, hidden the whole time - never a visible/persistent "ghost".
--
-- Everything here is still throwaway/diagnostic. Phase 1 replaces
-- ItemSwap_TestSpawn with the real poll-based drop detector wired to the
-- network layer.

System.LogAlways("ITEMSWAP-LOADED")

ItemSwap = {}

-- ===== Diagnostics: dump the player's current inventory item classes =====
-- Use this first to find a real item class name/GUID to pass to
-- ItemSwap_TestSpawn, instead of guessing.
function ItemSwap_ProbeInventory()
    if not player or not player.inventory then
        System.LogAlways("[ITEMSWAP] no player/inventory")
        return
    end
    local ok, tbl = pcall(function() return player.inventory:GetInventoryTable() end)
    if not ok or not tbl then
        System.LogAlways("[ITEMSWAP] GetInventoryTable failed: " .. tostring(tbl))
        return
    end
    for _, wuid in pairs(tbl) do
        local okItem, item = pcall(function() return ItemManager.GetItem(wuid) end)
        if okItem and item then
            System.LogAlways(string.format("[ITEMSWAP] item wuid=%s class=%s amount=%s health=%s",
                tostring(wuid), tostring(item.class), tostring(item.amount), tostring(item.health)))
        end
    end
end

-- ===== Spawn: mint + place an item via a hidden, momentary NPC placer =====
-- Verified live (docs/SPIKE-RESULTS.md #4). The placer is hidden the entire
-- time it exists (a handful of Lua statements, no rendered frame) and is
-- removed immediately after - it is a mechanism, never a visible "ghost".
-- Returns the found ground entity, or nil + an error string.
function ItemSwap_SpawnItemAt(cls, health, amount, pos)
    if not pos then return nil, "no target position" end

    -- Snapshot existing PickableItem ids near the target BEFORE minting, so
    -- we can tell "the one that just appeared" apart from anything already
    -- lying around.
    local preIds = {}
    local nearby = System.GetEntitiesInSphere(pos, 3)
    if nearby then
        for _, e in pairs(nearby) do
            if e and e.class == "PickableItem" then preIds[e.id] = true end
        end
    end

    local found, foundErr = nil, nil
    local ok, err = pcall(function()
        local placer = System.SpawnEntity({
            class = "NPC",
            name = "ItemSwap_Placer_" .. tostring(os.clock()),
            position = pos,
        })
        if not placer then error("SpawnEntity(placer) failed") end

        local hideOk, hideErr = pcall(function() placer:Hide(true) end)
        if not hideOk then
            System.LogAlways("[ITEMSWAP] placer Hide failed (non-fatal): " .. tostring(hideErr))
        end

        placer.inventory:CreateItem(cls, health, amount)

        -- CreateItem returns a boolean success flag, not an item handle -
        -- the real wuid has to be read back from the placer's own (freshly
        -- empty, so single-entry) inventory table.
        local wuid = nil
        for _, w in pairs(placer.inventory:GetInventoryTable()) do wuid = w end
        if not wuid then error("CreateItem did not produce a readable item wuid") end

        placer.human:PlaceItem(wuid, placer.id, false)

        local nearby2 = System.GetEntitiesInSphere(pos, 3)
        if nearby2 then
            for _, e in pairs(nearby2) do
                if e and e.class == "PickableItem" and not preIds[e.id] then
                    found = e
                    break
                end
            end
        end
        if not found then foundErr = "placer ran with no error, but no new ground item was found nearby" end

        System.RemoveEntity(placer.id)
    end)

    if not ok then return nil, tostring(err) end
    return found, foundErr
end

-- A point ~dist meters in front of the LOCAL player, at their own height.
-- Used both for local test-spawns and for materializing a peer's drop -
-- see ItemSwap_OnPeerDrop for why peer world coordinates are never used
-- for placement.
function ItemSwap_PosInFrontOfPlayer(dist)
    if not player then return nil end
    local pos = nil
    pcall(function() pos = player:GetWorldPos() end)
    if not pos then return nil end
    local ang = nil
    pcall(function() ang = player:GetWorldAngles() end)
    local yaw = (ang and ang.z) or 0
    return { x = pos.x + math.cos(yaw) * dist, y = pos.y + math.sin(yaw) * dist, z = pos.z }
end

-- Usage from the in-game console (retail, -devmode, RC or local ~):
--   #ItemSwap_TestSpawn("<itemClass>", 1, 1.0)
function ItemSwap_TestSpawn(cls, amount, health)
    amount = tonumber(amount) or 1
    health = tonumber(health) or 1.0

    local spawnPos = ItemSwap_PosInFrontOfPlayer(1.5)
    if not spawnPos then
        System.LogAlways("[ITEMSWAP-SPIKE] no player/position")
        return
    end

    local found, err = ItemSwap_SpawnItemAt(cls, health, amount, spawnPos)
    if found then
        System.LogAlways(string.format("[ITEMSWAP-SPIKE] placed: ground item name=%s id=%s",
            tostring(found:GetName()), tostring(found.id)))
    else
        System.LogAlways("[ITEMSWAP-SPIKE-ERR] " .. tostring(err))
    end
end

-- ===== Receiving a peer's drop (the other half of "shareable via dropping") =====
-- Called by the C# agent (via RC, plain `#`-eval is fine here - this is a
-- one-shot call, not something that starts a timer) whenever the peer link
-- reports another player's ItemDrop message:
--   #ItemSwap_OnPeerDrop("<dropId>", "<cls>", <amount>, <health>)
--
-- Deliberately ignores the sender's world x/y/z. Every player here is on
-- their own completely independent single-player save - the sender's
-- coordinates describe a position in a world the receiver's game has never
-- loaded and has no relationship to. The only placement that means anything
-- is "near wherever the receiving player actually is right now", same as a
-- local test-spawn.
--
-- dropId originates on the DROPPING player's side (minted in
-- ItemSwap_DetectTick) and rides the wire unchanged through the relay, so
-- every player who ends up with a ground copy of this same conceptual item
-- - the original dropper and every receiver - tracks it under the identical
-- id. That's what let's the claim watcher below correlate "who actually
-- picked this up" across independent worlds.
function ItemSwap_OnPeerDrop(dropId, cls, amount, health)
    amount = tonumber(amount) or 1
    health = tonumber(health) or 1.0

    local spawnPos = ItemSwap_PosInFrontOfPlayer(2.0)
    if not spawnPos then
        System.LogAlways("[ITEMSWAP] OnPeerDrop: no local player/position, dropping dropId=" .. tostring(dropId))
        return
    end

    local found, err = ItemSwap_SpawnItemAt(cls, health, amount, spawnPos)
    if found then
        System.LogAlways(string.format(
            "[ITEMSWAP] OnPeerDrop placed dropId=%s class=%s amount=%s ground=%s",
            tostring(dropId), tostring(cls), tostring(amount), tostring(found:GetName())))
        ItemSwap_TrackDrop(dropId, cls, amount, found:GetName())
    else
        System.LogAlways(string.format("[ITEMSWAP-ERR] OnPeerDrop failed dropId=%s: %s",
            tostring(dropId), tostring(err)))
    end
end

-- ===== Phase 1: automatic drop detection =====
-- Dual-gate, checked every tick: (a) a PickableItem entity appeared near the
-- player that we haven't accounted for yet, AND (b) some item class in the
-- player's own inventory count just went down. Only firing when BOTH hold is
-- what distinguishes "I just dropped this" from world clutter, an NPC's own
-- drop happening nearby, or the detector's own baseline noise.
--
-- Known simplification: the emitted "health" is always 1.0 (full/new),
-- since reading the exact condition of the specific dropped item isn't
-- wired up yet. Fine for early testing; revisit if condition needs to
-- survive a trade.
ItemSwap.detectRunning = false
ItemSwap.detectIntervalMs = 750
ItemSwap.dropRadius = 3
ItemSwap.seenItemIds = {}     -- entity id -> true, PickableItems already accounted for
ItemSwap.lastInvCounts = {}   -- item class -> count, as of the previous tick

-- ===== Claim / first-pickup-wins =====
-- Every player who ends up with a ground copy of a given dropId - the
-- original dropper (tracked from ItemSwap_DetectTick) and every receiver
-- (tracked from ItemSwap_OnPeerDrop) - watches their own copy here for a
-- local pickup. The host is the sole arbiter (PeerLink.ResolveAndBroadcast-
-- ClaimAsync on the C# side); this side only detects "did MY copy disappear"
-- and reacts to the eventual resolution via ItemSwap_OnClaimResolved.
ItemSwap.tracked = {}   -- dropId (as a string key) -> {cls, amount, entityName, state}

-- Confirmed live (2026-09-07): this game's Lua tostring() on a number does
-- NOT behave like standard Lua's default ("%.14g") - it silently drops to
-- scientific notation far earlier (matches roughly "%.6g"), so
-- tostring(1328647168) here produces "1.32865e+09", not "1328647168". A
-- dropId minted via math.random(1, 2000000000) is exactly the kind of value
-- this corrupts. Using plain tostring() as a table key or in a wire message
-- silently breaks: two different dropIds can collide on the same mangled
-- key, and a mangled id sent over the wire fails uint32 parsing on the C#
-- side entirely. string.format("%.0f", n) is not subject to this and always
-- gives the exact integer text. Strings pass through unchanged (a dropId
-- received from a peer already arrives as an exact decimal string, formed
-- from a real integer on the C# side - never re-derived from a Lua number).
function ItemSwap_DropIdKey(dropId)
    if type(dropId) == "number" then return string.format("%.0f", dropId) end
    return tostring(dropId)
end

function ItemSwap_TrackDrop(dropId, cls, amount, entityName)
    ItemSwap.tracked[ItemSwap_DropIdKey(dropId)] = { cls = cls, amount = tonumber(amount) or 1, entityName = entityName, state = "ground" }
end

-- Called every detect tick (piggybacks the same timer - no separate
-- Script.SetTimer chain needed). For each tracked drop still in "ground"
-- state, checks whether its entity is still there; if it vanished (and we
-- didn't do that ourselves - there's no other remover in this design), that
-- means a local pickup, so it's reported as a claim.
function ItemSwap_ClaimWatchTick()
    for dropId, t in pairs(ItemSwap.tracked) do
        if t.state == "ground" then
            local ent = System.GetEntityByName(t.entityName)
            if not ent then
                t.state = "claimed_local"
                System.LogAlways("[ITEMSWAP-EVT] claim " .. dropId)
            end
        end
    end
end

-- Called by the agent once the host resolves a claim:
--   #ItemSwap_OnClaimResolved("<dropId>", <wonAsInt>)
-- `won` is 1 or 0 - the agent (not Lua) is the side that knows the local
-- player's network id needed to decide that.
--
-- Deliberately checks REALITY (is the ground entity still there? is the
-- item actually in our inventory?) rather than trusting `t.state` to know
-- what to do about a loss. Confirmed live this distinction matters: the
-- claim watcher only polls every detectIntervalMs (750ms default), so a
-- resolution can arrive before this client's own pickup has even been
-- locally detected yet (t.state still "ground" even though the player
-- physically already has it, or is about to). Trusting `t.state ==
-- "claimed_local"` alone silently skipped the rollback in that ordering -
-- the player kept an item they'd lost the race for, with no message at all.
function ItemSwap_OnClaimResolved(dropId, won)
    local key = ItemSwap_DropIdKey(dropId)
    local t = ItemSwap.tracked[key]
    if not t then return end -- a drop we're not tracking (e.g. already resolved) - fine

    local iWon = (tostring(won) == "1" or won == true)
    if iWon then
        ItemSwap.tracked[key] = nil
        System.LogAlways("[ITEMSWAP] claim " .. key .. " resolved: kept it")
        return
    end

    -- Lost. If our ground copy is still sitting there, remove it right now
    -- so it can never be picked up locally after the fact - this covers the
    -- common case (resolution arrives before the player gets to it) with no
    -- inventory surgery needed at all.
    local ent = System.GetEntityByName(t.entityName)
    if ent then
        pcall(function() System.RemoveEntity(ent.id) end)
        ItemSwap.tracked[key] = nil
        System.LogAlways("[ITEMSWAP] claim " .. key .. " resolved: lost, removed the unclaimed ground copy")
        return
    end

    -- Ground copy already gone - either our own watcher already caught the
    -- pickup (t.state == "claimed_local") or it vanished a moment ago and
    -- our own poll just hasn't noticed yet. Either way, the only thing that
    -- matters now is whether it's actually sitting in our inventory.
    ItemSwap.tracked[key] = nil
    local wuid = nil
    if player and player.inventory then
        pcall(function()
            for _, w in pairs(player.inventory:GetInventoryTable()) do
                local iok, item = pcall(function() return ItemManager.GetItem(w) end)
                if iok and item and item.class == t.cls then wuid = w; break end
            end
        end)
    end

    if wuid then
        -- Known simplification: matches by class only, so if the player
        -- already owned another of the same class before this pickup, which
        -- one gets deleted isn't guaranteed - acceptable for now, same
        -- spirit as this project's other early-testing simplifications.
        pcall(function() player.inventory:DeleteItem(wuid, t.amount) end)
        System.LogAlways("[ITEMSWAP] claim " .. key .. " resolved: too slow, rolled back")
    else
        System.LogAlways("[ITEMSWAP] claim " .. key .. " resolved: lost, but nothing found to roll back")
    end
    -- TODO: an on-screen "too slow" message once a suitable UI call is confirmed.
end

function ItemSwap_InventoryCounts()
    local counts = {}
    if not player or not player.inventory then return counts end
    local ok, tbl = pcall(function() return player.inventory:GetInventoryTable() end)
    if not ok or not tbl then return counts end
    for _, wuid in pairs(tbl) do
        local okItem, item = pcall(function() return ItemManager.GetItem(wuid) end)
        if okItem and item and item.class then
            counts[item.class] = (counts[item.class] or 0) + 1
        end
    end
    return counts
end

-- Reads a WORLD PickableItem entity's real item class, or nil if it can't
-- be determined. Confirmed live (2026-09-07) this needs entity.item:GetId()
-- (a real item wuid) followed by ItemManager.GetItem(wuid).class - NOT
-- ItemManager.GetItem(entity.id) directly (that returns something, but
-- without a usable .class field), and NOT entity.item.class either (the
-- .item sub-object is a bound C++ wrapper exposing only "__this" as a raw
-- field - actual data comes through methods like :GetId(), not properties).
function ItemSwap_GetGroundItemClass(entity)
    local cls = nil
    pcall(function()
        local wuid = entity.item:GetId()
        local itemData = ItemManager.GetItem(wuid)
        if itemData then cls = itemData.class end
    end)
    return cls
end

function ItemSwap_DetectTick()
    if not ItemSwap.detectRunning then return end
    Script.SetTimer(ItemSwap.detectIntervalMs, ItemSwap_DetectTick)  -- reschedule first: a Lua error must not kill the loop

    if not player then return end
    local pos = nil
    pcall(function() pos = player:GetWorldPos() end)
    if not pos then return end

    local newCounts = ItemSwap_InventoryCounts()

    -- PickableItem entities nearby that we haven't accounted for yet.
    local newItems = {}
    local nearby = System.GetEntitiesInSphere(pos, ItemSwap.dropRadius)
    if nearby then
        for _, e in pairs(nearby) do
            if e and e.class == "PickableItem" and not ItemSwap.seenItemIds[e.id] then
                ItemSwap.seenItemIds[e.id] = true
                newItems[#newItems + 1] = e
            end
        end
    end

    -- For each new nearby item, verify ITS OWN actual class is one that
    -- really decreased - never just assume the first decreased class found
    -- belongs to it. Confirmed live this assumption was wrong and caused a
    -- real bug: a class unrelated to the actual drop (e.g. something
    -- untracked, or some other item's count moving for an unrelated reason
    -- in the same ~750ms tick) could get attributed to the new item,
    -- sending a peer a completely different item than what was dropped.
    for _, dropped in ipairs(newItems) do
        local realCls = ItemSwap_GetGroundItemClass(dropped)
        if realCls then
            local prevCount = ItemSwap.lastInvCounts[realCls]
            local nowCount = newCounts[realCls] or 0
            if prevCount and nowCount < prevCount then
                local dpos = pos
                pcall(function() dpos = dropped:GetWorldPos() or pos end)
                local amount = prevCount - nowCount

                -- Minted here, not by the agent: this side needs the id
                -- immediately to track its own ground copy for the claim
                -- watcher, and every other player converges on the same id
                -- because it rides the wire unchanged from here on.
                local dropId = math.random(1, 2000000000)
                ItemSwap_TrackDrop(dropId, realCls, amount, dropped:GetName())

                System.LogAlways(string.format(
                    "[ITEMSWAP-EVT] drop %d %s %d %.2f %.3f %.3f %.3f",
                    dropId, realCls, amount, 1.0, dpos.x, dpos.y, dpos.z))
            end
            -- realCls with no matching decrease (prevCount nil, e.g. an
            -- untracked food/consumable class, or not actually smaller) is
            -- correctly just skipped - no event, no misattribution.
        end
    end

    ItemSwap.lastInvCounts = newCounts
    ItemSwap_ClaimWatchTick()
end

function ItemSwap_DetectOn()
    if ItemSwap.detectRunning then return end
    ItemSwap.detectRunning = true
    ItemSwap.lastInvCounts = ItemSwap_InventoryCounts()

    -- Baseline: anything already lying around nearby doesn't count as "new".
    ItemSwap.seenItemIds = {}
    if player then
        local pos = nil
        pcall(function() pos = player:GetWorldPos() end)
        if pos then
            local nearby = System.GetEntitiesInSphere(pos, ItemSwap.dropRadius)
            if nearby then
                for _, e in pairs(nearby) do
                    if e and e.class == "PickableItem" then ItemSwap.seenItemIds[e.id] = true end
                end
            end
        end
    end

    System.LogAlways(string.format("[ITEMSWAP] drop detector ON (interval=%dms, radius=%d)",
        ItemSwap.detectIntervalMs, ItemSwap.dropRadius))
    Script.SetTimer(ItemSwap.detectIntervalMs, ItemSwap_DetectTick)
end

function ItemSwap_DetectOff()
    ItemSwap.detectRunning = false
    System.LogAlways("[ITEMSWAP] drop detector OFF")
end

-- %LINE arrives as ONE string (everything after the command name), so this
-- wrapper splits it into the 3 positional args ItemSwap_TestSpawn expects.
-- Prefer calling ItemSwap_TestSpawn(cls, amount, health) directly via a
-- '#'-prefixed RC/console Lua-eval where 3 real arguments are wanted;
-- this command form exists for typing directly into the local console.
function ItemSwap_TestSpawnCmd(line)
    local parts = {}
    for w in tostring(line or ""):gmatch("%S+") do parts[#parts + 1] = w end
    ItemSwap_TestSpawn(parts[1], parts[2], parts[3])
end

System.AddCCommand("itemswap_probe_inventory", "ItemSwap_ProbeInventory()",
    "ItemSwap: dump the player's current inventory item classes")
System.AddCCommand("itemswap_test_spawn", 'ItemSwap_TestSpawnCmd("%LINE")',
    "ItemSwap: spawn a test item via the hidden-placer technique: itemswap_test_spawn <class> [amount] [health]")
System.AddCCommand("itemswap_detect_on", "ItemSwap_DetectOn()",
    "ItemSwap Phase 1: start the automatic drop detector")
System.AddCCommand("itemswap_detect_off", "ItemSwap_DetectOff()",
    "ItemSwap Phase 1: stop the automatic drop detector")

-- IMPORTANT, confirmed live (2026-09-07) - do NOT call ItemSwap_DetectOn()
-- here at the bottom of the Startup script. It looks like it should work
-- (it sets ItemSwap.detectRunning = true) but the Script.SetTimer call
-- inside it silently never fires, because Script.SetTimer only works from
-- a genuine runtime context, NOT from a startup/init script's own top-level
-- execution (documented in kcd2_lua_api.md: "runtime only, NOT from
-- startup/init scripts", from the reference project this project takes
-- techniques, not code, from). The same call also silently fails when sent
-- as a raw '#'-prefixed RC Lua-eval - that is apparently not "runtime"
-- either. It DOES work when triggered as a plain registered console command
-- (RC ConsoleCommand type with just the bare command name, no '#' prefix) -
-- confirmed repeatedly: `itemswap_detect_off` then `itemswap_detect_on` sent
-- that way produced real automatic [ITEMSWAP-EVT] lines with zero further
-- manual intervention. This matches the reference project's own design:
-- KCD2MP_StartEmitter is NEVER auto-called at the bottom of kdcmp.lua either
-- - it is only ever reached via its mp_emit_on AddCCommand, triggered
-- externally by their C# agent.
--
-- Consequence for this project's own C# agent (Phase 2/3): it must detect
-- each fresh level/save load (e.g. by tailing kcd.log for ITEMSWAP-LOADED)
-- and then send the plain command `itemswap_detect_on` over RC - not
-- '#ItemSwap_DetectOn()' - every single time. There is no way for the mod
-- to reliably self-arm on load from inside this script.
--
-- This is left un-called deliberately, as a reminder. Start the detector via
-- itemswap_detect_on (sent as a bare command over RC, or typed directly into
-- the in-game console) after each level/save load.
