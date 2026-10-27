local function demand(player)
    return {apiVersion = 2, player = player, resourceType = 'total',
        allowedResourceTypes = {'Originium', 'Iron'}, amount = 3,
        cost = {OriginiumShard = 1}}
end

return function(ctx)
    if ctx.payload.queryKind == 'resource_payment_demand.v1' then
        local items = {}
        if ctx.payload.facts.actionId == 'special_action.city_style.composite_power_system' then
            items[1] = demand(ctx.playerId)
        end
        return {queryId = ctx.payload.queryId, basisDigest = ctx.payload.basisDigest, items = items}
    end
    if ctx.eventType == 'EffectCompleted' then
        -- 条件、付款及放置的完成都不能冒充真实城市移动。
        if ctx.payload.effectTypeId ~= 'effect.city.move' or ctx.payload.outcome ~= 'completed' then return {} end
        local result = ctx.payload.normalizedResult
        if result == nil or result.traversedRouteSlots == nil then return {} end
        local slots = result.traversedRouteSlots
        if #slots == 0 then return {} end
        return { Effect.PlaceInfluence({ executingPlayer = ctx.playerId,
            influenceSource = 'city_style.composite.route', ownerSubject = ctx.playerId,
            causeKind = 'place_road', candidateScope = slots }) }
    end
    local move = Effect.MoveCity({player = ctx.playerId,
        waiveBaseCost = true, consumeMainAction = false})
    move.completionHandlerId = 'city-styles.composite.after_move'
    local payment = demand(ctx.playerId)
    return {Effect.WithPresentation(Effect.Condition({decisionPlayer = ctx.playerId, ruleCondition = true,
        leftEffects = {Effect.PayResource(payment)},
        rightEffects = {move}}), {interaction = {prompt = Text.Ref('core:condition.city_style.composite_power_system',
            {shards = payment.cost.OriginiumShard, flexibleCost = payment.amount})}})}
end
