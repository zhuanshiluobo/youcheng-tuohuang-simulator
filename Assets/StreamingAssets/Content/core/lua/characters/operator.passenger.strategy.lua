local completion = 'content.operator.passenger.strategy.step_completed'

local function find_player(key)
    for _, player in ipairs(GameData.GetPlayers()) do
        if player.playerId == key then return player end
    end
    error('passenger_target_player_invalid')
end

local function find_result_player(number)
    for _, player in ipairs(GameData.GetPlayers()) do
        if tonumber(string.sub(player.playerId, 2)) == number then return player end
    end
    error('passenger_loss_target_invalid')
end

local function basic_resource(resource)
    return resource == 'originium' or resource == 'originium-shard' or resource == 'iron'
end

-- 资源项直接包含原数量选择效果；数量仍是有正式来源和上限的数据选择。
local function resource_choice(ctx, target)
    local branches, labels = {}, {}
    local keys = { originium = 'originium', ['originium-shard'] = 'originium_shard', iron = 'iron' }
    for _, resource in ipairs({ 'originium', 'originium-shard', 'iron' }) do
        branches[#branches + 1] = { id = resource, effects = {
            Effect.Choice({ player = ctx.playerId,
                amountRange = { minAmount = 0, maxAmount = 4, sourcePlayer = target.playerId, resourceType = resource },
                resultContext = { stage = 'amount', targetPlayer = target.playerId, resourceType = resource },
                completionHandlerId = completion })
        } }
        labels[#labels + 1] = { candidateId = resource, label = Text.Ref('core:interaction.resource.' .. keys[resource]) }
    end
    return Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, branches = branches,
        interactionType = 'effect.choice.execute', promptKey = 'effect.resource.choose_type' }),
        { interaction = { options = labels } })
end

return function(ctx)
    if ctx.eventType ~= 'EffectCompleted' then
        local opponents = {}
        for _, player in ipairs(GameData.GetPlayers()) do
            if player.playerId ~= ctx.playerId then
                opponents[#opponents + 1] = 'player:' .. string.sub(player.playerId, 2)
            end
        end
        return { Effect.Choice({ player = ctx.playerId, options = opponents,
            promptKey = 'effect.player.choose_target', resultContext = { stage = 'target' } }) }
    end
    if ctx.payload.outcome ~= 'completed' then return {} end
    local result = ctx.payload.normalizedResult
    if ctx.payload.effectTypeId == 'effect.resource.lose' then
        local target = find_result_player(result.targetPlayer)
        if target.playerId == ctx.playerId then error('passenger_loss_target_invalid') end
        local rewards = {}
        if result.amount > 0 then
            rewards[#rewards + 1] = Effect.GainResource({ recipient = ctx.playerId,
                resourceType = result.resourceType, amount = result.amount })
        end
        rewards[#rewards + 1] = Effect.GainScore({ recipient = ctx.playerId, amount = 1 })
        rewards[#rewards + 1] = Effect.GainScore({ recipient = target, amount = 1 })
        return rewards
    end
    if ctx.payload.effectTypeId ~= 'effect.flow.choice' then error('passenger_step_effect_invalid') end
    local context = result.context
    if context.stage == 'target' then
        local key = nil
        for _, player in ipairs(GameData.GetPlayers()) do
            if 'player:' .. string.sub(player.playerId, 2) == result.value then key = player.playerId end
        end
        local target = find_player(key)
        if target.playerId == ctx.playerId then error('passenger_target_player_invalid') end
        return { resource_choice(ctx, target) }
    end
    local target = find_player(context.targetPlayer)
    if target.playerId == ctx.playerId then error('passenger_target_player_invalid') end
    if context.stage == 'amount' then
        if not basic_resource(context.resourceType) then error('passenger_resource_selection_invalid') end
        return { Effect.LoseResource({ apiVersion = 1, player = ctx.playerId, targetPlayer = target,
            resourceType = context.resourceType, amount = result.value, completionHandlerId = completion }) }
    end
    error('passenger_step_context_invalid')
end
