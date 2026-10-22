return function(ctx)
    local effects = {}
    if ctx.payload.primaryInfluenceSlotId ~= nil and ctx.payload.primaryInfluenceSlotId ~= '' then
        table.insert(effects, Effect.PlaceInfluence({ executingPlayer = ctx.playerId, ownerSubject = ctx.playerId,
            influenceSource = 'event_card_primary', targetSlotId = ctx.payload.primaryInfluenceSlotId, causeKind = 'other_intrinsic_flow' }))
    end
    local option = tonumber(ctx.payload.optionIndex)
    if option == 0 then
        table.insert(effects, Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.LoseScore({ player = ctx.playerId, amount = 1 }) },
            rightEffects = { Flow.UpgradeThenActivateEnterpriseSpecial({ apiVersion = 1, player = ctx.playerId, levels = 1 }) } }), { interaction = { prompt = Text.Ref('core:condition.enterprise.event.collection_platform.resolve') } }))
    elseif option == 1 then
        table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Originium', amount = 4 }))
        table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'OriginiumShard', amount = 3 }))
    elseif option == 2 then
        table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Iron', amount = 4 }))
        table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'GoldVoucher', amount = 5 }))
    end
    return effects
end
