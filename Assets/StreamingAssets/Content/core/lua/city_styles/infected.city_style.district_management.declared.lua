return function(ctx)
    return { Effect.WithPresentation(Effect.Choice({ interactionType = 'effect.choice.execute', player = ctx.playerId, promptKey = 'city_style.return_level_two_marker', branches = {
        { id = 'return', effects = { Effect.WithPresentation(Effect.Condition({ leftEffects = {
            Effect.ReturnCityStyleMarker({ apiVersion = 1, player = ctx.playerId })
        }, rightEffects = { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'pure_originium', amount = 1 }) } }), { interaction = { prompt = Text.Ref('core:condition.infected.city_style.district_management.declared') } }) } },
        { id = 'keep', effects = {} }
    } }), { interaction = { prompt = Text.Ref('core:interaction.district_management.choose'), options = { { candidateId = 'return', label = Text.Ref('core:condition.infected.city_style.district_management.declared') }, { candidateId = 'keep', label = Text.Ref('core:interaction.district_management.keep') } } } }) }
end
