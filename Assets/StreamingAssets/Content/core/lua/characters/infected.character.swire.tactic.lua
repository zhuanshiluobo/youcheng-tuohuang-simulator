-- 规则阶段只存在权威Effect续接元数据中；不保留Lua VM或UI草稿。
local function copy(value)
    if type(value) ~= 'table' then return value end
    local result = {}; for key, item in pairs(value) do result[key] = copy(item) end; return result
end
local function state(ctx)
    if ctx.eventType == 'EffectCompleted' then
        if ctx.payload.continuationContext == nil or ctx.payload.outcome ~= 'completed' then return nil end
        return copy(ctx.payload.continuationContext)
    end
    local source = ctx.payload.cardInstanceRef or ctx.payload.sourceItemId or ctx.payload.enterpriseItemId
    if source == nil then error('content_source_missing') end
    return { source = source, stage = 'start' }
end
local function tag(effect, context, stage)
    local saved = copy(context); saved.stage = stage; return Effect.WithContext(effect, saved)
end
local function zone(localId, player)
    local filter = { localZoneId = localId }; if player ~= nil then filter.owner = player end
    local values = Global.Content.FindZones(filter)
    if #values ~= 1 then error('content_zone_missing:' .. localId) end
    return values[1].id
end
local function has(values, id)
    for _, value in ipairs(values or {}) do if value == id then return true end end; return false
end
local function choose(ctx, context, stage, ids, labels, prompt, minimum, kind)
    local options = {}; for _, id in ipairs(ids) do options[#options + 1] = { candidateId = id, label = labels[id] } end
    return tag(Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, options = ids,
        minSelections = minimum or 1, maxSelections = 1, interactionType = kind or 'lua.choice', promptKey = prompt }),
        { interaction = { prompt = Text.Ref(prompt), options = options } }), context, stage)
end

return function(ctx)
    local c = state(ctx); if c == nil then return {} end
    if c.stage == 'start' and ctx.eventType ~= 'PlayerCleanupStarted' or c.stage == 'stored' then
        if c.stage == 'stored' then
            if ctx.payload.outcome ~= 'completed' then return {} end
            c.taken = (c.taken or 0) + 1; c.owners = c.owners or {}; c.owners[#c.owners + 1] = c.selectedOwner
        end
        if (c.taken or 0) >= 2 then return {} end
        local ids, labels, seen = {}, {}, {}
        for _, supply in ipairs(Global.Content.FindZones({ templateId = 'core.zone.player_marker_supply' })) do
            if supply.owner ~= ctx.playerId and not has(c.owners, supply.owner) and not seen[supply.owner] then
                local items = Global.Content.FindInstances({ zoneId = supply.id, typeId = 'core.player_marker' })
                if #items > 0 then ids[#ids + 1] = items[1].id; labels[items[1].id] = Text.Ref('core:interaction.marker.owner', { player = Text.Player(items[1].ownerSubject) }); seen[supply.owner] = true end
            end
        end
        return { choose(ctx, c, 'store_selected', ids, labels, 'core:interaction.swire.take_marker', 0, 'lua.choice') }
    elseif c.stage == 'store_selected' then
        local id = ctx.payload.normalizedResult; if id == nil then return {} end
        c.selectedOwner = Global.Content.GetInstance(id).ownerSubject
        return { tag(Effect.StorePlayerMarker({ apiVersion = 1, player = ctx.playerId, markerItemId = id,
            cardItemId = c.source, localZoneId = 'markers' }), c, 'stored') }
    elseif c.stage == 'marker_selected' then
        local id = ctx.payload.normalizedResult; if id == nil then return {} end
        return { tag(Effect.ReturnPlayerMarker({ apiVersion = 1, player = ctx.playerId, markerItemId = id,
            cardItemId = c.source, localZoneId = 'markers' }), c, 'marker_returned') }
    elseif c.stage == 'marker_returned' then
        if ctx.payload.outcome ~= 'completed' then return {} end
        c.originalOwner = ctx.payload.normalizedResult.originalOwner
        return { tag(Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 6 }), c, 'gold_received') }
    elseif c.stage == 'gold_received' then
        local ids, labels = {}, {}
        for _, display in ipairs(Global.Content.FindZones({ templateId = 'core.zone.enterprise_display' })) do
            for _, item in ipairs(Global.Content.FindInstances({ zoneId = display.id, contentType = 'enterprise' })) do
                ids[#ids + 1] = item.id; labels[item.id] = Text.Ref('core:interaction.entity.item', { entity = Text.Item(item.id) })
            end
        end
        if #ids == 0 then return {} end
        return { choose(ctx, c, 'enterprise_selected', ids, labels, 'core:interaction.swire.upgrade.choose', 1, 'lua.choice') }
    elseif c.stage == 'enterprise_selected' then
        local id = ctx.payload.normalizedResult
        return { Effect.UpgradeEnterprise({ apiVersion = 1, player = ctx.playerId, enterpriseItemId = id, levels = 1 }),
            Effect.UpgradeEnterpriseForPlayer({ apiVersion = 1, player = ctx.playerId, targetPlayer = c.originalOwner, enterpriseItemId = id, levels = 1 }) }
    elseif c.stage ~= 'start' then return {} end
    local storage = Global.Content.FindZones({ parentId = c.source, localZoneId = 'markers' })
    if #storage ~= 1 then error('marker_storage_missing') end
    local ids, labels = {}, {}
    for _, item in ipairs(Global.Content.FindInstances({ zoneId = storage[1].id, typeId = 'core.player_marker' })) do
        ids[#ids + 1] = item.id; labels[item.id] = Text.Ref('core:interaction.marker.owner', { player = Text.Player(item.ownerSubject) })
    end
    return { choose(ctx, c, 'marker_selected', ids, labels, 'core:interaction.marker.return.choose', 0, 'lua.choice') }
end
