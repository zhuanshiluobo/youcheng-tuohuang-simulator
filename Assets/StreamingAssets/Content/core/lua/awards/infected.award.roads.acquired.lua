return function(ctx)
    local source, retired
    for _, zone in ipairs(Global.Content.FindZones()) do
        if zone.templateId == 'core.zone.player_awards' and zone.owner == ctx.playerId then source = zone.id end
        if zone.templateId == 'core.zone.box_retired' then retired = zone.id end
    end
    local retire = Effect.MoveItem({ apiVersion = 1, player = ctx.playerId, itemRefs = { { itemId = ctx.payload.rewardItemId } },
        expectedSource = { zoneId = source }, destination = { zoneId = retired }, insertion = { kind = 'unordered' },
        operationProfileId = 'core.reward_card_transfer', requiredCount = 1 })
    return { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 20 }),
        Effect.Repeat({ apiVersion = 1, count = 2, effects = { Flow.ClearRouteAndPlaceRoad({ apiVersion = 1, player = ctx.playerId, scoreRemovedPlayers = 0, scoreExecutor = 0 }) } }), retire }
end
