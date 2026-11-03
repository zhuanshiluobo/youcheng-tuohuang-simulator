-- 规则阶段只存在权威Effect续接元数据中；不保留Lua VM或UI草稿。
local function copy(value)
    if type(value) ~= 'table' then return value end
    local result = {}; for key, item in pairs(value) do result[key] = copy(item) end; return result
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
