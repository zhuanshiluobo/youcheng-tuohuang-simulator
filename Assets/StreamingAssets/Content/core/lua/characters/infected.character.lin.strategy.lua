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

local prices = { originium = 3, originium_shard = 4, iron = 5, pure_originium = 15 }
local order = { 'originium', 'originium_shard', 'iron', 'pure_originium' }
local resourceFields = { originium = 'originium', originium_shard = 'originiumShard', iron = 'iron', pure_originium = 'pureOriginium' }
return function(ctx)
    local c = state(ctx); if c == nil then return {} end
    if c.stage == 'trade_committed' then
        if ctx.payload.normalizedResult.outcome == 'condition_met' then c.completed = (c.completed or 0) + 1 end
    elseif c.stage == 'trade_selected' then
        local selected = ctx.payload.normalizedResult
        if selected == nil or selected == 'stop' then return {} end
        local sell = string.sub(selected, 1, 5) == 'sell_'
        local resource = string.sub(selected, sell and 6 or 5)
        if prices[resource] == nil then error('trade_resource_invalid') end
        local payment = Effect.PayResource({ payer = ctx.playerId, resourceType = sell and resource or 'gold_voucher',
            amount = sell and 1 or prices[resource] - 2, allowSubstitution = not sell })
        local condition = Effect.Condition({ ruleCondition = not sell, leftEffects = { payment },
            rightEffects = { Effect.GainResource({ recipient = ctx.playerId, resourceType = sell and 'gold_voucher' or resource,
                amount = sell and prices[resource] or 1 }) } })
        if not sell then condition = Effect.WithPresentation(condition, { interaction = {
            prompt = Text.Ref('core:condition.character.lin.buy.' .. resource, { cost = prices[resource] - 2 }) } }) end
        return { tag(condition, c, 'trade_committed') }
    elseif c.stage ~= 'start' then return {} end
    if (c.completed or 0) >= 10 then return {} end
    local inventory = PlayerData.GetResources(ctx.playerId); local ids, labels = {}, {}
    for _, resource in ipairs(order) do
        if inventory[resourceFields[resource]] > 0 then
            local id = 'sell_' .. resource; ids[#ids + 1] = id
            labels[id] = Text.Ref('core:interaction.trade.sell.' .. resource, { price = prices[resource] })
        end
        if inventory.goldVoucher >= prices[resource] - 2 then
            local id = 'buy_' .. resource; ids[#ids + 1] = id
            labels[id] = Text.Ref('core:interaction.trade.buy.' .. resource, { cost = prices[resource] - 2 })
        end
    end
    ids[#ids + 1] = 'stop'; labels.stop = Text.Ref('core:interaction.trade.stop')
    return { choose(ctx, c, 'trade_selected', ids, labels, 'core:interaction.trade.choose', 0, 'effect.choice.execute') }
end
