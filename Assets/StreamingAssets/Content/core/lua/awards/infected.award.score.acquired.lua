return function(ctx)
    local source, retired
    for _, zone in ipairs(Global.Content.FindZones()) do
        if zone.templateId == 'core.zone.player_rewards' and zone.owner == ctx.playerId then source = zone.id end
        if zone.templateId == 'core.zone.box_retired' then retired = zone.id end
    end
    local retire = Effect.MoveItem({ apiVersion = 4, player = ctx.playerId,operation='transfer', selector={mode='explicit',itemIds={ctx.payload.rewardItemId},expectedSource={ zoneId = source }}, destination = { zoneId = retired }, insertion = { kind = 'unordered' },
        operationProfileId = 'core.reward_card_transfer', requiredCount = 1 })
    return { Effect.GainScore({ recipient = ctx.playerId, amount = 1 }), retire }
end
