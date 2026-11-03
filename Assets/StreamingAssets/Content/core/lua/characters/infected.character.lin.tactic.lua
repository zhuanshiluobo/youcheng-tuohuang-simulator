-- 初始数量由真实效果分支选择；权威移动复验源区、库存和载物权限后足量提交。
local function initial_marker_inventory(ctx, card)
    local carriers = Global.Content.FindInstances({ id = card })
    if #carriers ~= 1 or carriers[1].controller ~= ctx.playerId then error('initial_marker_carrier_invalid') end
    local supplies = Global.Content.FindZones({ templateId = 'core.zone.player_supply', owner = ctx.playerId })
    local storage = Global.Content.FindZones({ parentId = card, localZoneId = 'markers' })
    if #supplies ~= 1 or #storage ~= 1 then error('initial_marker_zone_invalid') end
    local ids, seen = {}, {}
    for _, item in ipairs(Global.Content.FindInstances({ zoneId = supplies[1].id, typeId = 'core.player_marker' })) do
        if item.ownerSubject == ctx.playerId then
            if type(item.id) ~= 'string' or item.id == '' or seen[item.id] then error('initial_marker_inventory_invalid') end
            seen[item.id] = true
            ids[#ids + 1] = item.id
        end
    end
    table.sort(ids)
    return ids, supplies[1].id, storage[1].id
end

local function initial_marker_choice(ctx, card)
    local stock, supply, storage = initial_marker_inventory(ctx, card)
    local maximum = #stock
    if maximum > 2 then maximum = 2 end
    if maximum == 0 then return {} end
    local branches, options = {}, {}
    for count = 1, maximum do
        local id, ids = tostring(count), {}
        for i = 1, count do ids[#ids + 1] = stock[i] end
        branches[#branches + 1] = { id = id, effects = { Effect.MoveItem({ apiVersion = 3, player = ctx.playerId,
            itemIds = ids, expectedSource = { zoneId = supply }, destination = { zoneId = storage },
            insertion = { kind = 'unordered' }, operationProfileId = 'core.cube_storage', requiredCount = count }) } }
        options[#options + 1] = { candidateId = id, label = Text.Ref('core:interaction.lin.initial_markers.count', { count = count }) }
    end
    -- 放弃只取消本次可选存放；每个正数分支都是一笔足量真实转移，不使用空效果或值分派回调。
    return { Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, branches = branches,
        minSelections = 0, maxSelections = 1, interactionType = 'effect.choice.execute',
        promptKey = 'core:interaction.lin.initial_markers.prompt' }),
        { interaction = { prompt = Text.Ref('core:interaction.lin.initial_markers.prompt', { count = maximum }), options = options } }) }
end

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
        return initial_marker_choice(ctx, c.source)
    end
    if c.stage == 'marker_returned' then
        if ctx.payload.outcome ~= 'completed' then return {} end
        local result = ctx.payload.normalizedResult
        if type(result) ~= 'table' or result.outcome ~= 'completed' or result.changed ~= true or result.movedCount ~= 1 or
            type(result.movedItemIds) ~= 'table' or #result.movedItemIds ~= 1 or result.movedItemIds[1] ~= c.returnedMarkerId then
            error('lin_marker_return_result_invalid')
        end
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
    local markers, branches, options = {}, {}, {}
    for _, item in ipairs(Global.Content.FindInstances({ zoneId = storage[1].id, typeId = 'core.player_marker' })) do markers[#markers + 1] = item end
    table.sort(markers, function(a, b) return a.id < b.id end)
    for _, item in ipairs(markers) do
        local context = copy(c); context.returnedMarkerId = item.id
        branches[#branches + 1] = { id = item.id, effects = { tag(Effect.MoveItem({ apiVersion = 4, operation = 'transfer', player = ctx.playerId,
            selector = { mode = 'explicit', itemIds = { item.id }, expectedSource = { zoneId = storage[1].id } },
            destination = { kind = 'original_supply' }, insertion = { kind = 'unordered' }, requiredCount = 1,
            operationProfileId = 'core.player_marker_storage', carrierPolicy = 'preserve' }), context, 'marker_returned') } }
        options[#options + 1] = { candidateId = item.id, label = Text.Ref('core:interaction.marker.owner', { player = Text.Player(item.ownerSubject) }) }
    end
    if #branches == 0 then return {} end
    return { Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, branches = branches,
        minSelections = 0, maxSelections = 1, interactionType = 'effect.choice.execute', promptKey = 'core:interaction.marker.return.choose' }),
        { interaction = { prompt = Text.Ref('core:interaction.marker.return.choose'), options = options } }) }
end
