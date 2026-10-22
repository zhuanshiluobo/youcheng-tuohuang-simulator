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
    if c.stage == 'start' and ctx.eventType ~= 'PlayerCleanupStarted' then
        return { tag(Effect.LoadCardMarkers({ apiVersion = 1, player = ctx.playerId, cardInstanceId = c.source,
            localZoneId = 'markers', maxCount = 2 }), c, 'done') }
    end
    if c.stage == 'marker_selected' then
        local id = ctx.payload.normalizedResult; if id == nil then return {} end
        return { tag(Effect.ReturnPlayerMarker({ apiVersion = 1, player = ctx.playerId, cardItemId = c.source,
            markerItemId = id, localZoneId = 'markers' }), c, 'marker_returned') }
    elseif c.stage == 'marker_returned' then
        if ctx.payload.outcome ~= 'completed' then return {} end
        local ids, labels = {}, {}
        for _, player in ipairs(GameData.GetPlayers()) do
            if player.playerId ~= ctx.playerId and PlayerData.GetResources(player).goldVoucher >= 20 then
                ids[#ids + 1] = player.playerId
                labels[player.playerId] = Text.Ref('core:interaction.player.name', { player = Text.Player(player.playerId) })
            end
        end
        if #ids == 0 then return {} end
        return { choose(ctx, c, 'target_selected', ids, labels, 'core:interaction.lin.transfer.choose', 1, 'lua.choice') }
    elseif c.stage == 'target_selected' then
        local target = nil
        for _, player in ipairs(GameData.GetPlayers()) do
            if player.playerId == ctx.payload.normalizedResult and player.playerId ~= ctx.playerId
                and PlayerData.GetResources(player).goldVoucher >= 20 then target = player end
        end
        if target == nil then error('lin_transfer_target_invalid') end
        return { tag(Effect.Condition({ ruleCondition = false,
            leftEffects = { Effect.PayResource({ payer = target, resourceType = 'gold_voucher', amount = 10 }) },
            rightEffects = { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 10 }) } }), c, 'done') }
    elseif c.stage ~= 'start' then return {} end
    local storage = Global.Content.FindZones({ parentId = c.source, localZoneId = 'markers' })
    if #storage ~= 1 then error('marker_storage_missing') end
    local ids, labels = {}, {}
    for _, item in ipairs(Global.Content.FindInstances({ zoneId = storage[1].id, typeId = 'core.player_marker' })) do
        ids[#ids + 1] = item.id; labels[item.id] = Text.Ref('core:interaction.marker.owner', { player = Text.Player(item.ownerSubject) })
    end
    return { choose(ctx, c, 'marker_selected', ids, labels, 'core:interaction.marker.return.choose', 0, 'lua.choice') }
end
