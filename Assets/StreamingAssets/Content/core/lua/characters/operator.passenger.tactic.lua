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
    if maximum > 5 then maximum = 5 end
    if maximum == 0 then return {} end
    local branches, options = {}, {}
    for count = 1, maximum do
        local id, ids = tostring(count), {}
        for i = 1, count do ids[#ids + 1] = stock[i] end
        branches[#branches + 1] = { id = id, effects = { Effect.MoveItem({ apiVersion = 3, player = ctx.playerId,
            itemIds = ids, expectedSource = { zoneId = supply }, destination = { zoneId = storage },
            insertion = { kind = 'unordered' }, operationProfileId = 'core.cube_storage', requiredCount = count }) } }
        options[#options + 1] = { candidateId = id, label = Text.Ref('core:interaction.passenger.initial_markers.count', { count = count }) }
    end
    -- 放弃只取消本次可选存放；每个正数分支都是一笔足量真实转移，不使用空效果或值分派回调。
    return { Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, branches = branches,
        minSelections = 0, maxSelections = 1, interactionType = 'effect.choice.execute',
        promptKey = 'core:interaction.passenger.initial_markers.prompt' }),
        { interaction = { prompt = Text.Ref('core:interaction.passenger.initial_markers.prompt', { count = maximum }), options = options } }) }
end

return function(ctx)
    if ctx.eventType == 'EffectCompleted' then return {} end
    return initial_marker_choice(ctx, ctx.payload.cardInstanceRef)
end
