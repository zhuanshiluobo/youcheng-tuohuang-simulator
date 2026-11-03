-- API-39：分配队列只决定尝试顺序；成功额度由实际交易 Condition 的结果扣除。
local function copy(value)
    if type(value) ~= 'table' then return value end
    local result = {}; for key, item in pairs(value) do result[key] = copy(item) end; return result
end
local function tag(effect, context, stage)
    local saved = copy(context); saved.stage = stage; return Effect.WithContext(effect, saved)
end
local prices = { originium = 3, originium_shard = 4, iron = 5, pure_originium = 15 }
local order = { 'originium', 'originium_shard', 'iron', 'pure_originium' }

return function(ctx)
    local c
    if ctx.eventType == 'EffectCompleted' then
        if ctx.payload.continuationContext == nil or ctx.payload.outcome ~= 'completed' then return {} end
        c = copy(ctx.payload.continuationContext)
        if c.contract ~= 'lin_allocation_v1' then error('lin_allocation_context_invalid') end
        -- 保留每次交易的既有私有完成事实；只有外层分配完成才统计及续接。
        if c.stage == 'trade_committed' then return {} end
        if c.stage ~= 'allocation_committed' then error('lin_allocation_stage_invalid') end
        local r = ctx.payload.allocationResult
        if r == nil or r.version ~= 'allocation_result_v1' or r.ownerEffectId ~= ctx.payload.effectId or
            r.resultDigest ~= ctx.payload.normalizedResult.resultDigest then error('lin_allocation_result_invalid') end
        local successes = 0
        for _, execution in ipairs(r.executions) do
            local root = execution.roots[1]
            local result = root and root.normalizedResult
            if root and root.state == 'Completed' and result and
                (result.conditionMet == true or result.outcome == 'condition_met') then successes = successes + 1 end
        end
        c.completed = c.completed + successes
        if c.completed > 10 then error('lin_allocation_success_limit') end
        if r.submitKind == 'end_action' or c.completed == 10 then return {} end
    else
        local source = ctx.payload.cardInstanceRef or ctx.payload.sourceItemId or ctx.payload.enterpriseItemId
        if source == nil then error('content_source_missing') end
        c = { source = source, completed = 0, contract = 'lin_allocation_v1' }
    end
    local remaining = 10 - c.completed
    local options = {}
    for _, resource in ipairs(order) do
        for _, action in ipairs({ 'sell', 'buy' }) do
            local selling = action == 'sell'
            local price = prices[resource]
            local payment = Effect.PayResource({ payer = ctx.playerId,
                resourceType = selling and resource or 'gold_voucher', amount = selling and 1 or price - 2,
                allowSubstitution = not selling })
            local trade = Effect.Condition({ ruleCondition = not selling, leftEffects = { payment },
                rightEffects = { Effect.GainResource({ recipient = ctx.playerId,
                    resourceType = selling and 'gold_voucher' or resource, amount = selling and price or 1 }) } })
            if not selling then trade = Effect.WithPresentation(trade, { interaction = {
                prompt = Text.Ref('core:condition.character.lin.buy.' .. resource, { cost = price - 2 }) } }) end
            options[#options + 1] = { id = action .. '_' .. resource, effects = { tag(trade, c, 'trade_committed') },
                text = Text.Ref('core:interaction.trade.' .. action .. '.' .. resource,
                    selling and { price = price } or { cost = price - 2 }), maxExecutions = remaining }
        end
    end
    local allocation = Effect.AllocateEffects({ apiVersion = 1, player = ctx.playerId, options = options,
        totalExecutions = remaining, allowEndAction = true })
    allocation = Effect.WithPresentation(allocation, { interaction = {
        prompt = Text.Ref('core:interaction.trade.allocate', { remaining = remaining }) } })
    return { tag(allocation, c, 'allocation_committed') }
end
