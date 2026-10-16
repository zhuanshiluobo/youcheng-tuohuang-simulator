return function(ctx)
    local effects = {}
    if ctx.payload.primaryInfluenceSlotId ~= nil and ctx.payload.primaryInfluenceSlotId ~= '' then
        table.insert(effects, Effect.PlaceInfluence({ executingPlayer = ctx.playerId, ownerSubject = ctx.playerId,
            influenceSource = 'event_card_primary', targetSlotId = ctx.payload.primaryInfluenceSlotId, causeKind = 'other_intrinsic_flow' }))
    end
    local option = tonumber(ctx.payload.optionIndex)
    if option == 0 then
        table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Originium', amount = 5 }))
    elseif option == 1 then
        table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'GoldVoucher', amount = 10 }))
    elseif option == 2 then
        local candidates = {}
        for _, item in ipairs(Global.Content.FindInstances()) do
            if item.contentType == 'enterprise' then
                table.insert(candidates, item.id)
            end
        end
        local right = { Effect.UpgradeEnterprise({ apiVersion = 1, player = ctx.playerId, enterpriseItemIds = candidates, levels = 1 }) }
        table.insert(effects, Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ recipient = ctx.playerId, resourceType = 'Iron', amount = 1 }) }, rightEffects = right }), { interaction = { prompt = Text.Ref('core:condition.enterprise.event.cave_ruins.resolve') } }))
    end
    return effects
end
