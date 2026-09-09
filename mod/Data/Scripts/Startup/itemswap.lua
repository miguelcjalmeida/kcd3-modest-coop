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
--   #ItemSwap_OnPeerDrop("<dropId>", "<cls>", <amount>, <health>, <x>, <y>, <z>)
--
-- Places the item at the DROPPER's own real world position, not near the
-- receiving player. Originally this landed near the receiver instead,
-- reasoning that "every player is on an independent single-player save, so
-- the sender's coordinates describe a position the receiver's game has
-- never loaded" - but Milestone 2's presence markers proved that reasoning
-- wrong: KCD2's open world is the same static, shared map for every save,
-- so a given (x, y, z) is the same physical location in everyone's game.
-- x/y/z default to the receiving player's own position if omitted or all
-- zero (an older agent build, or a synthetic test with no real coordinates).
--
-- dropId originates on the DROPPING player's side (minted in
-- ItemSwap_DetectTick) and rides the wire unchanged through the relay, so
-- every player who ends up with a ground copy of this same conceptual item
-- - the original dropper and every receiver - tracks it under the identical
-- id. That's what let's the claim watcher below correlate "who actually
-- picked this up" across independent worlds.
function ItemSwap_OnPeerDrop(dropId, cls, amount, health, x, y, z)
    amount = tonumber(amount) or 1
    health = tonumber(health) or 1.0

    x, y, z = tonumber(x), tonumber(y), tonumber(z)
    local spawnPos = nil
    if x and y and z and not (x == 0 and y == 0 and z == 0) then
        spawnPos = { x = x, y = y, z = z }
    else
        spawnPos = ItemSwap_PosInFrontOfPlayer(2.0)
    end
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
ItemSwap.peerBasePositions = {}  -- playerId (string key) -> {x,y,z}, the peer's raw reported position (no height offset, no bob)
ItemSwap.markerHeightOffset = 1.9  -- meters above the peer's reported (ground) position
ItemSwap.labelHeightOffset = 1.4   -- meters above the peer's reported position (just below the marker's tip)
ItemSwap.markerScale = 0.15
ItemSwap.labelSize = 6.0

-- Milestone 3 crouch state, keyed the same way as peerMarkers/peerBasePositions.
ItemSwap.peerCrouching = {}        -- key -> bool, latest known crouch state
ItemSwap.peerAnimPauseUntil = {}   -- key -> os.clock() timestamp; bob suppressed while os.clock() < this (math.huge while actively crouching)
ItemSwap.peerHeightTransition = {} -- key -> {startClock, fromOffset, toOffset}, or nil once finished
ItemSwap.crouchHeightReduction = 0.8   -- meters the marker sits lower while crouched
ItemSwap.crouchTransitionSec = 0.35    -- seconds to ease to the new height when crouch state changes
ItemSwap.crouchAnimCooldownSec = 10    -- seconds after standing back up before the bob resumes

-- Milestone 4: label enhancements (distance cutoff). HP itself is still
-- tracked here (ItemSwap.peerHealth) but no longer has its own world-space
-- sub-label under the marker - it moved to the F2 panel instead (Milestone 5).
ItemSwap.peerHealth = {}       -- key -> {cur, max}, latest reported HP for that peer
ItemSwap.peerLabelHidden = {}  -- key -> bool, whether this peer's labels are currently distance-hidden (tracked to skip redundant Hide() calls)
ItemSwap.peerNames = {}        -- key -> display name, kept for the F2 peer panel (not otherwise stored outside the label entity's own Text)
ItemSwap.labelMaxDistance = 300     -- meters from the local player beyond which a peer's labels (not their marker) are hidden

-- Milestone 7: [Dueling]/[Danger] panel tags.
ItemSwap.peerCombat = {}  -- key -> bool, latest reported IsInCombatMode() for that peer
ItemSwap.peerDanger = {}  -- key -> bool, latest reported IsInCombatDanger() for that peer
ItemSwap.peerCaught = {}   -- key -> bool, latest reported IsInTenseCircumstance() for that peer
ItemSwap.peerTalking = {}  -- key -> bool, latest reported IsInDialog() for that peer
ItemSwap.peerRiding = {}   -- key -> bool, latest reported IsMounted() for that peer
ItemSwap.peerPickpocketing = {}  -- key -> bool, latest reported IsPickpocketing() for that peer
ItemSwap.peerUnconscious = {}     -- key -> bool, latest reported IsUnconscious() for that peer
ItemSwap.peerDead = {}            -- key -> bool, latest reported IsDead() for that peer
ItemSwap.peerWanted = {}          -- key -> bool, latest reported IsPublicEnemy() for that peer
ItemSwap.peerArmed = {}           -- key -> bool, latest reported IsWeaponDrawn() for that peer
ItemSwap.peerCarryingCorpse = {}  -- key -> bool, latest reported IsCarryingCorpse() for that peer

System.SetCVar('cl_comment', 1)  -- required once: Comment entities no-op their per-frame draw otherwise

-- Starts (or redirects, if one is already in flight) a smooth height
-- transition for one peer's marker, and sets how long the bob stays
-- suppressed: indefinitely while crouching, or for crouchAnimCooldownSec
-- after standing back up. If a transition is already in progress, the new
-- one starts from wherever it currently is (not a hardcoded rest value),
-- so rapid crouch-toggling can't produce a visible pop.
function ItemSwap_BeginHeightTransition(key, wasCrouching, isCrouching)
    local restOffset = function(crouching)
        return crouching and (ItemSwap.markerHeightOffset - ItemSwap.crouchHeightReduction) or ItemSwap.markerHeightOffset
    end
    local fromOffset = restOffset(wasCrouching)
    local trans = ItemSwap.peerHeightTransition[key]
    if trans then
        local t = math.min(1.0, (os.clock() - trans.startClock) / ItemSwap.crouchTransitionSec)
        local eased = t * t * (3 - 2 * t)
        fromOffset = trans.fromOffset + (trans.toOffset - trans.fromOffset) * eased
    end
    ItemSwap.peerHeightTransition[key] = { startClock = os.clock(), fromOffset = fromOffset, toOffset = restOffset(isCrouching) }
    ItemSwap.peerAnimPauseUntil[key] = isCrouching and math.huge or (os.clock() + ItemSwap.crouchAnimCooldownSec)
end

-- Called by the agent (one-shot '#'-eval is fine, no timer involved) on
-- every position update relayed from a peer:
--   #ItemSwap_OnPeerPosition(<playerId>, <x>, <y>, <z>, "<name>", <isCrouching>, <curHp>, <maxHp>, <inCombat>, <inDanger>, <inTense>, <inDialog>, <inRiding>, <inPickpocketing>, <inUnconscious>, <inDead>, <inWanted>, <inArmed>, <inCarryingCorpse>)
-- Moves the existing marker+label if they exist for this player, or creates them.
--
-- Entirely wrapped in pcall: this runs as a raw one-shot RC eval with no
-- caller-side protection, so an uncaught error here (e.g. an entity API
-- behaving unexpectedly on someone's specific machine/game state) would
-- otherwise surface only as a raw Lua error the player has no reason to
-- notice, silently leaving their marker missing or stuck with no
-- indication why. Logs [ITEMSWAP-ERR] instead.
function ItemSwap_OnPeerPosition(playerId, x, y, z, name, isCrouching, curHp, maxHp, inCombat, inDanger, inTense, inDialog, inRiding, inPickpocketing, inUnconscious, inDead, inWanted, inArmed, inCarryingCorpse)
    local ok, err = pcall(ItemSwap_OnPeerPositionBody, playerId, x, y, z, name, isCrouching, curHp, maxHp, inCombat, inDanger, inTense, inDialog, inRiding, inPickpocketing, inUnconscious, inDead, inWanted, inArmed, inCarryingCorpse)
    if not ok then
        System.LogAlways("[ITEMSWAP-ERR] OnPeerPosition threw for player " .. tostring(playerId) .. ": " .. tostring(err))
    end
end

function ItemSwap_OnPeerPositionBody(playerId, x, y, z, name, isCrouching, curHp, maxHp, inCombat, inDanger, inTense, inDialog, inRiding, inPickpocketing, inUnconscious, inDead, inWanted, inArmed, inCarryingCorpse)
    local key = tostring(playerId)
    local basePos = { x = tonumber(x), y = tonumber(y), z = tonumber(z) }
    if not basePos.x or not basePos.y or not basePos.z then return end
    name = (name and name ~= "") and name or ("Player " .. key)
    isCrouching = isCrouching == true
    ItemSwap.peerNames[key] = name

    -- Store the peer's raw position and let ItemSwap_AnimTick (running on
    -- its own independent clock-driven loop) do the actual SetWorldPos,
    -- adding the bob offset on top each cycle. This is deliberate, not
    -- just simpler: it's what guarantees a position update can never reset
    -- the bob animation's phase - this function no longer touches the
    -- entity's transform at all once it exists, only the animation loop
    -- does, and that loop's phase is a pure function of elapsed real time.
    ItemSwap.peerBasePositions[key] = basePos

    -- A change in crouch state starts a smooth height transition and
    -- (re)sets the bob-pause window. First sighting of a peer adopts
    -- whatever state they're already in with no transition - nothing to
    -- animate from yet, and their marker is about to spawn fresh anyway.
    local wasCrouching = ItemSwap.peerCrouching[key]
    if wasCrouching == nil then wasCrouching = isCrouching end
    if isCrouching ~= wasCrouching then
        ItemSwap_BeginHeightTransition(key, wasCrouching, isCrouching)
    end
    ItemSwap.peerCrouching[key] = isCrouching

    local restHeightOffset = isCrouching and (ItemSwap.markerHeightOffset - ItemSwap.crouchHeightReduction) or ItemSwap.markerHeightOffset
    local markerPos = { x = basePos.x, y = basePos.y, z = basePos.z + restHeightOffset }
    local labelPos = { x = basePos.x, y = basePos.y, z = basePos.z + ItemSwap.labelHeightOffset }

    local rec = ItemSwap.peerMarkers[key]
    if rec then
        local markerEnt = System.GetEntityByName(rec.markerName)
        local labelEnt = System.GetEntityByName(rec.labelName)
        if not (markerEnt and labelEnt) then
            -- One or both entities are gone (shouldn't normally happen -
            -- neither is a real pickable item) - fall through and respawn
            -- both. The HP sub-label, if any, is left alone: it isn't part
            -- of this staleness check and gets its own respawn-on-change
            -- handling below regardless of what happens here.
            ItemSwap.peerMarkers[key] = nil
            rec = nil
        end
    end

    if not rec then
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

        -- fMaxDist=255 is Comment's own "always visible regardless of
        -- distance" sentinel (see its OnUpdate: >=255 short-circuits to
        -- alpha=1.0, never fading by distance at all) - our own
        -- labelMaxDistance/Hide() cutoff in ItemSwap_AnimTickBody is what
        -- actually governs visibility now, so the entity's native fade
        -- must be disabled or it would additionally (and much more
        -- aggressively, its slider tops out at 255) cull on its own.
        local label = System.SpawnEntity({ class = "Comment", name = labelName, position = labelPos, properties = {
            Text = name, fSize = ItemSwap.labelSize, bFixed = true, fMaxDist = 255,
        } })

        if marker and label then
            ItemSwap.peerMarkers[key] = { markerName = markerName, labelName = labelName }
            rec = ItemSwap.peerMarkers[key]
        else
            System.LogAlways("[ITEMSWAP-ERR] OnPeerPosition failed to create marker/label for player " .. key)
        end
    end

    -- HP is still tracked (the F2 peer panel reads it), just no longer
    -- rendered as its own world-space sub-label under the marker.
    -- curHp/maxHp of 0/0 (or unparseable) means "unknown".
    curHp = tonumber(curHp)
    maxHp = tonumber(maxHp)
    if curHp and maxHp and maxHp > 0 then
        ItemSwap.peerHealth[key] = { cur = curHp, max = maxHp }
    end

    ItemSwap.peerCombat[key] = inCombat == true
    ItemSwap.peerDanger[key] = inDanger == true
    ItemSwap.peerCaught[key] = inTense == true
    ItemSwap.peerTalking[key] = inDialog == true
    ItemSwap.peerRiding[key] = inRiding == true
    ItemSwap.peerPickpocketing[key] = inPickpocketing == true
    ItemSwap.peerUnconscious[key] = inUnconscious == true
    ItemSwap.peerDead[key] = inDead == true
    ItemSwap.peerWanted[key] = inWanted == true
    ItemSwap.peerArmed[key] = inArmed == true
    ItemSwap.peerCarryingCorpse[key] = inCarryingCorpse == true
end

-- Called by the agent when a peer disconnects, so their marker doesn't sit
-- around forever representing someone no longer playing:
--   #ItemSwap_OnPeerLeft(<playerId>)
function ItemSwap_OnPeerLeft(playerId)
    local key = tostring(playerId)
    local rec = ItemSwap.peerMarkers[key]
    ItemSwap.peerMarkers[key] = nil
    ItemSwap.peerBasePositions[key] = nil
    ItemSwap.peerCrouching[key] = nil
    ItemSwap.peerAnimPauseUntil[key] = nil
    ItemSwap.peerHeightTransition[key] = nil
    ItemSwap.peerHealth[key] = nil
    ItemSwap.peerLabelHidden[key] = nil
    ItemSwap.peerNames[key] = nil
    ItemSwap.peerCombat[key] = nil
    ItemSwap.peerDanger[key] = nil
    ItemSwap.peerCaught[key] = nil
    ItemSwap.peerTalking[key] = nil
    ItemSwap.peerRiding[key] = nil
    ItemSwap.peerPickpocketing[key] = nil
    ItemSwap.peerUnconscious[key] = nil
    ItemSwap.peerDead[key] = nil
    ItemSwap.peerWanted[key] = nil
    ItemSwap.peerArmed[key] = nil
    ItemSwap.peerCarryingCorpse[key] = nil
    if not rec then return end
    local markerEnt = System.GetEntityByName(rec.markerName)
    if markerEnt then pcall(function() System.RemoveEntity(markerEnt.id) end) end
    local labelEnt = System.GetEntityByName(rec.labelName)
    if labelEnt then pcall(function() System.RemoveEntity(labelEnt.id) end) end
end

-- ===== Milestone 3: marker idle animation =====
-- A gentle, continuous up/down bob on every peer marker+label, independent
-- of position updates: this loop is the ONLY thing that ever calls
-- SetWorldPos on a marker/label once it exists (ItemSwap_OnPeerPosition
-- just records the peer's latest raw position and returns). The bob's
-- phase comes from a single elapsed-time counter started once in
-- ItemSwap_AnimOn, never touched by anything else - so receiving a fresh
-- position, no matter how often, cannot restart or desync the animation.
ItemSwap.animRunning = false
ItemSwap.animIntervalMs = 33     -- ~30Hz: smooth for a slow bob without being wasteful
ItemSwap.animAmplitude = 0.084375  -- meters of vertical travel each way - "just a bit" (25% less again, from 0.1125)
ItemSwap.animPeriodSec = 2.0     -- seconds for one full up-down-up cycle
ItemSwap.animStartClock = nil    -- os.clock() reference point captured once in ItemSwap_AnimOn

function ItemSwap_AnimTick()
    if not ItemSwap.animRunning then return end
    Script.SetTimer(ItemSwap.animIntervalMs, ItemSwap_AnimTick)  -- reschedule first, same reasoning as ItemSwap_DetectTick
    local ok, err = pcall(ItemSwap_AnimTickBody)
    if not ok then
        System.LogAlways("[ITEMSWAP-ERR] AnimTick failed (loop kept alive): " .. tostring(err))
    end
end

function ItemSwap_AnimTickBody()
    local elapsed = os.clock() - ItemSwap.animStartClock
    local phase = (elapsed / ItemSwap.animPeriodSec) * 2 * math.pi
    local bob = math.sin(phase) * ItemSwap.animAmplitude
    local now = os.clock()

    -- Read once per tick, not once per peer: distance culling below only
    -- needs it for comparison, and a failed read (nil) just means "don't
    -- cull this tick" rather than hiding every label.
    local localPos = nil
    if player then pcall(function() localPos = player:GetWorldPos() end) end
    local maxDistSq = ItemSwap.labelMaxDistance * ItemSwap.labelMaxDistance

    for key, rec in pairs(ItemSwap.peerMarkers) do
        local base = ItemSwap.peerBasePositions[key]
        if base then
            -- Resolve this peer's current marker height: mid-transition
            -- (crouch just started or ended) uses an eased blend toward the
            -- new resting value; otherwise it's already at rest.
            local heightOffset
            local trans = ItemSwap.peerHeightTransition[key]
            if trans then
                local t = (now - trans.startClock) / ItemSwap.crouchTransitionSec
                if t >= 1.0 then
                    heightOffset = trans.toOffset
                    ItemSwap.peerHeightTransition[key] = nil
                else
                    local eased = t * t * (3 - 2 * t)  -- smoothstep: gentler than linear
                    heightOffset = trans.fromOffset + (trans.toOffset - trans.fromOffset) * eased
                end
            else
                local isCrouching = ItemSwap.peerCrouching[key]
                heightOffset = isCrouching and (ItemSwap.markerHeightOffset - ItemSwap.crouchHeightReduction) or ItemSwap.markerHeightOffset
            end

            -- Bob is suppressed while crouching, and for crouchAnimCooldownSec
            -- after standing back up (peerAnimPauseUntil is set in
            -- ItemSwap_BeginHeightTransition, never touched here).
            local pauseUntil = ItemSwap.peerAnimPauseUntil[key] or 0
            local effectiveBob = (now < pauseUntil) and 0 or bob

            local markerEnt = System.GetEntityByName(rec.markerName)
            if markerEnt then
                pcall(function()
                    markerEnt:SetWorldPos({ x = base.x, y = base.y, z = base.z + heightOffset + effectiveBob })
                end)
            end
            -- Label deliberately excludes `bob` and stays at its own fixed
            -- height (unaffected by crouch) - it still tracks the peer's
            -- real X/Y/Z every tick, just without the marker's vertical
            -- games, so the name stays readable/steady regardless.
            local labelEnt = System.GetEntityByName(rec.labelName)

            -- Distance-based visibility: only the name label is culled,
            -- never the marker - a marker with no readable label at long
            -- range is still a useful "someone is over there" signal.
            -- Hide() is only called on an actual state change, not every
            -- tick, since it's a real entity-flag write, not a cheap read.
            local hidden = false
            if localPos then
                local dx, dy, dz = base.x - localPos.x, base.y - localPos.y, base.z - localPos.z
                hidden = (dx * dx + dy * dy + dz * dz) > maxDistSq
            end
            if hidden ~= ItemSwap.peerLabelHidden[key] then
                ItemSwap.peerLabelHidden[key] = hidden
                -- Hide(bool) is unreliable here - Hide(false) was confirmed
                -- live to leave IsHidden() stuck true once hidden, never
                -- un-hiding the entity again. Hide(<number>) (0/1) was
                -- confirmed live to work symmetrically both ways, so that's
                -- what's used - not a style choice, a required workaround.
                local hideArg = hidden and 1 or 0
                if labelEnt then pcall(function() labelEnt:Hide(hideArg) end) end
            end

            if labelEnt then
                pcall(function()
                    labelEnt:SetWorldPos({ x = base.x, y = base.y, z = base.z + ItemSwap.labelHeightOffset })
                end)
            end
        end
    end
end

-- Started by the agent the same way as itemswap_detect_on: sent as a bare
-- console command (never a '#'-eval), the only form that reliably starts a
-- Script.SetTimer chain - see the note near itemswap_detect_on below.
function ItemSwap_AnimOn()
    if ItemSwap.animRunning then return end
    ItemSwap.animRunning = true
    ItemSwap.animStartClock = os.clock()
    System.LogAlways("[ITEMSWAP] marker animation ON")
    Script.SetTimer(ItemSwap.animIntervalMs, ItemSwap_AnimTick)
end

function ItemSwap_AnimOff()
    ItemSwap.animRunning = false
    System.LogAlways("[ITEMSWAP] marker animation OFF")
end

System.AddCCommand("itemswap_anim_on", "ItemSwap_AnimOn()", "ItemSwap Milestone 3: start marker bob animation")
System.AddCCommand("itemswap_anim_off", "ItemSwap_AnimOff()", "ItemSwap Milestone 3: stop marker bob animation")

-- ===== Milestone 5: F2 connected-peers panel =====
-- A simple on-screen text panel, toggled by F2, listing every connected
-- peer and their distance from the local player. Two APIs make this
-- possible, both confirmed live (neither had been used anywhere in this
-- mod before):
--   - System.DrawText(x, y, text, size) - 2D screen-space text, immediate
--     mode (redrawn every frame it should appear, same as System.DrawLabel
--     under the Comment entities used elsewhere in this file). Confirmed
--     via the reference project's own documented usage, then confirmed
--     live in this build.
--   - System.ExecuteCommand("bind f2 <command>") - binds a raw keypress
--     straight to a registered console command, no action-map XML needed.
--     Confirmed live: bound F6 to itemswap_start as a throwaway test and
--     pressing it fired the command (F2 was chosen for the real feature -
--     see below - but the bind mechanism itself was proven on F6/F7/F8
--     first). Both F2 and F6 are doubly confirmed safe to claim - unbound
--     in this game's own default keybind config, AND the reference
--     project's own notes record every F-key as individually live-tested
--     before use, flagging F1/F3/F10 as hardcoded debug traps invisible to
--     config inspection alone. Neither F2 nor F6 was one of them; F2 was
--     picked over F6 purely on request, after F6 had already been proven
--     to work.
--     One real quirk found live: rebinding an already-bound key silently
--     does nothing (even after an explicit unbind) - the key sticks to
--     whichever command it was FIRST bound to in the session. Binding a
--     previously-untouched key works cleanly every time. Not a problem for
--     normal play (this Startup code only ever binds F2 once per session),
--     only bit us during our own live testing when we rebound the same key
--     more than once.
--
-- System.DrawText is genuinely one-frame-only immediate-mode - unlike the
-- Comment-entity technique used elsewhere in this file, which gets real
-- per-frame callbacks from the engine's own entity update system, a
-- Script.SetTimer loop is never truly frame-locked, so a slow enough
-- interval visibly flickers. Confirmed live: 16ms and 8ms both flickered
-- noticeably; 4ms (close to the interval between individual frames at a
-- high frame rate) looked clean. Cheap enough given this only runs while
-- the panel is actually open.
ItemSwap.panelOpen = false
ItemSwap.panelIntervalMs = 4  -- tuned live: 16ms and 8ms both still flickered visibly, 4ms looked clean

function ItemSwap_PanelTick()
    if not ItemSwap.panelOpen then return end  -- stops the chain entirely while closed; toggling back on restarts it
    Script.SetTimer(ItemSwap.panelIntervalMs, ItemSwap_PanelTick)  -- reschedule first, same reasoning as every other timer loop here
    local ok, err = pcall(ItemSwap_PanelTickBody)
    if not ok then
        System.LogAlways("[ITEMSWAP-ERR] PanelTick failed (loop kept alive): " .. tostring(err))
    end
end

-- A cheap outline effect (black text offset 1px in each direction, then
-- colored text on top) standing in for a real background box: no rect-
-- drawing primitive exists in this build for that (confirmed live:
-- Draw2dImage/DrawScreenQuad/DrawQuad/Draw2dRect are all absent). DrawText
-- DOES take optional r,g,b,a (0-1 range) beyond the base (x,y,text,size)
-- signature the reference project used - confirmed live with a red
-- on-screen test - which is what makes this outline (and HP coloring)
-- possible at all. Deliberately a plain global, not `local function`: a
-- `local` here only lives for the one RC eval chunk that defines it - bit
-- us live when a later patch redefining ItemSwap_PanelTickBody alone
-- couldn't see it anymore and errored on every tick.
function ItemSwap_DrawTextOutlined(x, y, text, size, r, g, b)
    r, g, b = r or 1, g or 1, b or 1
    System.DrawText(x - 1, y, text, size, 0, 0, 0, 1)
    System.DrawText(x + 1, y, text, size, 0, 0, 0, 1)
    System.DrawText(x, y - 1, text, size, 0, 0, 0, 1)
    System.DrawText(x, y + 1, text, size, 0, 0, 0, 1)
    System.DrawText(x, y, text, size, r, g, b, 1)
end

-- 8-way compass bearing of (dx, dz2) - the peer's offset from the local
-- player in the X/Y world plane - matching this game's own top-down compass
-- convention (Y+ = North, X+ = East), confirmed live: a peer placed at a
-- pure +X offset from the local player showed as due East.
function ItemSwap_CardinalDirection(dx, dz2)
    local bearingDeg = math.deg(math.atan2(dx, dz2))
    if bearingDeg < 0 then bearingDeg = bearingDeg + 360 end
    local dirs = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }
    local idx = math.floor((bearingDeg + 22.5) / 45) % 8 + 1
    return dirs[idx]
end

function ItemSwap_PanelTickBody()
    local localPos = nil
    if player then pcall(function() localPos = player:GetWorldPos() end) end
    if not localPos then return end

    -- titleGap/hpGap/blockGap are all separately tuned live: the title's
    -- much bigger font needs more headroom than a peer's own two-line
    -- block does, and the block-to-block gap needs to read as a clear
    -- separator between different peers, not just another line within one.
    local x, y, titleGap, hpGap, blockGap = 10, 20, 42, 22, 30
    ItemSwap_DrawTextOutlined(x, y, "Connected peers (F2)", 3.6)
    y = y + titleGap

    local any = false
    for key, base in pairs(ItemSwap.peerBasePositions) do
        any = true
        local name = ItemSwap.peerNames[key] or ("Player " .. key)
        local dx, dz2, dz = base.x - localPos.x, base.y - localPos.y, base.z - localPos.z
        local dist = math.sqrt(dx * dx + dz2 * dz2 + dz * dz)
        local dir = ItemSwap_CardinalDirection(dx, dz2)
        ItemSwap_DrawTextOutlined(x, y, string.format("%s - %.0fm %s", name, dist, dir), 2.4)
        y = y + hpGap

        local health = ItemSwap.peerHealth[key]
        local hpText = health and string.format("HP: %d/%d", math.floor(health.cur + 0.5), math.floor(health.max + 0.5)) or "HP: ?/?"
        -- Unknown health (no `health` yet) defaults to the green branch -
        -- "assume fine until told otherwise" reads better than alarming
        -- red for a peer we simply haven't heard from yet.
        local frac = (health and health.max > 0) and (health.cur / health.max) or 1
        local hr, hg, hb
        if frac > 0.4 then hr, hg, hb = 0.5, 1, 0.5 else hr, hg, hb = 1, 0.5, 0.5 end
        ItemSwap_DrawTextOutlined(x, y, hpText, 2.0, hr, hg, hb)
        y = y + hpGap

        -- Optional third line, only drawn when there's something to say -
        -- most peers most of the time are fighting nothing, and an empty
        -- line for every single one would just be clutter.
        local combat = ItemSwap.peerCombat[key]
        local danger = ItemSwap.peerDanger[key]
        local caught = ItemSwap.peerCaught[key]
        local talking = ItemSwap.peerTalking[key]
        local riding = ItemSwap.peerRiding[key]
        local pickpocketing = ItemSwap.peerPickpocketing[key]
        local unconscious = ItemSwap.peerUnconscious[key]
        local dead = ItemSwap.peerDead[key]
        local wanted = ItemSwap.peerWanted[key]
        local armed = ItemSwap.peerArmed[key]
        local carryingCorpse = ItemSwap.peerCarryingCorpse[key]
        if combat or danger or caught or talking or riding or pickpocketing
            or unconscious or dead or wanted or armed or carryingCorpse then
            local tags = {}
            if dead then tags[#tags + 1] = "[Dead]" end
            if unconscious then tags[#tags + 1] = "[Unconscious]" end
            if combat then tags[#tags + 1] = "[Dueling]" end
            if danger then tags[#tags + 1] = "[Danger]" end
            if caught then tags[#tags + 1] = "[Caught]" end
            if wanted then tags[#tags + 1] = "[Wanted]" end
            if talking then tags[#tags + 1] = "[Talking]" end
            if riding then tags[#tags + 1] = "[Riding]" end
            if pickpocketing then tags[#tags + 1] = "[Pickpocketing]" end
            if armed then tags[#tags + 1] = "[Armed]" end
            if carryingCorpse then tags[#tags + 1] = "[Burying]" end
            -- Priority, most to least urgent: [Dead] (somber grey - already
            -- happened, alarm doesn't help) > [Unconscious] (deep orange-red,
            -- knocked out) > [Caught] (a pursuer has actually spotted the
            -- peer, harsher red than the general orange/danger tint) >
            -- [Pickpocketing] (own distinct yellow, getting caught
            -- red-handed ends badly). Everything else not already covered
            -- by the general orange/danger tint - [Talking], [Riding],
            -- [Armed], [Burying] alone - gets a calm blue instead of
            -- the alarming palette, since none of those alone is a warning.
            local tr, tg, tb = 1, 0.6, 0.2
            if dead then
                tr, tg, tb = 0.6, 0.6, 0.6
            elseif unconscious then
                tr, tg, tb = 1, 0.4, 0
            elseif caught then
                tr, tg, tb = 1, 0.15, 0.15
            elseif pickpocketing then
                tr, tg, tb = 1, 0.9, 0.2
            elseif (talking or riding or armed or carryingCorpse) and not (combat or danger or wanted) then
                tr, tg, tb = 0.4, 0.8, 1
            end
            ItemSwap_DrawTextOutlined(x, y, table.concat(tags, " "), 2.0, tr, tg, tb)
            y = y + hpGap
        end

        y = y + (blockGap - hpGap)
    end
    if not any then
        ItemSwap_DrawTextOutlined(x, y, "(no peers connected)", 2.4)
    end
end

function ItemSwap_PanelToggle()
    ItemSwap.panelOpen = not ItemSwap.panelOpen
    if ItemSwap.panelOpen then
        System.LogAlways("[ITEMSWAP] panel ON")
        Script.SetTimer(ItemSwap.panelIntervalMs, ItemSwap_PanelTick)
    else
        System.LogAlways("[ITEMSWAP] panel OFF")
    end
end

System.AddCCommand("itemswap_panel_toggle", "ItemSwap_PanelToggle()", "ItemSwap Milestone 5: toggle the F2 connected-peers panel")

-- Bound once per Startup execution (i.e. once per session/level load, same
-- as everything else re-armed by itemswap_start) - re-binding is harmless
-- and cheap, so no guard against doing it more than once.
pcall(function() System.ExecuteCommand("bind f2 itemswap_panel_toggle") end)

-- Milestone 8: teleport to whichever connected peer the player is looking at.
--
-- player:SetWorldPos() confirmed live to be safe for the PLAYER entity, not
-- just decoration entities like markers - but ONLY when given a real
-- ground-truth Z. A first live test used the player's own current Z offset
-- by 800m in X/Y and dropped the player under the terrain, stuck looking up
-- - the destination's actual ground height was never consulted. The fix,
-- also confirmed live: a peer's broadcast (x, y, z) is always a genuine
-- GetWorldPos() reading from an actual live character standing there, so
-- teleporting to a peer's last-known position (not a guessed offset) is
-- safe by construction. Also confirmed live: an instant multi-hundred-meter
-- jump itself is fine - streaming/collision caught up correctly once Z
-- was correct; no PostPhysicalize() or similar workaround was needed.
--
-- player.actor:GetHeadDir() confirmed live (twice, once pre-death and once
-- post-respawn) to return a real normalized look-direction vector that
-- genuinely changes as the player turns - not a stuck/cached value. Peer
-- selection is by dot product between that direction and the vector to
-- each peer: the peer most in front of the player wins, so long as at
-- least one is within a generous ~60-degree cone (dot > 0.5) - otherwise
-- this is a no-op rather than surprising the player with a teleport to
-- someone behind them just because no one else is connected.
-- Cooldown is formula-based, not flat: 1 second per teleportSpeedMetersPerSec
-- of distance actually skipped, so fast travel compresses the walk into an
-- instant jump but still "costs" roughly the time that walk would have
-- taken - the user's own call, after measuring their real in-game movement
-- speed live from logged position samples (~4.1 m/s). A short floor still
-- applies for very close jumps, doubling as the anti-spam guard for a held
-- key firing this command every frame (confirmed live testing the Q bind).
ItemSwap.teleportSpeedMetersPerSec = 4.1
ItemSwap.teleportCooldownFloorSec = 3.0
ItemSwap.lastTeleportClock = nil  -- nil (not 0) until the first real teleport, so the cooldown display doesn't show a false "on cooldown" state right after load
ItemSwap.lastTeleportCooldownSec = nil  -- this jump's own cooldown duration, since it now varies per-jump rather than being one fixed constant
ItemSwap.teleportLookDotThreshold = 0.5  -- ~60 degree cone around where the player is looking

function ItemSwap_TeleportToLookedAtPeer()
    local ok, err = pcall(ItemSwap_TeleportToLookedAtPeerBody)
    if not ok then
        System.LogAlways("[ITEMSWAP-ERR] TeleportToLookedAtPeer threw: " .. tostring(err))
    end
end

function ItemSwap_TeleportToLookedAtPeerBody()
    local now = os.clock()
    if ItemSwap.lastTeleportClock and now - ItemSwap.lastTeleportClock < (ItemSwap.lastTeleportCooldownSec or 0) then return end

    if not player or not player.actor then return end
    local localPos, headDir = nil, nil
    pcall(function() localPos = player:GetWorldPos() end)
    pcall(function() headDir = player.actor:GetHeadDir() end)
    if not localPos or not headDir then return end

    local bestKey, bestPos, bestDot, bestDist = nil, nil, nil, nil
    for key, base in pairs(ItemSwap.peerBasePositions) do
        local dx, dy, dz = base.x - localPos.x, base.y - localPos.y, base.z - localPos.z
        local dist = math.sqrt(dx * dx + dy * dy + dz * dz)
        if dist > 0 then
            local dot = (dx / dist) * headDir.x + (dy / dist) * headDir.y + (dz / dist) * headDir.z
            if dot >= ItemSwap.teleportLookDotThreshold and (not bestDot or dot > bestDot) then
                bestKey, bestPos, bestDot, bestDist = key, base, dot, dist
            end
        end
    end

    if not bestPos then
        System.LogAlways("[ITEMSWAP] teleport: not looking at any connected peer")
        return
    end

    ItemSwap.lastTeleportClock = now
    ItemSwap.lastTeleportCooldownSec = math.max(ItemSwap.teleportCooldownFloorSec, bestDist / ItemSwap.teleportSpeedMetersPerSec)
    player:SetWorldPos({ x = bestPos.x, y = bestPos.y, z = bestPos.z })
    local name = ItemSwap.peerNames[bestKey] or ("Player " .. bestKey)
    System.LogAlways("[ITEMSWAP] teleported to " .. name .. string.format(" (was %.0fm away, cooldown %.0fs)", bestDist, ItemSwap.lastTeleportCooldownSec))
end

System.AddCCommand("itemswap_teleport_looked_at", "ItemSwap_TeleportToLookedAtPeer()", "ItemSwap Milestone 8: teleport to the connected peer the player is looking at")
pcall(function() System.ExecuteCommand("bind q itemswap_teleport_looked_at") end)

-- Always-on top-right countdown while the teleport is on cooldown -
-- deliberately independent of the F2 panel (should stay visible whether or
-- not that's open), so it's its own tiny tick loop, armed the same way as
-- everything else itemswap_start starts.
ItemSwap.cooldownDisplayIntervalMs = 4  -- same as the F2 panel: 16ms/8ms both flickered visibly in that earlier tuning, 4ms didn't
ItemSwap.cooldownDisplayRunning = false

function ItemSwap_CooldownDisplayOn()
    if ItemSwap.cooldownDisplayRunning then return end
    ItemSwap.cooldownDisplayRunning = true
    Script.SetTimer(ItemSwap.cooldownDisplayIntervalMs, ItemSwap_CooldownDisplayTick)
end

function ItemSwap_CooldownDisplayOff()
    ItemSwap.cooldownDisplayRunning = false
end

function ItemSwap_CooldownDisplayTick()
    if not ItemSwap.cooldownDisplayRunning then return end
    Script.SetTimer(ItemSwap.cooldownDisplayIntervalMs, ItemSwap_CooldownDisplayTick)  -- reschedule first, same reasoning as every other timer loop here
    local ok, err = pcall(ItemSwap_CooldownDisplayTickBody)
    if not ok then
        System.LogAlways("[ITEMSWAP-ERR] CooldownDisplayTick failed (loop kept alive): " .. tostring(err))
    end
end

function ItemSwap_CooldownDisplayTickBody()
    if not ItemSwap.lastTeleportClock then return end  -- never teleported yet this session
    local remaining = (ItemSwap.lastTeleportCooldownSec or 0) - (os.clock() - ItemSwap.lastTeleportClock)
    if remaining <= 0 then return end  -- off cooldown - nothing to draw

    -- r_Width read fresh each tick rather than cached: cheap CVar lookup,
    -- and correct even if a player changes resolution mid-session.
    local screenW = tonumber(System.GetCVar('r_Width')) or 1920
    local text = string.format("Fast travel: %ds", math.ceil(remaining))
    ItemSwap_DrawTextOutlined(screenW - 260, 20, text, 2.0, 1, 0.8, 0.3)
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
ItemSwap.detectIntervalMs = 250
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
-- claim watcher only polls every detectIntervalMs (250ms default), so a
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

-- No official "IsCrouching" API was found on player/player.actor/player.human
-- (confirmed live: GetStance, IsCrouching, GetPhysicalizationProfile all
-- either don't exist or don't reflect stance - GetPhysicalizationProfile
-- returns "alive", a life-state, not a pose). Detected instead via a real
-- physical signal: player.actor:GetHeadPos() drops by ~0.53m while
-- crouching (confirmed live: standing ~1.57m above the feet position,
-- crouching ~1.04m - matches BasicActor.lua's own stance table, where
-- crouch's viewOffset.z is 1.1 against normal/combat's 1.6). 1.3 sits
-- comfortably between the two.
function ItemSwap_IsLocalPlayerCrouching()
    if not player or not player.actor then return false end
    local ok, headPos = pcall(function() return player.actor:GetHeadPos() end)
    if not ok or not headPos then return false end
    local feetPos = nil
    pcall(function() feetPos = player:GetWorldPos() end)
    if not feetPos then return false end
    return (headPos.z - feetPos.z) < 1.3
end

-- Polling, not an event: no HP-change callback exists on player/player.actor
-- in this build (same conclusion the more thoroughly-researched reference
-- project reached - it polls GetHealth() every tick too, no hook found).
-- GetMaxHealth() exists alongside it (confirmed live: both currently read
-- 100/100) - Vitality perks can raise a player's max above the default 100,
-- so it can't be assumed constant and sent as a fixed number.
function ItemSwap_GetLocalPlayerHealth()
    if not player or not player.actor then return 0, 0 end
    local ok1, cur = pcall(function() return player.actor:GetHealth() end)
    local ok2, max = pcall(function() return player.actor:GetMaxHealth() end)
    if not ok1 or not ok2 or type(cur) ~= "number" or type(max) ~= "number" then return 0, 0 end
    return cur, max
end

-- Both confirmed live against a real sparring match, and confirmed to be
-- genuinely DIFFERENT signals, not aliases: IsInCombatMode() tracks
-- momentary active engagement (flips false the instant you turn away from
-- an opponent), IsInCombatDanger() stays true for the whole encounter
-- regardless of facing. [Dueling] uses the former, [Danger] the latter.
--
-- IsInTenseCircumstance() ([Caught]) was found by the user, not guessed:
-- this game's own HUD shows a rabbit icon while a pursuer is searching for
-- the player (white), an intermediate state while investigating (yellow -
-- confirmed live no general API exists for this one; it was also too brief
-- to reliably poll even if it did), and two rabbits fighting the instant a
-- pursuer actually spots the player for real, which is exactly when this
-- flips true. AI.GetAlertness/GetGroupAlertness(player.id) were tried for
-- the yellow state and confirmed live to read 0 throughout all three -
-- alertness is a property of the pursuing NPC, not the player, so querying
-- it from the player's own id was the wrong angle; not pursued further.
-- InDialog ([Talking]) is read here too, despite the function's name being
-- about combat - confirmed live to correctly flip true/false around a real
-- NPC conversation, and piggybacking it onto this same poll/emit avoids a
-- second near-identical function and a second call at the DetectTickBody
-- site for what's fundamentally the same kind of "peer status" data.
--
-- InRiding ([Riding], player.human:IsMounted()) and InPickpocketing
-- ([Pickpocketing], player.human:IsPickpocketing()) are read here for the
-- same reason - both confirmed live to be real booleans. A third candidate,
-- IsOnLadder() for a [Climbing] tag, was tested and found to return a
-- number (0) rather than a real boolean, unlike every other flag here -
-- deliberately left out rather than special-cased, per the user's call.
--
-- InUnconscious (player.actor:IsUnconscious()), InDead (player.actor:IsDead()),
-- InWanted (player.soul:IsPublicEnemy()), InArmed (player.human:IsWeaponDrawn()),
-- and InCarryingCorpse (player.actor:IsCarryingCorpse()) round out the panel
-- tags - all five individually type-checked live as real booleans before
-- being wired up here, same discipline as every flag above.
function ItemSwap_GetLocalCombatState()
    if not player or not player.soul or not player.human or not player.actor then
        return false, false, false, false, false, false, false, false, false, false, false
    end
    local ok1, mode = pcall(function() return player.soul:IsInCombatMode() end)
    local ok2, danger = pcall(function() return player.soul:IsInCombatDanger() end)
    local ok3, tense = pcall(function() return player.soul:IsInTenseCircumstance() end)
    local ok4, dialog = pcall(function() return player.human:IsInDialog() end)
    local ok5, mounted = pcall(function() return player.human:IsMounted() end)
    local ok6, pickpocketing = pcall(function() return player.human:IsPickpocketing() end)
    local ok7, unconscious = pcall(function() return player.actor:IsUnconscious() end)
    local ok8, dead = pcall(function() return player.actor:IsDead() end)
    local ok9, wanted = pcall(function() return player.soul:IsPublicEnemy() end)
    local ok10, armed = pcall(function() return player.human:IsWeaponDrawn() end)
    local ok11, carryingCorpse = pcall(function() return player.actor:IsCarryingCorpse() end)
    return (ok1 and mode == true), (ok2 and danger == true), (ok3 and tense == true), (ok4 and dialog == true),
        (ok5 and mounted == true), (ok6 and pickpocketing == true),
        (ok7 and unconscious == true), (ok8 and dead == true), (ok9 and wanted == true),
        (ok10 and armed == true), (ok11 and carryingCorpse == true)
end

function ItemSwap_DetectTick()
    if not ItemSwap.detectRunning then return end
    Script.SetTimer(ItemSwap.detectIntervalMs, ItemSwap_DetectTick)  -- reschedule first: belt-and-braces alongside the pcall below

    -- The whole body is wrapped in pcall, not just individual risky calls.
    -- Confirmed live (2026-09-07): an uncaught error here doesn't just skip
    -- this one tick and let the next scheduled call proceed as the
    -- "reschedule first" comment above assumed - it silently kills the
    -- entire Script.SetTimer chain outright, with nothing in kcd.log to
    -- explain why position/drop events just stop forever until something
    -- external (a manual `#ItemSwap_DetectTick()` call) kicks it again.
    -- That's not acceptable for something players depend on without any
    -- debugging access of their own, so nothing inside this function may
    -- ever be allowed to throw uncaught.
    local ok, tickErr = pcall(ItemSwap_DetectTickBody)
    if not ok then
        System.LogAlways("[ITEMSWAP-ERR] DetectTick failed (loop kept alive): " .. tostring(tickErr))
    end
end

function ItemSwap_DetectTickBody()
    if not player then return end
    local pos = nil
    pcall(function() pos = player:GetWorldPos() end)
    if not pos then return end

    -- Milestone 2: piggyback the same tick for a low-rate position
    -- broadcast (4Hz at the default 250ms interval) - deliberately not a
    -- separate, faster timer. This is exactly the design goal from the
    -- start: a coarse, infrequent position stream is enough for a presence
    -- marker and avoids anything like the reference project's continuous
    -- 50Hz stream that originally motivated keeping this mod's console/log
    -- output minimal.
    local crouching = ItemSwap_IsLocalPlayerCrouching()
    local curHp, maxHp = ItemSwap_GetLocalPlayerHealth()
    local inCombat, inDanger, inTense, inDialog, inRiding, inPickpocketing,
        inUnconscious, inDead, inWanted, inArmed, inCarryingCorpse = ItemSwap_GetLocalCombatState()
    System.LogAlways(string.format("[ITEMSWAP-EVT] pos %.3f %.3f %.3f %d %.1f %.1f %d %d %d %d %d %d %d %d %d %d %d",
        pos.x, pos.y, pos.z, crouching and 1 or 0, curHp, maxHp, inCombat and 1 or 0, inDanger and 1 or 0, inTense and 1 or 0, inDialog and 1 or 0,
        inRiding and 1 or 0, inPickpocketing and 1 or 0,
        inUnconscious and 1 or 0, inDead and 1 or 0, inWanted and 1 or 0, inArmed and 1 or 0, inCarryingCorpse and 1 or 0))

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
    -- in the same tick) could get attributed to the new item,
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

-- Single entry point for everything the mod needs armed each session - the
-- agent's own auto-arm (on seeing ITEMSWAP-LOADED) is unreliable in
-- practice (RC isn't always up yet at that exact moment), so this exists
-- as a one-command manual fallback: type it once and everything the mod
-- needs is running.
--
-- Off-then-wait-then-on, not just straight to On: the user's own report was
-- that position broadcasting sometimes silently stops for their friends
-- over a long session, and the existing fix has always been running
-- itemswap_*_off then itemswap_*_on by hand. Going straight to On alone
-- assumes each loop's Running flag is false already - but if a previous
-- Script.SetTimer chain died silently (the same "uncaught error kills the
-- whole chain with nothing logged" failure mode already documented for
-- ItemSwap_DetectTick) while its Running flag stayed stuck true from
-- before, On() would see that flag, no-op, and leave the mod dark with no
-- indication why. Off() unconditionally clears the flag first regardless
-- of whatever state it was actually in, so On() is guaranteed to actually
-- schedule a fresh Script.SetTimer chain rather than trusting stale state.
-- The 1.5s gap is just so a chain from the previous SetTimer generation
-- has time to naturally stop rescheduling itself before a new one starts,
-- rather than two overlapping generations both alive briefly.
function ItemSwap_Start()
    ItemSwap_DetectOff()
    ItemSwap_AnimOff()
    ItemSwap_CooldownDisplayOff()
    System.LogAlways("[ITEMSWAP] itemswap_start: stopped everything, rearming in 1.5s")
    Script.SetTimer(1500, ItemSwap_StartOnPart)
end

function ItemSwap_StartOnPart()
    ItemSwap_DetectOn()
    ItemSwap_AnimOn()
    ItemSwap_CooldownDisplayOn()
    System.LogAlways("[ITEMSWAP] itemswap_start: rearmed")
end

System.AddCCommand("itemswap_start", "ItemSwap_Start()",
    "ItemSwap: start everything the mod needs (drop detector + marker animation)")

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
