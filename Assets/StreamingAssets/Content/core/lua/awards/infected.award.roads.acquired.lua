return function(ctx)
    local source, retired
    for _, zone in ipairs(Global.Content.FindZones({ templateId = 'core.zone.player_rewards', owner = ctx.playerId })) do
        source = zone.id
    end
    for _, zone in ipairs(Global.Content.FindZones({ templateId = 'core.zone.box_retired' })) do
        retired = zone.id
    end
    local retire = Effect.MoveItem({ apiVersion = 4, player = ctx.playerId,operation='transfer', selector={mode='explicit',itemIds={ctx.payload.rewardItemId},expectedSource={ zoneId = source }}, destination = { zoneId = retired }, insertion = { kind = 'unordered' },
        operationProfileId = 'core.reward_card_transfer', requiredCount = 1 })
    return { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 20 }),
        Flow.ClearRouteAndPlaceRoad({ apiVersion = 1, player = ctx.playerId, scoreRemovedPlayers = 0, scoreExecutor = 0 }),
        Flow.ClearRouteAndPlaceRoad({ apiVersion = 1, player = ctx.playerId, scoreRemovedPlayers = 0, scoreExecutor = 0 }), retire }
end
