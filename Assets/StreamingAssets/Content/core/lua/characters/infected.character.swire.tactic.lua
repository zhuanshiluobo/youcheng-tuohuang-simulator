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
local function has(values, id)
    for _, value in ipairs(values or {}) do if value == id then return true end end; return false
end
local function choose(ctx, context, stage, ids, labels, prompt, minimum, maximum)
    local options = {}; for _, id in ipairs(ids) do options[#options + 1] = { candidateId = id, label = labels[id] } end
    return tag(Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, options = ids,
        minSelections = minimum or 1, maxSelections = maximum or 1, interactionType = 'lua.choice', promptKey = prompt }),
        { interaction = { prompt = Text.Ref(prompt), options = options } }), context, stage)
end
local function store_next(ctx, c)
    while c.nextOpponent <= #c.opponents do
        local owner = c.opponents[c.nextOpponent]; c.nextOpponent = c.nextOpponent + 1
        local supplies = Global.Content.FindZones({ templateId = 'core.zone.player_supply', owner = owner })
        if #supplies ~= 1 then error('swire_opponent_supply_missing') end
        local markers = {}
        for _, item in ipairs(Global.Content.FindInstances({ zoneId = supplies[1].id, typeId = 'core.player_marker' })) do
            if item.ownerSubject == owner then markers[#markers + 1] = item end
        end
        -- 选择的是对手；其供应已空就跳过，不补选、不再选择标记实例。
        table.sort(markers, function(a, b) return a.id < b.id end)
        if #markers > 0 then
            return { tag(Effect.StorePlayerMarker({ apiVersion = 1, player = ctx.playerId, markerItemId = markers[1].id,
                cardItemId = c.source, localZoneId = 'markers' }), c, 'stored') }
        end
    end
    return {}
end
local function return_marker(ctx, c, id)
    return { tag(Effect.ReturnPlayerMarker({ apiVersion = 1, player = ctx.playerId, markerItemId = id,
        cardItemId = c.source, localZoneId = 'markers' }), c, 'marker_returned') }
end

return function(ctx)
    local c = state(ctx); if c == nil then return {} end
    if c.stage == 'start' and ctx.eventType ~= 'PlayerCleanupStarted' then
        local ids, labels = {}, {}
        for _, player in ipairs(GameData.GetPlayers()) do
            if player.playerId ~= ctx.playerId then
                ids[#ids + 1] = player.playerId
                labels[player.playerId] = Text.Ref('core:interaction.player.name', { player = Text.Player(player.playerId) })
            end
        end
        if #ids == 0 then return {} end
        c.opponentCandidates = ids
        local maximum = #ids; if maximum > 2 then maximum = 2 end
        return { choose(ctx, c, 'opponents_selected', ids, labels, 'core:interaction.swire.take_marker', 0, maximum) }
    elseif c.stage == 'opponents_selected' then
        local selected = ctx.payload.normalizedResult; if selected == nil then return {} end
        if type(selected) == 'string' then selected = { selected } end
        if type(selected) ~= 'table' or #selected > 2 then error('swire_opponent_selection_invalid') end
        local seen = {}
        for _, id in ipairs(selected) do
            if type(id) ~= 'string' or id == ctx.playerId or seen[id] or not has(c.opponentCandidates, id) then
                error('swire_opponent_selection_invalid')
            end
            seen[id] = true
        end
        c.opponents = selected; c.nextOpponent = 1
        return store_next(ctx, c)
    elseif c.stage == 'stored' then
        return store_next(ctx, c)
    elseif c.stage == 'marker_selected' then
        local id = ctx.payload.normalizedResult; if id == nil then return {} end
        return return_marker(ctx, c, id)
    elseif c.stage == 'marker_returned' then
        if ctx.payload.outcome ~= 'completed' then return {} end
        c.originalOwner = ctx.payload.normalizedResult.originalOwner
        return { tag(Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 6 }), c, 'gold_received') }
    elseif c.stage == 'gold_received' then
        local ids = {}
        for _, display in ipairs(Global.Content.FindZones({ templateId = 'core.zone.enterprise_display' })) do
            for _, item in ipairs(Global.Content.FindInstances({ zoneId = display.id, contentType = 'enterprise' })) do
                ids[#ids + 1] = item.id
            end
        end
        if #ids == 0 then return {} end
        c.upgradeCandidates = ids
        return { tag(Effect.WithPresentation(Effect.UpgradeEnterprise({ apiVersion = 1, player = ctx.playerId,
            enterpriseItemIds = ids, levels = 1 }), { interaction = { prompt = Text.Ref('core:interaction.swire.upgrade.choose') } }), c, 'self_upgraded') }
    elseif c.stage == 'self_upgraded' then
        local result = ctx.payload.normalizedResult
        if type(result) ~= 'table' or not has(c.upgradeCandidates, result.selectedEnterpriseItemId) then
            error('swire_upgrade_selection_missing')
        end
        return { Effect.UpgradeEnterpriseForPlayer({ apiVersion = 1, player = ctx.playerId, targetPlayer = c.originalOwner,
            enterpriseItemId = result.selectedEnterpriseItemId, levels = 1 }) }
    elseif c.stage ~= 'start' then return {} end
    local storage = Global.Content.FindZones({ parentId = c.source, localZoneId = 'markers' })
    if #storage ~= 1 then error('marker_storage_missing') end
    local ids, labels, owners = {}, {}, {}
    local markers = {}
    for _, item in ipairs(Global.Content.FindInstances({ zoneId = storage[1].id, typeId = 'core.player_marker' })) do markers[#markers + 1] = item end
    table.sort(markers, function(a, b) return a.id < b.id end)
    for _, item in ipairs(markers) do
        ids[#ids + 1] = item.id; labels[item.id] = Text.Ref('core:interaction.marker.owner', { player = Text.Player(item.ownerSubject) })
        if not has(owners, item.ownerSubject) then owners[#owners + 1] = item.ownerSubject end
    end
    if #ids == 0 then return {} end
    -- 本牌只容纳玩家标记，原主决定返供和升级受益人；相同原主无须按实例ID另选。
    if #owners == 1 then return return_marker(ctx, c, ids[1]) end
    return { choose(ctx, c, 'marker_selected', ids, labels, 'core:interaction.marker.return.choose', 0, 1) }
end
