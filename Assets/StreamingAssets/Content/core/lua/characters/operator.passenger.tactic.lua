-- 初始数量由Lua选择；提交后重读真实供应，逐枚交通用token移动校验。
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
    local stock = initial_marker_inventory(ctx, card)
    local maximum = #stock
    if maximum > 5 then maximum = 5 end
    if maximum == 0 then return {} end
    local ids, options = {}, {}
    for count = 0, maximum do
        local id = tostring(count)
        ids[#ids + 1] = id
        options[#options + 1] = { candidateId = id, label = count == 0
            and Text.Ref('core:interaction.passenger.initial_markers.none')
            or Text.Ref('core:interaction.passenger.initial_markers.count', { count = count }) }
    end
    return { Effect.WithContext(Effect.WithPresentation(Effect.Choice({ player = ctx.playerId,
        options = ids, minSelections = 1, maxSelections = 1, interactionType = 'lua.choice',
        promptKey = 'core:interaction.passenger.initial_markers.prompt' }),
        { interaction = { prompt = Text.Ref('core:interaction.passenger.initial_markers.prompt', { count = maximum }), options = options } }),
        { protocol = 1, stage = 'initial_markers_selected', source = card, maximum = maximum }) }
end

local function initial_markers_selected(ctx, saved)
    if saved.protocol ~= 1 or type(saved.source) ~= 'string' or saved.source == ''
        or type(saved.maximum) ~= 'number' or saved.maximum % 1 ~= 0 or saved.maximum < 1 or saved.maximum > 5 then
        error('initial_marker_context_invalid')
    end
    local raw = ctx.payload.normalizedResult
    local count = type(raw) == 'string' and tonumber(raw) or nil
    if count == nil or count % 1 ~= 0 or count < 0 or count > saved.maximum or tostring(count) ~= raw then
        error('initial_marker_count_invalid')
    end
    local stock, supply, storage = initial_marker_inventory(ctx, saved.source)
    if count == 0 then return {} end
    local effects = {}
    for i = 1, count do
        local ids = {}
        if stock[i] ~= nil then ids[1] = stock[i] end
        -- 缺少实例时保留空列表，让这一枚移动失败；不虚构ID或撤回其它移动。
        effects[#effects + 1] = Effect.MoveItem({ apiVersion = 3, player = ctx.playerId, itemIds = ids,
            expectedSource = { zoneId = supply }, destination = { zoneId = storage },
            insertion = { kind = 'unordered' }, operationProfileId = 'core.cube_storage', requiredCount = 1 })
    end
    return effects
end

return function(ctx)
    if ctx.eventType == 'EffectCompleted' then
        local saved = ctx.payload.continuationContext
        if saved == nil or ctx.payload.outcome ~= 'completed' then return {} end
        if saved.stage ~= 'initial_markers_selected' then error('passenger_continuation_invalid') end
        return initial_markers_selected(ctx, saved)
    end
    return initial_marker_choice(ctx, ctx.payload.cardInstanceRef)
end
