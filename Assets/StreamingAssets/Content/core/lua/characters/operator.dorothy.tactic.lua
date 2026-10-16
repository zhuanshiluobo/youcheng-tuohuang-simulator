return function(ctx)
    local card = ctx.payload.cardInstanceRef
    if ctx.eventType == 'InfluenceRemoved' or ctx.eventType == 'InfluenceReplaced' then
        return { Effect.WithPresentation(Effect.Choice({ interactionType = 'effect.choice.execute', player = ctx.playerId, promptKey = 'operator.dorothy.store', branches = {
            { id = 'store', effects = { Effect.StoreRemovedInfluence({ apiVersion = 1, player = ctx.playerId, cardInstanceId = card, localZoneId = 'markers', influenceItemId = ctx.payload.influenceId }) } },
            { id = 'decline', effects = {} } } }), { interaction = { prompt = Text.Ref('core:interaction.dorothy.store.prompt'), options = { { candidateId = 'store', label = Text.Ref('core:interaction.dorothy.store') }, { candidateId = 'decline', label = Text.Ref('core:interaction.dorothy.decline') } } } }) }
    end
    if ctx.eventType == 'PlayerCleanupStarted' then
        return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayCardMarkers({ apiVersion = 1, player = ctx.playerId, cardInstanceId = card, localZoneId = 'markers', count = 2 }) }, rightEffects = { Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'player_supply', ownerSubject = ctx.playerId, causeKind = 'other_intrinsic_flow' }) } }), { interaction = { prompt = Text.Ref('core:condition.operator.dorothy.tactic') } }) }
    end
    return { Effect.LoadCardMarkers({ apiVersion = 1, player = ctx.playerId, cardInstanceId = card, localZoneId = 'markers', maxCount = 4 }) }
end
