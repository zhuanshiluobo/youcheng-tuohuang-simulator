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
    local c
    if ctx.eventType == 'EffectCompleted' then
        if ctx.payload.continuationContext == nil or ctx.payload.outcome ~= 'completed' then return {} end
        c = copy(ctx.payload.continuationContext)
        local receipt = ctx.payload.normalizedResult
        c.revenue = c.revenue + receipt.revenue; c.soldCount = c.soldCount + receipt.soldCount; c.batches = c.batches + 1
        if receipt.finished then return {} end
    else c = { stage = 'sale', revenue = 0, soldCount = 0, batches = 0 } end
    return { Effect.WithContext(Effect.WithPresentation(Effect.SellResource({ apiVersion = 2, player = ctx.playerId, saleMode = 'batch',
        pricePolicy = 'core.resource_sale', interactionType = 'lua.choice', promptKey = 'effect.resource.sell' }), { interaction = { prompt = Text.Ref('core:interaction.sale.batch', { soldCount = c.soldCount, revenue = c.revenue }) } }), c) }
end
