-- ItemSwap
--
-- Item spawning history (the short version - see docs/AGENT-FINDINGS.md for
-- the full live-debugging trail): the original design minted items via a
-- hidden, momentary NPC "placer" entity (System.SpawnEntity{class="NPC"} +
-- CreateItem + PlaceItem), which looked like it worked in solo testing - a
-- real, named ground item always appeared. The first real 2-player session
-- exposed the problem: the CLASS of what appeared was essentially random
-- civilian clothing, regardless of what class was actually requested (money,
-- a unique dice item, anything) - confirmed live by reading the placer's own
-- inventory immediately after CreateItem, before any placement step even
-- ran. A bare, freshly-spawned NPC entity apparently gets its inventory
-- auto-outfitted with random civilian clothing by the engine itself, which
-- clobbers whatever CreateItem was actually asked to make.
--
-- The fix, verified live: skip the placer entirely and use the LOCAL
-- PLAYER's own inventory/human components directly. The very first Phase 0
-- attempt at this had been declared a failure - but that test passed
-- CreateItem's boolean return value into PlaceItem instead of the real item
-- wuid (the same mistake the placer version made too, just never re-checked
-- once the placer path "worked"). With the real wuid, `player.human:
-- PlaceItem(wuid, player.id, false)` correctly drops the exact requested
-- item on the ground next to the player, and it cleanly leaves the player's
-- own inventory (confirmed: no leftover duplicate).

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

-- ===== Spawn: mint on the LOCAL PLAYER, place via a throwaway anchor =====
-- Verified live (docs/AGENT-FINDINGS.md): CreateItem must run on the local
-- player (a bare NPC's inventory gets auto-outfitted with random civilian
-- clothing by the engine, clobbering whatever class was requested).
-- PlaceItem's second argument is a reference ENTITY, not a position - it was
-- originally called with player.id, which places near the player regardless
-- of `pos` (fine for item drops, which are always meant to land near
-- whoever's receiving them, but wrong for anything that needs an arbitrary
-- world position, like a peer's presence marker). Confirmed live: passing a
-- separate throwaway anchor entity spawned AT `pos` places the item next to
-- that anchor instead - 15m from the player, correctly. The anchor is
-- deleted immediately after; it is a position reference, never rendered
-- meaningfully (same class as the item itself, so it never even gets its
-- own frame before being cleaned up).
-- Returns the found ground entity, or nil + an error string.
function ItemSwap_SpawnItemAt(cls, health, amount, pos)
    if not pos then return nil, "no target position" end
    if not player or not player.inventory or not player.human then
        return nil, "no local player/inventory/human"
    end

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
        local anchor = System.SpawnEntity({
            class = "PickableItem",
            name = "ItemSwap_Anchor_" .. tostring(os.clock()),
            position = pos,
        })
        if not anchor then error("SpawnEntity(anchor) failed") end

        local before = {}
        for _, w in pairs(player.inventory:GetInventoryTable()) do before[tostring(w)] = true end

        player.inventory:CreateItem(cls, health, amount)

        -- CreateItem returns a boolean success flag, not an item handle -
        -- the real wuid has to be read back by diffing the inventory table.
        local wuid = nil
        for _, w in pairs(player.inventory:GetInventoryTable()) do
            if not before[tostring(w)] then wuid = w end
        end
        if not wuid then
            System.RemoveEntity(anchor.id)
            error("CreateItem did not produce a readable new item wuid")
        end

        player.human:PlaceItem(wuid, anchor.id, false)

        local nearby2 = System.GetEntitiesInSphere(pos, 3)
        if nearby2 then
            for _, e in pairs(nearby2) do
                if e and e.class == "PickableItem" and e.id ~= anchor.id and not preIds[e.id] then
                    found = e
                    break
                end
            end
        end
        if not found then foundErr = "PlaceItem ran with no error, but no new ground item was found nearby" end

        System.RemoveEntity(anchor.id)
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

-- ===== Milestone 2: peer presence markers =====
-- A visible marker for each connected peer, kept at their actual reported
-- position, floating roughly at character height. Two entities per peer:
--
-- 1. A small inverted-pyramid marker - BasicEntity's own built-in
--    placeholder mesh (normally only meant to be visible in the Sandbox
--    editor for an entity with no model assigned). Live experimentation
--    found this is reliably visible in actual gameplay too, unlike Light,
--    Torch, and ParticleEffect entities, none of which rendered anything
--    without properties this build doesn't expose. Flipped 180 deg (tip
--    pointing down at the peer) and scaled down from its huge default size.
-- 2. A floating name label using "Comment" - the same entity class the
--    game's own level editor uses for in-world dev notes, which already
--    does per-frame System.DrawLabel calls internally. Comment entities are
--    inert during normal play by default (its OnReset gates its own update
--    on the cl_comment CVar, meant for editor use) - cl_comment must be
--    forced on once below or the label silently never draws in a real
--    playthrough.
ItemSwap.peerMarkers = {}  -- playerId (string key) -> {markerName, labelName}
ItemSwap.markerHeightOffset = 1.9  -- meters above the peer's reported (ground) position
ItemSwap.labelHeightOffset = 1.4   -- meters above the peer's reported position (just below the marker's tip)
ItemSwap.markerScale = 0.15
ItemSwap.labelSize = 6.0

System.SetCVar('cl_comment', 1)  -- required once: Comment entities no-op their per-frame draw otherwise

-- Called by the agent (one-shot '#'-eval is fine, no timer involved) on
-- every position update relayed from a peer:
--   #ItemSwap_OnPeerPosition(<playerId>, <x>, <y>, <z>, "<name>")
-- Moves the existing marker+label if they exist for this player, or creates them.
function ItemSwap_OnPeerPosition(playerId, x, y, z, name)
    local key = tostring(playerId)
    local basePos = { x = tonumber(x), y = tonumber(y), z = tonumber(z) }
    if not basePos.x or not basePos.y or not basePos.z then return end
    name = (name and name ~= "") and name or ("Player " .. key)

    local markerPos = { x = basePos.x, y = basePos.y, z = basePos.z + ItemSwap.markerHeightOffset }
    local labelPos = { x = basePos.x, y = basePos.y, z = basePos.z + ItemSwap.labelHeightOffset }

    local rec = ItemSwap.peerMarkers[key]
    if rec then
        local markerEnt = System.GetEntityByName(rec.markerName)
        local labelEnt = System.GetEntityByName(rec.labelName)
        if markerEnt and labelEnt then
            pcall(function() markerEnt:SetWorldPos(markerPos) end)
            pcall(function() labelEnt:SetWorldPos(labelPos) end)
            return
        end
        -- One or both entities are gone (shouldn't normally happen - neither
        -- is a real pickable item) - fall through and respawn both.
        ItemSwap.peerMarkers[key] = nil
    end

    local markerName = "ItemSwap_Marker_" .. key
    local labelName = "ItemSwap_Label_" .. key
    -- Guard against orphans from a previous script load reusing these names.
    local staleMarker = System.GetEntityByName(markerName)
    if staleMarker then System.RemoveEntity(staleMarker.id) end
    local staleLabel = System.GetEntityByName(labelName)
    if staleLabel then System.RemoveEntity(staleLabel.id) end

    local marker = System.SpawnEntity({ class = "BasicEntity", name = markerName, position = markerPos })
    if marker then
        pcall(function() marker:SetAngles({ x = math.pi, y = 0, z = 0 }) end)  -- flip: tip points down at the peer
        pcall(function() marker:SetScale(ItemSwap.markerScale) end)
    end

    local label = System.SpawnEntity({ class = "Comment", name = labelName, position = labelPos, properties = {
        Text = name, fSize = ItemSwap.labelSize, bFixed = true, fMaxDist = 100,
    } })

    if marker and label then
        ItemSwap.peerMarkers[key] = { markerName = markerName, labelName = labelName }
    else
        System.LogAlways("[ITEMSWAP-ERR] OnPeerPosition failed to create marker/label for player " .. key)
    end
end

-- Called by the agent when a peer disconnects, so their marker doesn't sit
-- around forever representing someone no longer playing:
--   #ItemSwap_OnPeerLeft(<playerId>)
function ItemSwap_OnPeerLeft(playerId)
    local key = tostring(playerId)
    local rec = ItemSwap.peerMarkers[key]
    ItemSwap.peerMarkers[key] = nil
    if not rec then return end
    local markerEnt = System.GetEntityByName(rec.markerName)
    if markerEnt then pcall(function() System.RemoveEntity(markerEnt.id) end) end
    local labelEnt = System.GetEntityByName(rec.labelName)
    if labelEnt then pcall(function() System.RemoveEntity(labelEnt.id) end) end
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

    -- Milestone 2: piggyback the same tick for a low-rate position
    -- broadcast (~1.3Hz at the default 750ms interval) - deliberately not a
    -- separate, faster timer. This is exactly the design goal from the
    -- start: a coarse, infrequent position stream is enough for a presence
    -- marker and avoids anything like the reference project's continuous
    -- 50Hz stream that originally motivated keeping this mod's console/log
    -- output minimal.
    System.LogAlways(string.format("[ITEMSWAP-EVT] pos %.3f %.3f %.3f", pos.x, pos.y, pos.z))

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
